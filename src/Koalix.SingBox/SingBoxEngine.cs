using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SplitTunneling;
using Koalix.Tunneling;

namespace Koalix.SingBox;

/// <summary>
/// Runs sing-box with a TUN (Wintun) inbound. Configuration is validated by the real binary ("sing-box check") before
/// launch, written to the per-user runtime folder, and deleted after shutdown because it contains credentials.
/// </summary>
public class SingBoxEngine : ITunnelEngine
{
    protected readonly EnginePaths Paths;
    private readonly EngineProcess _proc = new(LogCategory.SingBox);
    private string? _configPath;
    private string _secret = "";

    public SingBoxEngine(EnginePaths paths)
    {
        Paths = paths;
        _proc.Output += (c, l) => LogLine?.Invoke(c, l);
        _proc.UnexpectedExit += c => UnexpectedExit?.Invoke(c);
    }

    public virtual EngineKind Kind => EngineKind.SingBox;
    public virtual bool SupportsProcessRouting(TunnelProfile profile) => true;
    public virtual bool IsRunning => _proc.IsRunning;
    public virtual IReadOnlyList<int> ProcessIds => _proc.Pid is { } p ? [p] : [];
    public int? HealthProxyPort { get; private set; }
    public (Uri Url, string Secret)? StatsApi { get; private set; }
    public string DirectDns { get; set; } = "1.1.1.1";
    public string RemoteDns { get; set; } = "1.1.1.1";
    public string LogLevel { get; set; } = "info";
    /// <summary>User override: the upstream server carries IPv6 (setting "tunnel.ipv6").</summary>
    public bool TunnelIpv6 { get; set; }
    /// <summary>TUN network stack (setting "tun.stack"): gvisor (default, most compatible), mixed, system.</summary>
    public string TunStack { get; set; } = "gvisor";
    /// <summary>strict_route (setting "tun.strict", DEFAULT ON since v0.4.30): the WFP rules that stop Windows'
    /// ordinary DNS requests from bypassing the TUN. Without them (the v0.4.15–0.4.29 default) Iranian ISPs answer
    /// the leaked lookups with the 10.10.34.x sinkhole while the tunnel itself stays healthy — «connected, the
    /// ip-check works, Google/YouTube never load». Rare filter-driver clashes can still be opted out per setting.</summary>
    public bool StrictRoute { get; set; } = true;
    /// <summary>v0.4.28 — connect-time DNS-poisoning defence (system resolver vs DoH). Setting "net.poisonGuard".</summary>
    public bool PoisonGuardEnabled { get; set; } = true;
    /// <summary>v0.4.28 — TUN MTU override; 0 keeps the engine default (9000). Setting "tun.mtu".</summary>
    public int TunMtu { get; set; }
    /// <summary>v0.4.28 — TLS ClientHello fragmentation (audit suggestion). Setting "net.fragment". Xray engine only.</summary>
    public bool FragmentEnabled { get; set; } = true;
    /// <summary>v0.4.28 — mux multiplexing (needs a mux-capable server; opt-in). Setting "net.mux". Xray engine only.</summary>
    public bool MuxEnabled { get; set; }

    public event Action<LogCategory, string>? LogLine;
    public event Action<int>? UnexpectedExit;
    protected void RaiseLog(LogCategory c, string l) => LogLine?.Invoke(c, l);
    protected void RaiseUnexpectedExit(int code) => UnexpectedExit?.Invoke(code);

    /// <summary>
    /// v0.4.24 (ZEUS/worker configs): Cloudflare Worker endpoints cannot reach Cloudflare's own resolvers, so a
    /// tunnel DNS of 1.1.1.1 connects but never resolves anything. For those profiles the tunnel DNS is swapped
    /// for 8.8.8.8 for THIS connection only — the user's setting stays untouched in the UI.
    /// </summary>
    protected void ApplyCloudflareDnsGuard(TunnelProfile profile)
    {
        try
        {
            if (CloudflareDnsGuard.OverrideFor(profile, RemoteDns, out var token) is { } dns)
            {
                RaiseLog(LogCategory.SingBox, $"Cloudflare Worker endpoint ({token}) detected: tunnel DNS {RemoteDns} is unreachable from inside Cloudflare — using {dns} for this connection");
                RemoteDns = dns;
            }
        }
        catch (Exception ex) { RaiseLog(LogCategory.SingBox, "dns guard: " + ex.Message); }
    }

