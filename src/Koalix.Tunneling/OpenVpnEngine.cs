using System.IO;
using System.Net.Sockets;
using System.Text;
using Koalix.Core;

namespace Koalix.Tunneling;

/// <summary>
/// An engine that can name the LOCALIZED KEY of the exact reason its last run died (v0.4.20) — the connection state
/// machine surfaces it instead of a generic "engine exited" report. OpenVPN uses it for volume/expiry exhaustion:
/// panels reject the credentials when the account's quota or time is over, and the client only ever sees AUTH_FAILED.
/// </summary>
public interface IReportedFatalError
{
    /// <summary>Localization key of the fatal reason, or null while the engine does not know better than "exit code N".</summary>
    string? FatalErrorKey { get; }
}

/// <summary>
/// Runs an .ovpn profile with the OFFICIAL OpenVPN client (openvpn.exe, v0.4.15) — the only genuinely complete
/// OpenVPN implementation (TAP/TUN drivers, cipher negotiation, auth-user-pass, tls-auth/tls-crypt…). KOALIX does
/// not reimplement the protocol; it supervises the process, reads live byte counters and the REAL connection state
/// from the management interface, and stops it cleanly. Health is state-based (v0.4.17): the management interface
/// pushes &gt;STATE: CONNECTED only when the tunnel is truly up, so KOALIX can never again report "connected" while
/// openvpn.exe has already died (the v0.4.16 field bug — see the remarks in ConnectionManager.OnEngineExitAsync).
/// Per-application split tunneling is NOT possible with OpenVPN and is refused honestly (ConnectionManager).
/// The binary is expected from the official OpenVPN for Windows installation (openvpn.net) or dropped manually
/// under engines\openvpn\ — EngineManager refuses unverifiable downloads, so KOALIX never fetches it itself.
/// </summary>
public sealed class OpenVpnEngine : ITunnelEngine, IProcessHealthEngine, IReportedFatalError
{
    private readonly EngineProcess _proc = new(LogCategory.OpenVpn);
    private readonly EnginePaths _paths;
    private string? _confPath;
    private CancellationTokenSource? _mgmtCts;
    private Task? _mgmtTask;
    private long _totalUp, _totalDown;

    /// <summary>Set the moment the management stream reports AUTH_FAILED (v0.4.20): panels kill the credentials when
    /// the account's volume or time is exhausted — the user deserves the real reason, not "exit code 1".</summary>
    public string? FatalErrorKey { get; private set; }
    /// <summary>Management-reported connection state: -1 = nothing reported yet, 0 = down/reconnecting, 1 = CONNECTED.</summary>
    private int _state = -1;

    /* ── v0.4.18: KOALIX-managed DNS ─────────────────────────────────────────────────────────
       The field log showed openvpn.exe dying with "NETSH: command failed → Exiting due to fatal error":
       the server pushes two DNS servers, openvpn.exe configures them with `netsh interface ip set/add dns`,
       the SECOND netsh call fails on that system (WireSock/WinpkFilter/ZoneAlarm filter drivers sit on the
       adapter) and OpenVPN treats a failed DNS step as FATAL. Result: connect → die → reconnect → die …
       Fix: pushed DHCP DNS options are now IGNORED by openvpn.exe (pull-filter) and KOALIX sets them itself
       with one PowerShell call (Set-DnsClientServerAddress, a different, netsh-free code path) AFTER the
       tunnel is CONNECTED — and a failure there is a warning, never a fatal teardown. */
    private readonly object _dnsGate = new();
    private readonly List<string> _pushedDns = [];
    private int? _dnsAdapterIndex;
    private bool _dnsApplied;
    private int _dnsApplyFlight;

    public OpenVpnEngine(EnginePaths paths)
    {
        _paths = paths;
        // Wired ONCE, in the constructor (v0.4.17). StartAsync used to add another handler pair on every (re)start,
        // so after N restarts a single process exit raised N "client exited" events and N reconnect loops ran in
        // parallel — the field log showed thousands of duplicated lines and a rapid connect/disconnect flapping.
        _proc.RawLine += OnRawEngineLine;   // v0.4.18: pushed DNS servers are read here (unredacted, never logged)
        _proc.Output += (_, l) => RaiseLog("[OpenVPN] " + l);
        _proc.UnexpectedExit += code => { RaiseLog($"[OpenVPN] client exited (code {code})"); RaiseUnexpectedExit(code); };
    }

