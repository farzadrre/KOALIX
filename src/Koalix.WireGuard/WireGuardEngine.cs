using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.Tunneling;

namespace Koalix.WireGuard;

public sealed record WireGuardStats(long RxBytes, long TxBytes, DateTimeOffset? LastHandshake, string? Endpoint);

/// <summary>
/// Two genuine modes:
///  1. Full tunnel (no split policy): the official WireGuard for Windows tunnel service
///     (wireguard.exe /installtunnelservice), i.e. the kernel-mode WireGuardNT implementation.
///  2. Per-application routing: the official WireGuard protocol has no per-process routing, so the same .conf is converted
///     into a sing-box WireGuard endpoint behind the Wintun TUN inbound with process_path rules.
/// </summary>
public sealed class WireGuardEngine(EnginePaths paths, IProcessRunner runner) : SingBoxEngine(paths), IProcessHealthEngine
{
    private string? _serviceTunnel;
    private string? _serviceConf;
    private EngineProcess? _ws;
    private string? _wsConf;

    /// <summary>
    /// Opt-in (setting "wireguard.wiresock", default OFF): use the user's installed WireSock CLI instead of the built-in
    /// engine. WireSock routes per app itself (AllowedApps/DisallowedApps) without a TUN adapter and supports
    /// AmneziaWG-style obfuscation keys, which the built-in sing-box WireGuard ignores.
    /// </summary>
    public static bool UseWireSock { get; set; }
    public bool UsingWireSock => _ws is not null;
    public bool ProcessHealthOnly => _ws is not null;
    private static bool WireSockAvailable => UseWireSock && OperatingSystem.IsWindows() && EnginePaths.WireSockExe is not null;
    public override bool IsRunning => _ws?.IsRunning == true || _serviceTunnel is not null || base.IsRunning;
    public override IReadOnlyList<int> ProcessIds => _ws?.Pid is { } p ? [p] : base.ProcessIds;

    public override EngineKind Kind => EngineKind.WireGuard;
    public bool UsingOfficialService => _serviceTunnel is not null;

    /// <summary>UAPI pipe of the official tunnel service ("WireGuard\\&lt;tunnel&gt;") — kernel rx/tx counters without wg.exe (v0.4.11).</summary>
    public string? UapiPipeName => _serviceTunnel is { } n ? "WireGuard\\" + n : null;

    /// <summary>
    /// Full tunnel prefers the official WireGuard service only when it is installed and the user opted in
    /// (setting "wireguard.official"); otherwise the built-in sing-box WireGuard endpoint carries all traffic —
    /// no separate WireGuard installation is required.
    /// </summary>
    public static bool PreferOfficial { get; set; }
    /// <summary>
    /// MTU forced on the built-in WireGuard endpoint (setting "wireguard.mtu"; null = use the config's value).
    /// Default 1280: many Iranian/mobile/PPPoE paths drop the 1420-byte packets most configs specify, which looks like
    /// "connected, ping OK, but websites never load" (TCP handshakes pass, full-size data packets vanish).
    /// </summary>
    public static int? MtuOverride { get; set; } = 1280;
    public static int? ParseMtu(string? v) =>
        string.IsNullOrWhiteSpace(v) ? 1280 : v == "config" ? null : int.TryParse(v, out var m) && m is >= 576 and <= 1500 ? m : 1280;
    private static bool UseOfficial(SplitPolicy policy) =>
        policy.Mode == SplitMode.FullTunnel && PreferOfficial && OperatingSystem.IsWindows() && File.Exists(EnginePaths.WireGuardExe);

