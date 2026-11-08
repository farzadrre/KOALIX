using System.Diagnostics;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.Tunneling;
using Koalix.WireGuard;
using Koalix.Xray;
using Xunit.Abstractions;

namespace Koalix.Tests;

/// <summary>
/// Validates generated configurations with the REAL engines (sing-box check / xray run -test).
/// Set KOALIX_SINGBOX_BIN and KOALIX_XRAY_BIN to the engine binaries; tests are no-ops (reported) otherwise.
/// </summary>
public class EngineValidationTests(ITestOutputHelper output)
{
    private static readonly string? SingBox = Environment.GetEnvironmentVariable("KOALIX_SINGBOX_BIN");
    private static readonly string? Xray = Environment.GetEnvironmentVariable("KOALIX_XRAY_BIN");

    private static SingBoxBuildOptions Opts(params string[] alwaysDirect) => new()
    { HealthPort = 18080, ClashApiPort = 19090, ClashApiSecret = "testsecret", AlwaysDirectExecutables = alwaysDirect };

    private static readonly SplitPolicy Include = new(SplitMode.Include,
    [
        new AppRule { DisplayName = "Game", ExecutablePath = @"C:\Games\Valorant\VALORANT.exe", Action = AppAction.Tunnel, IncludeFolder = true },
        new AppRule { DisplayName = "Discord", ExecutablePath = @"C:\Users\u\AppData\Local\Discord\app-1.0\Discord.exe", Action = AppAction.Tunnel },
        new AppRule { DisplayName = "Bad", ExecutablePath = @"C:\x\evil.exe", Action = AppAction.Block },
    ], TunnelDns: true, BlockIpv6Leaks: true);