    protected SingBoxBuildOptions NewOptions(IReadOnlyList<string>? alwaysDirect = null)
    {
        _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var o = new SingBoxBuildOptions
        {
            HealthPort = EnginePaths.FreeTcpPort(), ClashApiPort = EnginePaths.FreeTcpPort(), ClashApiSecret = _secret,
            DirectDns = DirectDns, RemoteDns = RemoteDns, LogLevel = LogLevel, AlwaysDirectExecutables = alwaysDirect ?? [],
            TunnelSupportsIpv6 = TunnelIpv6, Stack = TunStack, StrictRoute = StrictRoute,
            RuleSetCachePath = Path.Combine(Paths.RuntimeDir, "rule-set-cache.db"),
            // v0.4.28 — poison-proof resolution of outbound server names (only when the guard proved DoH works),
            Mtu = TunMtu is >= 1280 and <= 9500 ? TunMtu : 9000,
            ResolveViaDoh = _dohForResolver || _verdict is { DohIps.Count: > 0 },
            // v0.4.31 — the sinkhole kill-net rides the PoisonGuard switch: one REJECT rule for the ISP's poison pools.
            RejectSinkholes = PoisonGuardEnabled,
            // v0.4.35 — the server's own addresses bypass the TUN (see SingBoxBuildOptions.BypassServerIps).
            BypassServerIps = _bypassIps,
        };
        RaiseLog(LogCategory.SingBox, $"TUN: stack={o.Stack}, strict_route={o.StrictRoute}, mtu={o.Mtu}, dns_doh={o.ResolveViaDoh}, sinkhole_reject={o.RejectSinkholes}, server_bypass={SingBoxBuildOptions.BypassCidrs(o.BypassServerIps).Count}");
        if (AppVersionTag is { } v) RaiseLog(LogCategory.SingBox, $"KOALIX v{v} — field reports MUST name this line's version");
        return o;
    }

    /// <summary>v0.4.31 — the running app version, stamped into the engine log so every field log is self-dating
    /// (log 355 was analysed for hours before realizing it came from a pre-0.4.30 build: strict_route=False said so).</summary>
    public static string? AppVersionTag { get; set; }

    /// <summary>v0.4.28 — the connect-time PoisonGuard verdict for THIS profile (null = not applicable/checked).</summary>
    protected PoisonVerdict? _verdict;
    /// <summary>v0.4.28 — DoH was reachable when the guard last probed it (enables the DoH server-name resolver).</summary>
    protected bool _dohForResolver;
    /// <summary>v0.4.35 — every address the proxy server's name resolved to (system + DoH), routed outside the TUN.</summary>
    protected IReadOnlyList<string> _bypassIps = [];

