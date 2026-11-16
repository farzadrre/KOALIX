using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.Tunneling;

namespace Koalix.Aether;

/// <summary>
/// v0.4.49 — the split-aware companion of the Aether section (field report: «تو قسمت تونل زنی فایرفاکس گذاشتم مستقیم
/// و کروم تونل، و جفتشون تونل شدن با حالت اتر»). The old "system proxy" checkbox hijacked WinINET for the WHOLE
/// OS, so every app — including the ones the user marked Direct in تونل‌زنی — was forced through Aether.
///
/// This class instead runs a small sing-box session with the SAME TUN + SplitRuleCompiler pipeline every other
/// engine uses, but with Aether's local SOCKS5 as the proxy outbound: per-app rules, domain rules, DNS split,
/// QUIC block and the Iran preset all behave exactly like a normal tunnel. The Aether engine itself stays a plain
/// local proxy (started by <see cref="AetherService"/>); sing-box routes flows into or around it per policy.
/// </summary>
public sealed class AetherSplitTunnel : IAsyncDisposable
{
    /// <summary>Tag of the sing-box outbound that dials Aether's SOCKS5 (same tag the chain mode uses).</summary>
    public const string AetherOutTag = "aether-out";

    private readonly EngineProcess _proc = new(LogCategory.SingBox);
    private readonly EnginePaths _paths;
    private string? _configPath;

    public AetherSplitTunnel(EnginePaths paths, Action<string>? log = null)
    {
        _paths = paths;
        if (log is not null) _proc.Output += (_, l) => log(l);
    }

    /// <summary>Builds the sing-box config for the split session: TUN inbound, compiled per-app route/DNS rules,
    /// direct + aether-out outbounds, final per mode. Static so tests can pin the exact shape.</summary>
    public static JsonObject BuildConfig(SplitPolicy policy, int aetherPort, IReadOnlyList<string> alwaysDirect, SingBoxBuildOptions o)
    {
        // Same compilation path as every engine — process rules, domain rules, DNS split, QUIC/v6 guards.
        var compiled = SplitRuleCompiler.Compile(policy, alwaysDirect, SplitRuleCompiler.ProxyTag, o.TunnelSupportsIpv6, o.TunnelCidrs);
        compiled.RouteRules.Insert(SplitRuleCompiler.AfterSniff(compiled.RouteRules),
            new JsonObject { ["inbound"] = "health-in", ["outbound"] = SplitRuleCompiler.ProxyTag });
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = o.LogLevel, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    // Remote DNS rides Aether (same shape RemoteDnsServer produces: https + detour).
                    new JsonObject { ["type"] = "https", ["tag"] = SplitRuleCompiler.RemoteDnsTag, ["server"] = o.RemoteDns, ["detour"] = SplitRuleCompiler.ProxyTag },
                    new JsonObject { ["type"] = "udp", ["tag"] = SplitRuleCompiler.LocalDnsTag, ["server"] = o.DirectDns },
                },
                ["rules"] = compiled.DnsRules,
                ["final"] = compiled.DnsFinal,
                ["strategy"] = compiled.DnsIpv4Only ? "ipv4_only" : "prefer_ipv4",
            },
            ["certificate"] = new JsonObject { ["store"] = o.CertificateStore },
            ["inbounds"] = new JsonArray
            {
                o.TunInbound(),
                new JsonObject { ["type"] = "mixed", ["tag"] = "health-in", ["listen"] = "127.0.0.1", ["listen_port"] = o.HealthPort },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "direct", ["tag"] = SplitRuleCompiler.DirectTag },
                new JsonObject { ["type"] = "socks", ["tag"] = SplitRuleCompiler.ProxyTag, ["server"] = "127.0.0.1", ["server_port"] = aetherPort, ["version"] = "5" },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = compiled.RouteRules,
                ["final"] = compiled.RouteFinal,
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = SplitRuleCompiler.LocalDnsTag,
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{o.ClashApiPort}", ["secret"] = o.ClashApiSecret },
            },
        };
        return root;
    }

    /// <summary>Writes the config, checks it with the real binary, then starts the session. Throws with the
    /// engine's own words on failure (they carry no secrets — configs are never logged).</summary>
    public async Task StartAsync(SplitPolicy policy, int aetherPort, IReadOnlyList<string> alwaysDirect, CancellationToken ct)
    {
        var sbExe = _paths.FindSingBoxExe() ?? throw new InvalidOperationException("Engine.Error.SingBoxMissing");
        var o = new SingBoxBuildOptions
        {
            HealthPort = EnginePaths.FreeTcpPort(), ClashApiPort = EnginePaths.FreeTcpPort(),
            ClashApiSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
        };
        var cfg = BuildConfig(policy, aetherPort, alwaysDirect, o);
        _configPath = Path.Combine(_paths.RuntimeDir, $"aether-split-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_configPath, SingBoxConfigBuilder.ToJson(cfg), ct);

        // "sing-box check" first — a bad route rule must never take the network down.
        var check = new EngineProcess(LogCategory.SingBox);
        try
        {
            var checkedCfg = await Task.Run(() =>
            {
                check.Start(sbExe, ["check", "-c", _configPath, "-D", _paths.RuntimeDir, "--disable-color"], _paths.RuntimeDir);
                return true;
            }, ct);
            _ = checkedCfg;
        }
        catch (Exception ex)
        {
            TryDeleteConfig();
            throw new InvalidOperationException("Aether.Error.SplitTunnelCheck: " + ex.Message);
        }
        finally { await check.DisposeAsync(); }

        _proc.Start(sbExe, ["run", "-c", _configPath, "-D", _paths.RuntimeDir, "--disable-color"], _paths.RuntimeDir);
    }

    public bool IsRunning => _proc.IsRunning;

    public async Task StopAsync()
    {
        await _proc.StopAsync(TimeSpan.FromSeconds(5));
        TryDeleteConfig();
    }

    private void TryDeleteConfig()
    {
        try { if (_configPath is not null) File.Delete(_configPath); } catch { }
        _configPath = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _proc.DisposeAsync();
    }
}