    private static readonly SplitPolicy Exclude = new(SplitMode.Exclude,
        [new AppRule { DisplayName = "Chrome", ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe", Action = AppAction.Direct }]);

    private (int Code, string Out) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, o);
    }

    private void CheckSingBox(JsonObject config, string name)
    {
        if (SingBox is null) { output.WriteLine("KOALIX_SINGBOX_BIN not set — skipped"); return; }
        var f = Path.Combine(Path.GetTempPath(), $"koalix-{name}-{Guid.NewGuid():N}.json");
        File.WriteAllText(f, SingBoxConfigBuilder.ToJson(config));
        var (code, o) = Run(SingBox, "check", "-c", f);
        output.WriteLine($"[{name}] sing-box check exit={code} {o}");
        Assert.True(code == 0, $"sing-box rejected {name}: {o}\n{File.ReadAllText(f)}");
        Assert.Empty(SingBoxConfigLinter.Lint(config)); // semantic references (sing-box check does not verify them)
    }

    public static IEnumerable<object[]> Links() =>
    [
        [LinkAndConfigTests.Vless], [LinkAndConfigTests.Reality], [LinkAndConfigTests.Ss], [LinkAndConfigTests.Trojan],
        ["vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"v":"2","ps":"vm","add":"example.com","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"0","scy":"auto","net":"grpc","path":"svc","tls":"tls","sni":"example.com"}"""))],
        ["hysteria2://pass@example.com:443?sni=example.com&obfs=salamander&obfs-password=x#hy2"],
        ["tuic://11111111-2222-3333-4444-555555555555:pw@example.com:443?congestion_control=bbr&alpn=h3&sni=example.com#tuic"],
    ];

    [Theory]
    [MemberData(nameof(Links))]
    public void SingBox_accepts_generated_tun_config_for_share_links(string link)
    {
        var node = ShareLinkParser.Parse(link);
        Assert.True(SingBoxConfigBuilder.IsNativelySupported(node, out var why), why);
        CheckSingBox(SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, Include, Opts()), node.Protocol + "-include");
        CheckSingBox(SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, Exclude, Opts()), node.Protocol + "-exclude");
        CheckSingBox(SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, SplitPolicy.Full, Opts()), node.Protocol + "-full");
    }

    [Fact]
    public void Ipv4_only_tunnel_rejects_ipv6_for_tunneled_apps_before_proxy_rule()
    {
        var policy = Include with { BlockIpv6Leaks = false };
        var wg = WireGuardConfig.Parse(WireGuardAndSecurityTests.Conf, out _);
        var cfg = SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, policy, Opts() with { TunnelSupportsIpv6 = false });
        var rules = cfg["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var reject = rules.FindIndex(r => (int?)r["ip_version"] == 6 && (string?)r["action"] == "reject");
        var proxy = rules.FindIndex(r => (string?)r["outbound"] == "proxy" && r["process_path"] is not null);
        Assert.True(reject >= 0 && reject < proxy, "IPv6 reject must precede the tunneled-app rule");
        // sing-box 1.14+: the legacy per-rule `strategy` DNS action option is rejected (initialize dns router) as
        // soon as any query_type/ip_version DNS rule exists; IPv4-only is enforced via the global dns.strategy.
        Assert.All(cfg["dns"]!["rules"]!.AsArray().OfType<JsonObject>(), r => Assert.Null(r["strategy"]));
        Assert.Equal("ipv4_only", (string?)cfg["dns"]!["strategy"]);
        Assert.Equal("mozilla", (string?)cfg["certificate"]!["store"]);
        CheckSingBox(cfg, "wireguard-include-v4only");
        CheckSingBox(SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, SplitPolicy.Full, Opts()), "wireguard-full-v4only");
    }

    private const string PartialConf = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 10.8.0.2/32, fddd:2c4:2c4:2c4:0:0:9:74f2/128
        DNS = 8.8.8.8

        [Peer]
        PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
        AllowedIPs = 1.0.0.0/8, 2.0.0.0/7, 4.0.0.0/6, 8.0.0.0/7, 11.0.0.0/8, 12.0.0.0/6, 16.0.0.0/4
        Endpoint = fast3.example.com:995
        """;

    [Fact]
    public void WireSock_config_keeps_obfuscation_and_sets_app_lists()
    {
        var conf = PartialConf.Replace("DNS = 8.8.8.8", "DNS = 8.8.8.8\nJc = 4\nJmin = 40\nJmax = 70") + "\nAllowedApps = old.exe\n";
        var wg = WireGuardConfig.Parse(conf, out var errs);
        Assert.Empty(errs);
        Assert.True(wg.HasObfuscation);
        var ws = WireGuardConfig.ToWireSock(conf, [@"C:\Program Files\Google\Chrome\Application\chrome.exe"], null);
        Assert.Contains("Jc = 4", ws);
        Assert.DoesNotContain("old.exe", ws);
        Assert.Contains(@"AllowedApps = C:\Program Files\Google\Chrome\Application\chrome.exe", ws);
        Assert.Empty(WireGuardConfig.Parse(ws, out var e2).ObfuscationKeys.Except(wg.ObfuscationKeys)); Assert.Empty(e2);
    }

    [Fact]
    public void WireGuard_mtu_setting_defaults_to_1280()
    {
        Assert.Equal(1280, WireGuardEngine.ParseMtu(null));
        Assert.Equal(1200, WireGuardEngine.ParseMtu("1200"));
        Assert.Null(WireGuardEngine.ParseMtu("config"));
        Assert.Equal(1280, WireGuardEngine.ParseMtu("99999"));
    }

    [Fact]
    public void Partial_ipv4_allowed_ips_disable_ipv6_and_route_outside_ranges_direct()
    {
        var wg = WireGuardConfig.Parse(PartialConf, out var errs);
        Assert.Empty(errs);
        Assert.True(wg.HasIpv6Address);
        Assert.False(wg.CarriesIpv6);
        Assert.NotNull(wg.PartialTunnelCidrs);
        Assert.True(WireGuardConfig.IsCovered("8.8.8.8", wg.PartialTunnelCidrs!));
        Assert.False(WireGuardConfig.IsCovered("185.1.1.1", wg.PartialTunnelCidrs!));
        var full = WireGuardConfig.Parse(WireGuardAndSecurityTests.Conf, out _);
        Assert.True(full.CarriesIpv6);
        Assert.Null(full.PartialTunnelCidrs);

        var o = Opts() with { TunnelSupportsIpv6 = wg.CarriesIpv6, TunnelCidrs = wg.PartialTunnelCidrs };
        // Full tunnel: v6 rejected, then outside-range -> direct, final proxy.
        var cfg = SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, SplitPolicy.Full, o);
        var rules = cfg["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var v6 = rules.FindIndex(r => (int?)r["ip_version"] == 6 && (string?)r["action"] == "reject");
        var outside = rules.FindIndex(r => r["ip_cidr"] is not null && (bool?)r["invert"] == true && (string?)r["outbound"] == "direct");
        Assert.True(v6 >= 0 && outside > v6);
        Assert.Equal("proxy", (string?)cfg["route"]!["final"]);
        CheckSingBox(cfg, "wireguard-partial-full");

        // Include: tunneled apps go to proxy only inside the ranges; no inverted rule combined with process match.
        var inc = SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, Include with { BlockIpv6Leaks = false }, o);
        var irules = inc["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Contains(irules, r => r["process_path"] is not null && r["ip_cidr"] is not null && (string?)r["outbound"] == "proxy");
        Assert.DoesNotContain(irules, r => r["process_path"] is not null && r["invert"] is not null);
        CheckSingBox(inc, "wireguard-partial-include");
        CheckSingBox(SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, Exclude, o), "wireguard-partial-exclude");
    }

    [Fact]
    public void SingBox_accepts_wireguard_endpoint_split_config()
    {
        var wg = WireGuardConfig.Parse(WireGuardAndSecurityTests.Conf, out _);
        CheckSingBox(SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, Include, Opts()), "wireguard-include");
    }