    /// <summary>
    /// v0.4.28 — runs the DNS-poisoning defence before a config is built. Share links: the single server domain is
    /// resolved through the system resolver AND DoH; on disagreement the engine later dials the DoH-confirmed IP
    /// (SNI/Host keep the domain). JSON configs: only DoH availability is probed — the merged config then resolves
    /// outbound server names over DoH instead of the hijackable UDP resolver. Never throws: a guard failure must
    /// not delay or break a connect that worked without it.
    /// </summary>
    protected async Task RunPoisonGuardAsync(TunnelProfile profile, CancellationToken ct)
    {
        _verdict = null;
        _dohForResolver = false;
        _bypassIps = [];
        try { _bypassIps = await ServerAddressesAsync(profile, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best effort: without it the server dial still works through the TUN, only less reliably */ }
        if (!PoisonGuardEnabled) { RaiseLog(LogCategory.SingBox, "PoisonGuard: disabled by setting"); return; }
        try
        {
            if (profile.Source == ProfileSource.ShareLink)
            {
                var node = ShareLinkParser.Parse(profile.Content);
                _verdict = await PoisonGuard.CheckAsync(node.Server, enabled: true, ct);
                RaiseLog(LogCategory.SingBox, "PoisonGuard: " + _verdict.Report);
                // A poisoned system answer (sinkhole) must never be routed around the TUN: then DoH's answer only.
                _bypassIps = (_verdict.PoisonSuspected ? _verdict.DohIps : _bypassIps.Concat(_verdict.SystemIps).Concat(_verdict.DohIps))
                    .Concat(_verdict.DialIp is { } d ? [d] : []).Distinct().ToList();
            }
            else
            {
                _dohForResolver = await PoisonGuard.DohReachableAsync(ct);
                RaiseLog(LogCategory.SingBox, _dohForResolver
                    ? "PoisonGuard: DoH reachable — outbound server names resolve over DoH (poison-proof)"
                    : "PoisonGuard: DoH unreachable — outbound server names keep the local resolver");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { RaiseLog(LogCategory.SingBox, "PoisonGuard: " + Redactor.Redact(ex.Message)); }
    }

    /// <summary>
    /// v0.4.35 — the proxy server(s) of a profile as IPs: a share link's single server, or every outbound "server" of
    /// a sing-box JSON. Names are resolved through the system resolver (before the TUN exists); 3 s cap in total.
    /// </summary>
    protected static async Task<IReadOnlyList<string>> ServerAddressesAsync(TunnelProfile profile, CancellationToken ct)
    {
        var hosts = new List<string>();
        if (profile.Source == ProfileSource.ShareLink) hosts.Add(ShareLinkParser.Parse(profile.Content).Server);
        else if (profile.Source == ProfileSource.SingBoxJson && JsonNode.Parse(profile.Content) is JsonObject root)
            foreach (var key in new[] { "outbounds", "endpoints" })
                if (root[key] is JsonArray arr)
                    foreach (var ob in arr.OfType<JsonObject>())
                        if (ob["server"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) hosts.Add(s.Trim());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        var ips = new List<string>();
        foreach (var h in hosts.Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
        {
            if (System.Net.IPAddress.TryParse(h, out var lit)) { ips.Add(lit.ToString()); continue; }
            try { ips.AddRange((await System.Net.Dns.GetHostAddressesAsync(h, cts.Token)).Select(a => a.ToString())); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (System.Net.Sockets.SocketException) { }
        }
        return ips.Distinct().ToList();
    }

    /// <summary>Builds the final sing-box JSON for the profile. Overridden by Xray/WireGuard engines that front through sing-box.</summary>
    protected virtual JsonObject BuildConfig(TunnelProfile profile, SplitPolicy policy, SingBoxBuildOptions o)
    {
        switch (profile.Source)
        {
            case ProfileSource.ShareLink:
                {
                    var node = PoisonGuard.Apply(ShareLinkParser.Parse(profile.Content), _verdict);
                    var errors = node.Validate();
                    if (errors.Count > 0) throw new InvalidDataException(string.Join(", ", errors));
                    if (!SingBoxConfigBuilder.IsNativelySupported(node, out var why)) throw new NotSupportedException(why);
                    return SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, policy, o);
                }
            case ProfileSource.SingBoxJson:
                {
                    var user = JsonNode.Parse(profile.Content) as JsonObject ?? throw new InvalidDataException("Root must be a JSON object");
                    var candidates = SingBoxConfigBuilder.ProxyCandidates(user);
                    if (candidates.Count == 0) throw new InvalidDataException("No proxy outbound found in configuration");
                    // Panel configs (BPB & co.) list individual nodes first and their selector/urltest groups last;
                    // the first candidate is usually the least reliable single node, while route.final/selector is
                    // the entry point the config was designed around. Field diagnosis aid: always log the choice.
                    var tag = SingBoxConfigBuilder.PickTunnelTarget(user, candidates);
                    RaiseLog(LogCategory.SingBox, $"User config: tunnel target outbound '{Redactor.Redact(tag)}'");
                    return SingBoxConfigBuilder.MergeUserConfig(user, tag, policy, o);
                }
            default: throw new NotSupportedException($"{profile.Source} is not a sing-box profile");
        }
    }

    public virtual async Task<EngineValidation> ValidateAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (!File.Exists(Paths.SingBoxExe)) return EngineValidation.Fail("Engine.Error.SingBoxMissing");
        ApplyCloudflareDnsGuard(profile); // v0.4.24: worker-fronted profiles must not inherit a Cloudflare tunnel DNS
        await RunPoisonGuardAsync(profile, ct); // v0.4.28: system-DNS-vs-DoH verdict before any config is built
        JsonObject cfg;
        try { cfg = BuildConfig(profile, policy, NewOptions()); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or FormatException or UriFormatException or InvalidOperationException or ArgumentException)
        { return EngineValidation.Fail(Redactor.Redact(ex.Message)); }
        return await CheckWithBinaryAsync(cfg, ct);
    }

    protected async Task<EngineValidation> CheckWithBinaryAsync(JsonObject cfg, CancellationToken ct)
    {
        var lint = SingBoxConfigLinter.Lint(cfg);
        if (lint.Count > 0) return new EngineValidation(false, lint);
        var path = WriteConfig(cfg, "check");
        try
        {
            var r = await EngineProcess.RunToEndAsync(Paths.SingBoxExe, ["check", "-c", path, "--disable-color"], Paths.RuntimeDir, TimeSpan.FromSeconds(20), ct);
            return r.Success ? EngineValidation.Success : EngineValidation.Fail((r.StdErr + r.StdOut).Trim());
        }
        finally { TryDelete(path); }
    }

    public virtual async Task StartAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        ApplyCloudflareDnsGuard(profile); // v0.4.24: worker-fronted profiles must not inherit a Cloudflare tunnel DNS
        await RunPoisonGuardAsync(profile, ct); // v0.4.28: system-DNS-vs-DoH verdict before any config is built
        var o = NewOptions(AlwaysDirectFor(profile));
        var cfg = BuildConfig(profile, policy, o);
        // Effective routing summary (v0.3.6): field diagnosis was hard because logs never stated the split mode or
        // which DNS final the generated config actually used. One line, no secrets, always logged on connect.
        RaiseLog(LogCategory.SingBox, $"Routing: mode={policy.Mode}, rules={policy.Rules.Count}, " +
            $"route_final={cfg["route"]?["final"]}, dns_final={cfg["dns"]?["final"]}, dns_strategy={cfg["dns"]?["strategy"]}, tunnelDns={policy.TunnelDns}");
        await StartSingBoxAsync(cfg, o, ct);
    }

    protected virtual IReadOnlyList<string> AlwaysDirectFor(TunnelProfile profile) => [];

    protected Task StartSingBoxAsync(JsonObject cfg, SingBoxBuildOptions o, CancellationToken ct)
    {
        _configPath = WriteConfig(cfg, "run");
        HealthProxyPort = o.HealthPort;
        StatsApi = (new Uri($"http://127.0.0.1:{o.ClashApiPort}"), o.ClashApiSecret);
        _proc.Start(Paths.SingBoxExe, ["run", "-c", _configPath, "-D", Paths.RuntimeDir, "--disable-color"], Paths.RuntimeDir);
        return Task.CompletedTask;
    }

    public virtual async Task StopAsync(CancellationToken ct)
    {
        await _proc.StopAsync(TimeSpan.FromSeconds(6));
        if (_configPath is not null) { TryDelete(_configPath); _configPath = null; }
        HealthProxyPort = null; StatsApi = null;
    }

    protected string WriteConfig(JsonObject cfg, string purpose)
    {
        var path = Path.Combine(Paths.RuntimeDir, $"sb-{purpose}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SingBoxConfigBuilder.ToJson(cfg));
        return path;
    }

    protected static void TryDelete(string p) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public virtual async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await _proc.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