    /// <summary>Collects the DNS servers the server pushes (dhcp-option DNS x.x.x.x inside PUSH_REPLY) from the
    /// raw engine output. Addresses go into _pushedDns only — nothing raw is ever forwarded to the log.</summary>
    private void OnRawEngineLine(string line)
    {
        if (line.IndexOf("PUSH: Received control message:", StringComparison.Ordinal) < 0) return;
        var ips = new List<string>();
        var idx = 0;
        while ((idx = line.IndexOf("dhcp-option DNS ", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = idx + "dhcp-option DNS ".Length;
            var end = start;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '.' || line[end] == ':')) end++;
            var ip = line[start..end].Trim(',','\'', '"', ' ');
            if (System.Net.IPAddress.TryParse(ip, out _)) ips.Add(ip);
            idx = end;
        }
        if (ips.Count == 0) return;
        lock (_dnsGate)
        {
            foreach (var ip in ips)
                if (!_pushedDns.Contains(ip)) _pushedDns.Add(ip);
            RaiseLog($"[OpenVPN] server pushed {_pushedDns.Count} DNS server(s) — KOALIX applies them itself (netsh path bypassed)");
        }
    }

    public EngineKind Kind => EngineKind.OpenVpn;
    public bool ProcessHealthOnly => true;
    /// <summary>OpenVPN reports CONNECTED / RECONNECTING / EXITING through the management interface (v0.4.17).</summary>
    public bool HasConnectionStateSignal => true;
    /// <summary>&gt;STATE: CONNECTED → true; RECONNECTING/EXITING/AUTH_FAILED → false; nothing reported yet → null.</summary>
    public bool? ReportedConnected { get { var s = Volatile.Read(ref _state); return s < 0 ? null : s == 1; } }
    public bool SupportsProcessRouting(TunnelProfile profile) => false;
    public bool IsRunning => _proc.IsRunning;
    public IReadOnlyList<int> ProcessIds => _proc.Pid is { } p ? [p] : [];
    public int? HealthProxyPort => null;
    public (Uri Url, string Secret)? StatsApi => null;

    /// <summary>Cumulative session bytes from the management interface (totals, not per-second deltas).</summary>
    public (long Up, long Down) Totals => (_totalUp, _totalDown);
    /// <summary>Raised roughly every second with CUMULATIVE session totals (management &gt;BYTECOUNT:).</summary>
    public event Action<long, long>? ByteCounts;
    public event Action<LogCategory, string>? LogLine;
    public event Action<int>? UnexpectedExit;

    private void RaiseLog(string l) => LogLine?.Invoke(LogCategory.OpenVpn, l);
    private void RaiseUnexpectedExit(int code) => UnexpectedExit?.Invoke(code);

    /// <summary>The best openvpn.exe available: a manually dropped binary wins (allows newer builds), then the official install.</summary>
    private string? Binary
    {
        get
        {
            var dropped = Path.Combine(_paths.EnginesDir, "openvpn", "openvpn.exe");
            if (File.Exists(dropped)) return dropped;
            return EnginePaths.OpenVpnExe;
        }
    }