    [Fact]
    public void SingBox_accepts_folder_rules_with_spaces()
    {
        var wg = WireGuardConfig.Parse(WireGuardAndSecurityTests.Conf, out _);
        var policy = new SplitPolicy(SplitMode.Include, [new AppRule { DisplayName = "Steam", ExecutablePath = @"C:\Program Files (x86)\Steam", IncludeFolder = true, Action = AppAction.Tunnel },
            new AppRule { DisplayName = "Riot", ExecutablePath = @"D:\Riot Games\#Riot+Client\RiotClientServices.exe", IncludeFolder = true, Action = AppAction.Block }]);
        CheckSingBox(SingBoxConfigBuilder.Build(wg.ToSingBoxEndpoint(), true, policy, Opts()), "wireguard-folder");
    }

    [Fact]
    public void SingBox_accepts_xray_front_config()
    {
        // Xray mode: sing-box TUN front with a SOCKS outbound to local Xray, xray.exe forced direct.
        var socks = new JsonObject { ["type"] = "socks", ["tag"] = "proxy", ["server"] = "127.0.0.1", ["server_port"] = 10808, ["version"] = "5" };
        CheckSingBox(SingBoxConfigBuilder.Build(socks, false, Include, Opts(@"C:\KOALIX\engines\xray\xray.exe")), "xray-front");
    }

    [Fact]
    public void SingBox_accepts_merged_user_config()
    {
        var user = JsonNode.Parse("""
            {"log":{"level":"warn"},"outbounds":[{"type":"vless","tag":"my-proxy","server":"example.com","server_port":443,"uuid":"11111111-2222-3333-4444-555555555555",
              "tls":{"enabled":true,"server_name":"example.com"}},{"type":"direct","tag":"direct"}]}
            """)!.AsObject();
        CheckSingBox(SingBoxConfigBuilder.MergeUserConfig(user, "my-proxy", Exclude, Opts()), "merged-user");
    }

    [Fact]
    public void Invalid_configs_are_rejected_by_engine_or_linter()
    {
        var dangling = JsonNode.Parse("""{"route":{"rules":[{"process_path":["x"],"outbound":"does-not-exist"}]}}""")!.AsObject();
        Assert.Contains(SingBoxConfigLinter.Lint(dangling), e => e.StartsWith("Lint.UnknownOutbound:does-not-exist"));
        Assert.Contains("Lint.ProcessRulesWithoutTun", SingBoxConfigLinter.Lint(dangling));
        if (SingBox is null) return;
        var f = Path.Combine(Path.GetTempPath(), $"koalix-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(f, """{"outbounds":[{"type":"nonexistent","tag":"a"}]}""");
        Assert.NotEqual(0, Run(SingBox, "check", "-c", f).Code);
    }

    public static IEnumerable<object[]> XrayLinks() =>
    [
        [LinkAndConfigTests.Xhttp], [LinkAndConfigTests.Vless], [LinkAndConfigTests.Reality], [LinkAndConfigTests.Trojan], [LinkAndConfigTests.Ss],
    ];

    [Theory]
    [MemberData(nameof(XrayLinks))]
    public void Xray_accepts_generated_config(string link)
    {
        if (Xray is null) { output.WriteLine("KOALIX_XRAY_BIN not set — skipped"); return; }
        var cfg = XrayConfigBuilder.Build(ShareLinkParser.Parse(link), 10808);
        var f = Path.Combine(Path.GetTempPath(), $"koalix-xray-{Guid.NewGuid():N}.json");
        File.WriteAllText(f, cfg.ToJsonString());
        var (code, o) = Run(Xray, "run", "-test", "-c", f);
        output.WriteLine($"xray -test exit={code} {o}");
        Assert.True(code == 0, $"xray rejected config: {o}\n{cfg.ToJsonString()}");
    }
}
