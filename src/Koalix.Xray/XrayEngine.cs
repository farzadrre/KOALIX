using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.Tunneling;

namespace Koalix.Xray;

/// <summary>
/// Xray-core has no Windows TUN and no process-aware routing, so system-level routing is provided by a sing-box TUN
/// front-end whose "proxy" outbound is Xray's loopback SOCKS inbound. xray.exe itself is forced DIRECT in the routing
/// rules, which prevents routing loops. This gives real per-application routing for Xray-only transports (XHTTP, mKCP...).
/// </summary>
public sealed class XrayEngine : SingBoxEngine
{
    private readonly EngineProcess _xray = new(LogCategory.Xray);
    private string? _xrayConfig;
    private int _socksPort;

    public XrayEngine(EnginePaths paths) : base(paths)
    {
        _xray.Output += (c, l) => RaiseLog(c, l);
        _xray.UnexpectedExit += c => RaiseLog(LogCategory.Xray, $"xray exited with code {c}");
    }

    public override EngineKind Kind => EngineKind.Xray;
    public override IReadOnlyList<int> ProcessIds => base.ProcessIds.Concat(_xray.Pid is { } p ? [p] : Array.Empty<int>()).ToList();

    private JsonObject BuildXray(TunnelProfile profile)
    {
        return profile.Source switch
        {
            ProfileSource.ShareLink => XrayConfigBuilder.Build(
                PoisonGuard.Apply(ValidNode(profile.Content), _verdict), _socksPort, LogLevel,
                new XrayTweaks(FragmentEnabled, MuxEnabled),
                // v0.4.34: hand xray.exe the addresses we already resolved — DoH-confirmed first, else the system's
                _verdict is { DohIps.Count: > 0 } ? _verdict.DohIps : _verdict?.SystemIps),
            ProfileSource.XrayJson => WithLoopbackSocks(JsonNode.Parse(profile.Content) as JsonObject ?? throw new InvalidDataException("Root must be an object")),
            _ => throw new NotSupportedException($"{profile.Source} is not an Xray profile"),
        };
    }

    private static ProxyNode ValidNode(string link)
    {
        var n = ShareLinkParser.Parse(link);
        var e = n.Validate();
        if (e.Count > 0) throw new InvalidDataException(string.Join(", ", e));
        return n;
    }

    /// <summary>Preserves the user's Xray JSON and adds (or replaces) only the KOALIX loopback SOCKS inbound.</summary>
    private JsonObject WithLoopbackSocks(JsonObject user)
    {
        var root = (JsonObject)user.DeepClone();
        var inbounds = root["inbounds"] as JsonArray ?? (JsonArray)(root["inbounds"] = new JsonArray());
        foreach (var i in inbounds.OfType<JsonObject>().Where(i => (string?)i["tag"] == "koalix-socks").ToList()) inbounds.Remove(i);
        inbounds.Insert(0, new JsonObject
        {
            ["tag"] = "koalix-socks", ["protocol"] = "socks", ["listen"] = "127.0.0.1", ["port"] = _socksPort,
            ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" },
            ["sniffing"] = XrayConfigBuilder.Sniffing(),
        });
        return root;
    }

    protected override IReadOnlyList<string> AlwaysDirectFor(TunnelProfile profile) => [Paths.XrayExe];

    protected override JsonObject BuildConfig(TunnelProfile profile, SplitPolicy policy, SingBoxBuildOptions o)
    {
        var upstream = new JsonObject { ["type"] = "socks", ["tag"] = "proxy", ["server"] = "127.0.0.1", ["server_port"] = _socksPort, ["version"] = "5", ["udp_over_tcp"] = false };
        return SingBoxConfigBuilder.Build(upstream, false, policy, o with { AlwaysDirectExecutables = [Paths.XrayExe], SinkholeViaProxy = true });
    }

    public override async Task<EngineValidation> ValidateAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (!File.Exists(Paths.XrayExe)) return EngineValidation.Fail("Engine.Error.XrayMissing");
        await RunPoisonGuardAsync(profile, ct); // v0.4.28: system-DNS-vs-DoH verdict before the config is built
        _socksPort = EnginePaths.FreeTcpPort();
        JsonObject cfg;
        try { cfg = BuildXray(profile); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or UriFormatException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return EngineValidation.Fail(Redactor.Redact(ex.Message)); }
        var path = Path.Combine(Paths.RuntimeDir, $"xr-check-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, cfg.ToJsonString(), ct);
        try
        {
            var r = await EngineProcess.RunToEndAsync(Paths.XrayExe, ["run", "-test", "-c", path], Paths.RuntimeDir, TimeSpan.FromSeconds(20), ct);
            if (!r.Success) return EngineValidation.Fail((r.StdOut + r.StdErr).Trim());
        }
        finally { TryDelete(path); }
        return await base.ValidateAsync(profile, policy, ct);
    }

    public override async Task StartAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        if (_socksPort == 0) _socksPort = EnginePaths.FreeTcpPort();
        await RunPoisonGuardAsync(profile, ct); // v0.4.28: verdict (cached 10 min — the second call is instant)
        if (profile.Source == ProfileSource.ShareLink)
        {
            if (_verdict is { PoisonSuspected: true, DialIp: { } ip })
                RaiseLog(LogCategory.Xray, $"Xray dials {ip} directly; TLS/WS keep the domain as SNI/Host (DNS poisoning shield, v0.4.28)");
            if (FragmentEnabled)
                RaiseLog(LogCategory.Xray, "TLS fragment: on (tlshello 100-200 bytes / 10-20 ms — DPI hardening, v0.4.28)");
            if (MuxEnabled)
                RaiseLog(LogCategory.Xray, "mux: on (concurrency 8 — server must support Mux.Cool, v0.4.28)");
        }
        var cfg = BuildXray(profile);
        _xrayConfig = Path.Combine(Paths.RuntimeDir, $"xr-run-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_xrayConfig, cfg.ToJsonString(), ct);
        _xray.Start(Paths.XrayExe, ["run", "-c", _xrayConfig], Paths.RuntimeDir);
        await WaitPortAsync(_socksPort, TimeSpan.FromSeconds(8), ct);
        await base.StartAsync(profile, policy, ct);
    }

    private static async Task WaitPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try { using var c = new System.Net.Sockets.TcpClient(); await c.ConnectAsync(System.Net.IPAddress.Loopback, port, ct); return; }
            catch (System.Net.Sockets.SocketException) { await Task.Delay(200, ct); }
        }
        throw new TimeoutException("Xray SOCKS inbound did not start");
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await _xray.StopAsync(TimeSpan.FromSeconds(4));
        if (_xrayConfig is not null) { TryDelete(_xrayConfig); _xrayConfig = null; }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _xray.DisposeAsync();
    }
}