    public Task<EngineValidation> ValidateAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (Binary is null) return Task.FromResult(EngineValidation.Fail("Ovpn.Error.EngineMissing"));
        var cfg = OpenVpnConfig.Parse(profile.Content, out var errors);
        if (errors.Count > 0) return Task.FromResult(EngineValidation.Fail(errors.ToArray()));
        if (cfg.HasAuthUserPass && !cfg.HasInlineCredentials) return Task.FromResult(EngineValidation.Fail("Ovpn.Error.NeedCredentials"));
        if (cfg.IsPkcs12) return Task.FromResult(EngineValidation.Fail("Ovpn.Error.Pkcs12"));
        return Task.FromResult(EngineValidation.Success);
    }

    public Task StartAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (Binary is not { } exe) throw new InvalidOperationException("Ovpn.Error.EngineMissing");
        // v0.4.18 — the «select certificate» fix: a cert WITHOUT a key (or a key without a cert) kills openvpn.exe
        // at option parsing (“If you use one of --cert or --key, you must use them both”). Drop the unpaired half;
        // server-side auth then proceeds via user-pass / no client cert, exactly like OpenVPN Connect's Continue.
        var content = OpenVpnConfig.SanitizeClientCertPair(profile.Content, out var certNote);
        var cfg = OpenVpnConfig.Parse(content, out var errors);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(", ", errors));
        // v0.4.17: a previous crashed run may have left its temp config behind (the v0.4.16 crash loop littered the
        // runtime dir with ovpn-run-*.ovpn files) — remove it before writing the new one.
        if (_confPath is not null) { try { File.Delete(_confPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } _confPath = null; }
        _confPath = Path.Combine(_paths.RuntimeDir, $"ovpn-run-{Guid.NewGuid():N}.ovpn");
        File.WriteAllText(_confPath, BuildRuntimeConfig(content, cfg));
        Volatile.Write(ref _state, -1);
        _totalUp = _totalDown = 0;
        lock (_dnsGate) { _pushedDns.Clear(); }
        _dnsAdapterIndex = null; _dnsApplied = false; Volatile.Write(ref _dnsApplyFlight, 0);

        var mgmtPort = EnginePaths.FreeTcpPort();
        if (certNote.Length > 0) RaiseLog("[OpenVPN] " + certNote);
        RaiseLog($"OpenVPN: endpoint={cfg.DisplayEndpoint}, proto={cfg.Remotes[0].Proto}, " +
            $"device={cfg.Device ?? "tun"}, cipher={cfg.Cipher ?? "auto"}, binary={Path.GetFileName(Path.GetDirectoryName(exe))}");
        var args = new List<string>
        {
            "--config", _confPath,
            "--management", "127.0.0.1", mgmtPort.ToString(),  // OpenVPN LISTENS here; the reader connects as client (v0.4.16 fix)
            // v0.4.17: "--management-client-bytecount" REMOVED. It is NOT a command-line option in OpenVPN 2.x — the
            // field log showed openvpn.exe dying instantly with
            //   "Options error: Unrecognized option or missing or extra parameter(s) in [CMD-LINE]:1: management-client-bytecount (2.7.9)"
            // on EVERY start, which is what turned into the connect/disconnect flapping. Byte counts are requested
            // over the management socket itself ("bytecount 1") and connection states via "state on" — both sent by
            // StartManagementReader below, the documented management-interface way.
            "--auth-retry", "nointeract",           // wrong credentials FAIL instead of hanging the tunnel
        };
        if (!cfg.HasVerb) { args.Add("--verb"); args.Add("3"); } // readable connection log unless the config sets its own level
        _proc.Start(exe, args, _paths.RuntimeDir);
        StartManagementReader(mgmtPort);
        return Task.CompletedTask;
    }

    /// <summary>
    /// v0.4.18 — runtime-config hardening, appended INSIDE the .ovpn file (config-file parsing needs no shell
    /// quoting, and repeated starts stay idempotent because these lines are only added when the profile lacks them):
    /// ① <c>pull-filter ignore "dhcp-option DNS"</c> — openvpn.exe never configures DNS via netsh. On the field
    ///    system its second netsh call failed and OpenVPN exits FATAL (“NETSH: command failed”) — the direct cause
    ///    of the rapid connect/disconnect loop. KOALIX applies the pushed DNS itself once CONNECTED.
    /// ② <c>data-ciphers …</c> — a legacy <c>cipher AES-256-CBC</c> profile is invisible to OpenVPN 2.5+ negotiation
    ///    (“--cipher … missing in --data-ciphers (DEFAULT)”), so CBC-only servers could never connect. The profile
    ///    cipher is prepended to the modern defaults and kept as negotiation fallback.
    /// </summary>
    private static string BuildRuntimeConfig(string content, OpenVpnConfig cfg)
    {
        var sb = new System.Text.StringBuilder(content.TrimEnd());
        sb.AppendLine().AppendLine("# --- KOALIX runtime hardening (v0.4.18) ---");
        if (!cfg.HasPullFilter)
            sb.AppendLine("pull-filter ignore \"dhcp-option DNS\""); // DNS is KOALIX's job now — netsh can never fail fatally again
        if (!cfg.HasDataCiphers && !string.IsNullOrWhiteSpace(cfg.Cipher))
        {
            var c = cfg.Cipher.Trim();
            var modern = new[] { "AES-256-GCM", "AES-128-GCM", "CHACHA20-POLY1305" };
            var list = modern.Contains(c, StringComparer.OrdinalIgnoreCase)
                ? string.Join(":", modern)   // profile cipher is already modern — keep the stock list
                : string.Join(":", new[] { c }.Concat(modern)); // legacy cipher (CBC…) must be part of the negotiation
            sb.AppendLine($"data-ciphers {list}");
            sb.AppendLine($"data-ciphers-fallback {c}");
        }
        return sb.ToString();
    }

    /// <summary>Applies the server-pushed DNS servers to the OpenVPN adapter with ONE PowerShell call —
    /// netsh-free, best-effort, never fatal. Runs once per connection, guarded against overlapping runs.</summary>
    private void ApplyDnsAsync(string localIp)
    {
        if (System.Threading.Interlocked.Exchange(ref _dnsApplyFlight, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string[] servers;
                lock (_dnsGate) servers = [.. _pushedDns];
                if (servers.Length == 0) { RaiseLog("[OpenVPN] no DNS servers pushed — adapter DNS untouched"); return; }
                // The adapter may still be settling right after CONNECTED — a few short retries.
                for (var attempt = 0; attempt < 4 && !_dnsApplied; attempt++)
                {
                    if (attempt > 0) await Task.Delay(1500);
                    if (!IsRunning) return;
                    var adapter = FindAdapterByIp(localIp);
                    if (adapter is null) { RaiseLog("[OpenVPN] adapter with tunnel IP not visible yet — retrying"); continue; }
                    var index = adapter.GetIPProperties().GetIPv4Properties()?.Index;
                    if (index is not { } idx) { RaiseLog("[OpenVPN] adapter has no IPv4 index — DNS left untouched"); return; }
                    var list = string.Join(",", servers.Select(s => $"'{s}'"));
                    var (code, err) = RunPowerShell($"Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses {list}");
                    if (code == 0)
                    {
                        _dnsAdapterIndex = idx; _dnsApplied = true;
                        RaiseLog($"[OpenVPN] DNS set on adapter '{adapter.Name}' (index {idx}) — {servers.Length} server(s), netsh never involved");
                    }
                    else RaiseLog($"[OpenVPN] Set-DnsClientServerAddress failed (exit {code}) {Redactor.Redact(err)} — tunnel stays up, DNS falls back to the adapter default");
                }
            }
            catch (Exception ex) { RaiseLog("[OpenVPN] DNS apply: " + Redactor.Redact(ex.Message)); }
            finally { Volatile.Write(ref _dnsApplyFlight, 0); }
        });
    }

    /// <summary>The up interface whose unicast IP equals the tunnel-local IP from &gt;STATE: CONNECTED.</summary>
    private static System.Net.NetworkInformation.NetworkInterface? FindAdapterByIp(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var want)) return null;
        foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var a in n.GetIPProperties().UnicastAddresses)
                if (a.Address.Equals(want)) return n;
        }
        return null;
    }

    /// <summary>Runs one PowerShell command (the app already runs elevated; DNS tools need it). Returns (exitCode, stderr).</summary>
    private static (int Code, string Error) RunPowerShell(string command)
    {
        try
        {
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command {command}",
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            p.Start();
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, err.Result.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>Hands the adapter's DNS back to DHCP (best-effort) after a KOALIX-managed OpenVPN session.</summary>
    private void RestoreDns()
    {
        var idx = _dnsAdapterIndex;
        _dnsAdapterIndex = null;
        if (!_dnsApplied || idx is not { } i) { _dnsApplied = false; return; }
        _dnsApplied = false;
        try
        {
            var (code, err) = RunPowerShell($"Set-DnsClientServerAddress -InterfaceIndex {i} -ResetServerAddresses");
            RaiseLog(code == 0
                ? $"[OpenVPN] adapter DNS restored (index {i})"
                : $"[OpenVPN] DNS restore failed (exit {code}) {Redactor.Redact(err)} — harmless: the tunnel adapter is going away anyway");
        }
        catch (Exception ex) { RaiseLog("[OpenVPN] DNS restore: " + Redactor.Redact(ex.Message)); }
    }

    /// <summary>
    /// Management interface reader: connects with retries (the port opens a moment after start), then asks OpenVPN
    /// itself for the two push channels — "bytecount 1" → &gt;BYTECOUNT: once per second, "state on" → &gt;STATE: on
    /// every transition (CONNECTED, RECONNECTING, EXITING…). v0.4.17 fixes: lines are read with a real line reader
    /// (the old 512-char buffer split mid-line and silently dropped counters), and the engine-reported state is the
    /// source of truth for the ConnectionManager health check.
    /// </summary>
    private void StartManagementReader(int port)
    {
        _mgmtCts = new CancellationTokenSource();
        var ct = _mgmtCts.Token;
        _mgmtTask = Task.Run(async () =>
        {
            TcpClient? client = null;
            try
            {
                for (var i = 0; i < 80 && client is null; i++) // up to ~20 s for the management port
                {
                    if (ct.IsCancellationRequested) return;
                    try { client = new TcpClient(); await client.ConnectAsync("127.0.0.1", port, ct); }
                    catch (SocketException) { client!.Dispose(); client = null; await Task.Delay(250, ct); }
                }
                if (client is null) { RaiseLog("[OpenVPN] management port never opened — counters/state unavailable"); return; }
                RaiseLog("[OpenVPN] management interface connected");
                using var stream = client.GetStream();
                var cmds = Encoding.ASCII.GetBytes("bytecount 1\nstate on\n");
                await stream.WriteAsync(cmds, ct);
                await stream.FlushAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024, leaveOpen: true);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // server closed the socket
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal)) HandleByteCount(line[">BYTECOUNT:".Length..]);
                    else if (line.StartsWith(">STATE:", StringComparison.Ordinal)) HandleState(line[">STATE:".Length..]);
                    else if (line.StartsWith("AUTH_FAILED", StringComparison.Ordinal))
                    {
                        Volatile.Write(ref _state, 0);
                        HandleAuthFailed(line);
                    }
                    // ">INFO:" greeting and "SUCCESS:" command acknowledgements are intentionally quiet.
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RaiseLog("[OpenVPN] management reader: " + Redactor.Redact(ex.Message)); }
            finally { client?.Dispose(); }
        }, ct);
    }

    /// <summary>
    /// AUTH_FAILED from the panel (v0.4.20). Panels (x-ui / Marzban / V2Board…) reject the credentials when the
    /// account's volume or time ran out — many say so right in the message (…limit…/…expire…/…volume…/…data…).
    /// We pick the quota wording when we see it, and always tell the user the credential was rejected (which is
    /// what an exhausted account looks like from the client side) — instead of a faceless "exit code 1".
    /// </summary>
    private void HandleAuthFailed(string detail)
    {
        if (FatalErrorKey is not null) return; // first reason wins
        var d = detail.ToLowerInvariant();
        var quota = d.Contains("limit") || d.Contains("expire") || d.Contains("volume") || d.Contains("data") || d.Contains("quota") || d.Contains("traffic");
        FatalErrorKey = quota ? "Ovpn.Error.Quota" : "Ovpn.Error.Auth";
        RaiseLog("[OpenVPN] " + (quota ? "authentication rejected — account volume/expiry exhausted" : "authentication rejected by the server"));
    }

    /// <summary>&gt;STATE:&lt;unix-ts&gt;,&lt;state&gt;,&lt;message&gt;[,&lt;local-ip&gt;[,&lt;remote&gt;]] — e.g. 1730000000,CONNECTED,SUCCESS,10.8.0.2,1.2.3.4.</summary>
    private void HandleState(string payload)
    {
        var parts = payload.Split(',');
        if (parts.Length < 2) return;
        var state = parts[1].Trim().ToUpperInvariant();
        switch (state)
        {
            case "CONNECTED":
                Volatile.Write(ref _state, 1);
                RaiseLog("[OpenVPN] state: CONNECTED — tunnel is up");
                // v0.4.18: the tunnel-local IP arrives with the CONNECTED line — apply the pushed DNS now.
                if (parts.Length > 3 && parts[3].Trim().Length > 0) ApplyDnsAsync(parts[3].Trim());
                break;
            case "RECONNECTING" or "RECONNECT_WAIT" or "WAIT" or "CONNECTING" or "AUTH" or "GET_CONFIG" or "ASSIGN_IP" or "ADD_ROUTES":
                Volatile.Write(ref _state, 0);
                RaiseLog($"[OpenVPN] state: {state}");
                // v0.4.20: a RECONNECTING that carries an auth-failure detail is the panel rejecting the
                // credentials (volume/expiry exhausted) — same honest handling as the bare AUTH_FAILED line.
                if (state == "RECONNECTING" && payload.Contains("auth-failure", StringComparison.OrdinalIgnoreCase)) HandleAuthFailed(payload);
                break;
            case "EXITING" or "HALT" or "SIGHUP" or "SIGTERM" or "SIGINT":
                Volatile.Write(ref _state, 0);
                RaiseLog($"[OpenVPN] state: {state}");
                break;
            default:
                RaiseLog($"[OpenVPN] state: {state}");
                break;
        }
    }

    private void HandleByteCount(string payload)
    {
        var parts = payload.Replace(':', ' ').Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;
        if (long.TryParse(parts[0], out var down) && long.TryParse(parts[1], out var up))
        {
            _totalDown = Math.Max(0, down);
            _totalUp = Math.Max(0, up);
            ByteCounts?.Invoke(_totalUp, _totalDown);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _mgmtCts?.Cancel(); } catch { }
        try { if (_mgmtTask is { } t) await t.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
        RestoreDns(); // v0.4.18: hand the adapter's DNS back when KOALIX set it (best-effort, never blocks teardown)
        await _proc.StopAsync(TimeSpan.FromSeconds(8)); // Ctrl+C lets OpenVPN tear down routes and the adapter
        if (_confPath is not null) { try { File.Delete(_confPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } _confPath = null; }
        _totalUp = _totalDown = 0;
        Volatile.Write(ref _state, -1);
        lock (_dnsGate) { _pushedDns.Clear(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await _proc.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