    protected override JsonObject BuildConfig(TunnelProfile profile, SplitPolicy policy, SingBoxBuildOptions o)
    {
        var cfg = WireGuardConfig.Parse(profile.Content, out var errors);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(", ", errors));
        var cidrs = cfg.PartialTunnelCidrs;
        var remoteDns = o.RemoteDns;
        if (cidrs is not null && !WireGuardConfig.IsCovered(remoteDns, cidrs))
            // The tunneled resolver must itself be reachable through the peer, otherwise every tunneled lookup fails.
            remoteDns = cfg.Dns.FirstOrDefault(d => WireGuardConfig.IsCovered(d, cidrs)) ?? remoteDns;
        // IPv6 only when the config can carry it AND the user confirmed the server routes it (setting "tunnel.ipv6").
        // Many servers hand out an IPv6 address without routing IPv6; browsers then stall on "network is unreachable".
        var ipv6 = cfg.CarriesIpv6 && o.TunnelSupportsIpv6;
        var endpoint = cfg.ToSingBoxEndpoint();
        if (MtuOverride is int mtu) endpoint["mtu"] = mtu;
        RaiseLog(LogCategory.WireGuard, $"WireGuard endpoint: mtu={endpoint["mtu"]}, ipv6={(ipv6 ? "tunnel" : "rejected")}, " +
            $"allowedIPs={(cidrs is null ? "all" : cidrs.Count + " ranges (others direct)")}, remoteDns={remoteDns}");
        return SingBoxConfigBuilder.Build(endpoint, true, policy,
            o with { TunnelSupportsIpv6 = ipv6, TunnelCidrs = cidrs, RemoteDns = remoteDns });
    }

    public override async Task<EngineValidation> ValidateAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        WireGuardConfig.Parse(profile.Content, out var errors);
        if (errors.Count > 0) return EngineValidation.Fail(errors.ToArray());
        if (WireSockAvailable)
            // WireSock cannot block apps; refuse instead of silently dropping the user's Block rules.
            return policy.Rules.Any(r => r.Action == AppAction.Block) ? EngineValidation.Fail("WG.Error.WireSockBlockRules") : EngineValidation.Success;
        if (UseOfficial(policy)) return EngineValidation.Success;
        return await base.ValidateAsync(profile, policy, ct);
    }

    public override async Task StartAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (WireSockAvailable) { await StartWireSockAsync(profile, policy, ct); return; }
        var parsed = WireGuardConfig.Parse(profile.Content, out _);
        if (parsed.HasObfuscation)
            RaiseLog(LogCategory.WireGuard, "Config has AmneziaWG/WireSock obfuscation keys (" + string.Join(",", parsed.ObfuscationKeys) +
                ") which the built-in engine ignores; enable WireSock in Settings if it is installed.");
        if (!UseOfficial(policy)) { await base.StartAsync(profile, policy, ct); return; }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await StartOfficialAsync(profile, ct);
    }

    [SupportedOSPlatform("windows")]
    private async Task StartOfficialAsync(TunnelProfile profile, CancellationToken ct)
    {
        // Tunnel name = file name; restricted to [A-Za-z0-9_=+.-] by WireGuard.
        var name = "koalix_" + new string(profile.Name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(20).ToArray());
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KOALIX", "wireguard");
        _serviceConf = Path.Combine(dir, name + ".conf");
        // The elevated helper creates the directory with an ACL limited to SYSTEM and Administrators (see SecureFile).
        // -EncodedCommand (UTF-16LE base64) keeps the script out of the argument validator's way ($ ; | * are rejected there);
        // the only variable part is our own ProgramData path, embedded as a single-quoted literal.
        var script = $"$d='{dir.Replace("'", "''")}'; New-Item -ItemType Directory -Force -Path $d | Out-Null; icacls $d /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null";
        var r = await runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))], elevated: true, ct: ct);
        if (!r.Success) throw new InvalidOperationException("Cannot prepare WireGuard config folder: " + r.StdErr);
        var tmp = Path.Combine(Paths.RuntimeDir, name + ".conf");
        await File.WriteAllTextAsync(tmp, profile.Content, ct);
        try
        {
            r = await runner.RunAsync("cmd", ["/c", "move", "/y", tmp, _serviceConf], elevated: true, ct: ct);
            if (!r.Success) throw new InvalidOperationException("Cannot store WireGuard config");
            r = await runner.RunAsync(EnginePaths.WireGuardExe, ["/installtunnelservice", _serviceConf], elevated: true, ct: ct);
            if (!r.Success) throw new InvalidOperationException("WireGuard tunnel service failed: " + r.StdErr);
            _serviceTunnel = name;
        }
        finally { TryDelete(tmp); }
    }

    private async Task StartWireSockAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (policy.Rules.Any(r => r.Action == AppAction.Block)) throw new InvalidOperationException("WG.Error.WireSockBlockRules");
        static string PathOf(AppRule r) => r.IncludeFolder || r.IsFolderRule ? r.FolderPrefix.TrimEnd('\\') + "\\" : r.ExecutablePath;
        IReadOnlyList<string>? allowed = null, disallowed = null;
        switch (policy.Mode)
        {
            case SplitMode.Include:
                var list = policy.Rules.Where(r => r.Action == AppAction.Tunnel).Select(PathOf).ToList();
                // KOALIX itself is NOT added: in the field WireSock did not carry KOALIX's own requests (probes timed out
                // while Chrome worked), so health is process-based in this mode.
                allowed = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                break;
            case SplitMode.Exclude:
                disallowed = policy.Rules.Where(r => r.Action == AppAction.Direct).Select(PathOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                break;
        }
        _wsConf = Path.Combine(Paths.RuntimeDir, $"ws-{Guid.NewGuid():N}.conf");
        File.WriteAllText(_wsConf, WireGuardConfig.ToWireSock(profile.Content, allowed, disallowed));
        RaiseLog(LogCategory.WireGuard, $"Using WireSock ({EnginePaths.WireSockExe}), mode={policy.Mode}, apps={(allowed ?? disallowed)?.Count ?? 0}");
        // WireSock keeps a driver-level session alive for a few seconds after a stop (or after an app crash), and a
        // second "run" then exits INSTANTLY with code 0 — seen in the field as "connected" followed by an immediate
        // "client exited (code 0)" and a health-check teardown. Kill any leftover instance before starting ours.
        try
        {
            var kill = await runner.RunAsync("taskkill", ["/f", "/im", "wiresock-client.exe", "/t"], elevated: true, ct: ct);
            if (kill.Success) { RaiseLog(LogCategory.WireGuard, "[WireSock] Stale instance terminated before start"); await Task.Delay(600, ct); }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* nothing stale — fine */ }
        _ws = new EngineProcess(LogCategory.WireGuard);
        _ws.Output += (c, l) => RaiseLog(c, "[WireSock] " + l);
        _ws.UnexpectedExit += code => { RaiseLog(LogCategory.WireGuard, $"[WireSock] client exited (code {code})"); RaiseUnexpectedExit(code); };
        _ws.Start(EnginePaths.WireSockExe!, ["run", "-config", _wsConf, "-log-level", "info"], Paths.RuntimeDir);
        await Task.Delay(1500, ct); // driver + handshake before the first health probe
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_ws is not null)
        {
            await _ws.StopAsync(TimeSpan.FromSeconds(6));
            await _ws.DisposeAsync();
            _ws = null;
            if (_wsConf is not null) { TryDelete(_wsConf); _wsConf = null; }
            return;
        }
        if (_serviceTunnel is not null)
        {
            await runner.RunAsync(EnginePaths.WireGuardExe, ["/uninstalltunnelservice", _serviceTunnel], elevated: true, ct: ct);
            if (_serviceConf is not null) await runner.RunAsync("cmd", ["/c", "del", "/f", "/q", _serviceConf], elevated: true, ct: ct);
            _serviceTunnel = null; _serviceConf = null;
            return;
        }
        await base.StopAsync(ct);
    }

    /// <summary>Reads transfer counters and last handshake from "wg show &lt;tunnel&gt; dump" (official tools).</summary>
    public async Task<WireGuardStats?> GetStatsAsync(CancellationToken ct)
    {
        if (_serviceTunnel is null || !File.Exists(EnginePaths.WgExe)) return null;
        var r = await runner.RunAsync(EnginePaths.WgExe, ["show", _serviceTunnel, "dump"], elevated: false, ct: ct);
        if (!r.Success) return null;
        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long rx = 0, tx = 0; DateTimeOffset? hs = null; string? ep = null;
        foreach (var l in lines.Skip(1)) // first line = interface (contains private key — never logged)
        {
            var f = l.Split('\t');
            if (f.Length < 7) continue;
            ep = f[2];
            if (long.TryParse(f[4], out var h) && h > 0) hs = DateTimeOffset.FromUnixTimeSeconds(h);
            rx += long.TryParse(f[5], out var a) ? a : 0; tx += long.TryParse(f[6], out var b) ? b : 0;
        }
        return new WireGuardStats(rx, tx, hs, ep);
    }
}
