using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.47 — the chain (config combination): WARP gaming split, full WARP exit, WARP over the config and the local
/// Psiphon exit, applied onto generated (share-link) and merged (panel JSON) sing-box configs. Also guards the
/// honest-failure contract: a chain that cannot run must degrade to the plain config, never break the connect.
/// </summary>
public class ChainConfigTests
{
    private const string WorkerVless =
        "vless://50414e45-4c5f-5a45-5553-e3e8e0530ae9@104.25.177.89:443?path=%2Fstream%2FPANEL%2Fe3e8e0530ae9&security=tls&encryption=none&host=panel.example.workers.dev&type=ws&sni=panel.example.workers.dev#ZEUS";

    private static WarpIdentity Warp(string privateKey = "mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=", int[]? reserved = null) => new()
    {
        PrivateKey = privateKey, AddressV4 = "172.16.0.2", AddressV6 = "2606:4700:110:8dbf::/128".Split('/')[0],
        Reserved = reserved ?? [42, 87, 154],
    };

    private static SingBoxBuildOptions Opts() => new() { HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s" };

    private static JsonObject GeneratedConfig() =>
        SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(ShareLinkParser.Parse(WorkerVless)), false, SplitPolicy.Full, Opts());

    /// <summary>A BPB/Koala-style panel config: nodes first, selector last, tunnel-DNS via the selector, UDP rejected.</summary>
    private const string KoalaJson = """
    {
      "log": { "level": "warn" },
      "dns": {
        "servers": [
          { "type": "https", "server": "8.8.8.8", "detour": "✅ Selector", "tag": "dns-remote" },
          { "type": "udp", "server": "8.8.8.8", "tag": "dns-direct" }
        ],
        "final": "dns-remote", "independent_cache": true, "strategy": "ipv4_only"
      },
      "inbounds": [ { "type": "tun", "tag": "tun-in", "address": ["172.19.0.1/30"], "mtu": 9000, "auto_route": true } ],
      "outbounds": [
        { "type": "vless", "tag": "🐨 1", "server": "v03yo4gf.pages.dev", "server_port": 443, "uuid": "f272053e-6960-4b68-a160-4e88a4ac718f", "tls": { "enabled": true }, "transport": { "type": "ws", "path": "/stream" } },
        { "type": "trojan", "tag": "🐨 2", "server": "v03yo4gf.pages.dev", "server_port": 443, "password": "pw", "tls": { "enabled": true }, "transport": { "type": "ws", "path": "/stream" } },
        { "type": "selector", "tag": "✅ Selector", "outbounds": ["🐨 1", "🐨 2"] },
        { "type": "direct", "tag": "direct" }
      ],
      "route": {
        "rules": [
          { "action": "sniff" },
          { "protocol": "dns", "action": "hijack-dns" },
          { "ip_is_private": true, "outbound": "direct" },
          { "network": "udp", "action": "reject" }
        ],
        "final": "✅ Selector", "auto_detect_interface": true
      }
    }
    """;

    private static JsonObject MergedConfig(string user, out string targetTag)
    {
        var parsed = JsonNode.Parse(user)!.AsObject();
        var candidates = SingBoxConfigBuilder.ProxyCandidates(parsed);
        targetTag = SingBoxConfigBuilder.PickTunnelTarget(parsed, candidates);
        return SingBoxConfigBuilder.MergeUserConfig(parsed, targetTag, SplitPolicy.Full, Opts());
    }

    private static JsonObject Ob(JsonObject cfg, string tag) =>
        ((cfg["outbounds"] as JsonArray ?? []).Concat((cfg["endpoints"] as JsonArray ?? []))).OfType<JsonObject>()
            .First(o => (string?)o["tag"] == tag);

    private static bool HasRule(JsonObject cfg, Func<JsonObject, bool> p) =>
        (cfg["route"]?["rules"] as JsonArray)!.OfType<JsonObject>().Any(p);

    private static string[] RulesOut(JsonObject cfg, string inbound) =>
        (cfg["route"]?["rules"] as JsonArray)!.OfType<JsonObject>().Where(r => (string?)r["inbound"] == inbound)
            .Select(r => (string)r["outbound"]!).ToArray();

    /* ── WARP gaming mode ── */

    [Fact]
    public void Gaming_mode_adds_warp_endpoint_and_udp_rule()
    {
        var cfg = ChainConfig.Apply(GeneratedConfig(), SplitRuleCompiler.ProxyTag,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpGaming, Warp = Warp() }, SplitPolicy.Full);
        var ep = Ob(cfg, ChainConfig.WarpTag);
        Assert.Equal("wireguard", (string)ep["type"]!);
        Assert.Equal("172.16.0.2/32", ((JsonArray)ep["address"]!)[0]!.GetValue<string>());
        Assert.Null(ep["detour"]); // direct dial — the gaming path must not depend on the config
        Assert.Equal([42, 87, 154], ((JsonArray)ep["peers"]![0]!["reserved"])!.Select(x => x!.GetValue<int>()).ToArray());
        Assert.True(HasRule(cfg, r => (string?)r["network"] == "udp" && (string?)r["outbound"] == ChainConfig.WarpTag));
        Assert.Equal("proxy", (string)cfg["route"]!["final"]!); // TCP keeps the config
        Assert.Equal([ChainConfig.WarpTag], RulesOut(cfg, "health-in")); // health exercises the gaming path
        Assert.Equal([], SingBoxConfigLinter.Lint(cfg).ToArray());
    }

    [Fact]
    public void Gaming_mode_removes_panel_udp_reject_and_keeps_selector_final()
    {
        var cfg = MergedConfig(KoalaJson, out var target);
        Assert.Equal("✅ Selector", target);
        var chained = ChainConfig.Apply(cfg, target,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpGaming, Warp = Warp() }, SplitPolicy.Full);
        // the PANEL's generic {"network":"udp","action":"reject"} must be gone; KOALIX's own conditional
        // rejects (QUIC on udp/443, the v6 leak guard) must survive untouched
        Assert.False(HasRule(chained, r => (string?)r["action"] == "reject" && (string?)r["network"] == "udp"
            && r["port"] is null && r["ip_cidr"] is null && r["process_path"] is null && r["process_path_regex"] is null));
        Assert.True(HasRule(chained, r => (string?)r["network"] == "udp" && (string?)r["outbound"] == ChainConfig.WarpTag));
        Assert.Equal("✅ Selector", (string)chained["route"]!["final"]!);
        Assert.Equal([], SingBoxConfigLinter.Lint(chained).ToArray());
    }

    [Fact]
    public void Gaming_include_mode_splits_only_tunneled_apps_udp()
    {
        var policy = new SplitPolicy(SplitMode.Include,
            [new AppRule { DisplayName = "game", ExecutablePath = @"C:\Games\_VALORANT\VALORANT-Win64.exe", Action = AppAction.Tunnel }]);
        var cfg = ChainConfig.Apply(
            SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(ShareLinkParser.Parse(WorkerVless)), false, policy, Opts()),
            SplitRuleCompiler.ProxyTag,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpGaming, Warp = Warp() }, policy);
        var rules = (cfg["route"]!["rules"] as JsonArray)!.OfType<JsonObject>().ToList();
        // the warp rules must sit BEFORE the process rule that sends the game to the proxy (first-match routing)
        var warpIdx = rules.FindIndex(r => (string?)r["outbound"] == ChainConfig.WarpTag);
        var proxyIdx = rules.FindIndex(r => (string?)r["outbound"] == SplitRuleCompiler.ProxyTag);
        Assert.True(warpIdx >= 0 && proxyIdx > warpIdx);
        // LAN stays direct, and the LAN rule must precede the WARP rule (first-match)
        var lanIdx = rules.FindIndex(r => r.ContainsKey("process_path") && r["ip_is_private"]?.GetValue<bool>() == true && (string?)r["outbound"] == "direct");
        Assert.True(lanIdx >= 0 && lanIdx < warpIdx);
        Assert.Equal("direct", (string)cfg["route"]!["final"]!); // Include default stays direct
    }

    /* ── WARP exit / over-proxy ── */

    [Fact]
    public void Exit_mode_routes_final_and_dns_through_warp()
    {
        var cfg = MergedConfig(KoalaJson, out var target);
        var chained = ChainConfig.Apply(cfg, target,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpExit, Warp = Warp() }, SplitPolicy.Full);
        Assert.Equal(ChainConfig.WarpTag, (string)chained["route"]!["final"]!);
        var remote = (chained["dns"]!["servers"] as JsonArray)!.OfType<JsonObject>().First(s => (string?)s["tag"] == "dns-remote");
        Assert.Equal(ChainConfig.WarpTag, (string)remote["detour"]!);
        Assert.Equal([ChainConfig.WarpTag], RulesOut(chained, "health-in"));
        Assert.Equal([], SingBoxConfigLinter.Lint(chained).ToArray());
    }

    [Fact]
    public void OverProxy_mode_dials_warp_through_the_config()
    {
        var cfg = ChainConfig.Apply(GeneratedConfig(), SplitRuleCompiler.ProxyTag,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpOverProxy, Warp = Warp() }, SplitPolicy.Full);
        var ep = Ob(cfg, ChainConfig.WarpTag);
        Assert.Equal("proxy", (string)ep["detour"]!);
        Assert.Equal(ChainConfig.WarpTag, (string)cfg["route"]!["final"]!);
        Assert.Equal([], SingBoxConfigLinter.Lint(cfg).ToArray());
    }

    /* ── Psiphon / fail-safe ── */

    [Fact]
    public void Psiphon_mode_adds_local_socks_exit()
    {
        var cfg = MergedConfig(KoalaJson, out var target);
        var chained = ChainConfig.Apply(cfg, target,
            new ChainOptions { Enabled = true, Mode = ChainMode.Psiphon, PsiphonPort = 1080 }, SplitPolicy.Full);
        var ob = Ob(chained, ChainConfig.PsiphonTag);
        Assert.Equal("socks", (string)ob["type"]!);
        Assert.Equal("127.0.0.1", (string)ob["server"]!);
        Assert.Equal(1080, ob["server_port"]!.GetValue<int>());
        Assert.Equal(ChainConfig.PsiphonTag, (string)chained["route"]!["final"]!);
        Assert.Equal([], SingBoxConfigLinter.Lint(chained).ToArray());
    }

    [Fact]
    public void Disabled_chain_returns_the_config_untouched()
    {
        var before = GeneratedConfig();
        var after = ChainConfig.Apply(before, SplitRuleCompiler.ProxyTag,
            new ChainOptions { Enabled = false, Warp = Warp() }, SplitPolicy.Full);
        Assert.Null(after["endpoints"]);
        Assert.Equal("proxy", (string)after["route"]!["final"]!);
    }

    [Fact]
    public void Missing_identity_degrades_to_the_plain_config()
    {
        var before = GeneratedConfig();
        var after = ChainConfig.Apply(before, SplitRuleCompiler.ProxyTag,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpGaming, Warp = null }, SplitPolicy.Full);
        Assert.Null(after["endpoints"]);
        Assert.Equal("proxy", (string)after["route"]!["final"]!);
    }

    /* ── identity parsing ── */

    [Fact]
    public void Parses_singbox_wireguard_outbound()
    {
        var json = """
        { "type": "wireguard", "tag": "warp", "server": "162.159.192.1", "server_port": 2408,
          "local_address": ["172.16.0.2/32"], "private_key": "mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=",
          "peer_public_key": "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=", "reserved": [1, 2, 3], "mtu": 1280 }
        """;
        var w = WarpIdentity.TryParse(json, out var err);
        Assert.Null(err);
        Assert.NotNull(w);
        Assert.Equal("162.159.192.1", w!.EndpointHost);
        Assert.Equal(2408, w.EndpointPort);
        Assert.Equal([1, 2, 3], w.Reserved);
    }

    [Fact]
    public void Parses_plain_wireguard_conf_without_reserved()
    {
        const string conf = """
        [Interface]
        PrivateKey = mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=
        Address = 172.16.0.2/32, 2606:4700:110:8dbf:db31:18d0:5e33:ee61/128
        DNS = 1.1.1.1

        [Peer]
        PublicKey = bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = engage.cloudflareclient.com:2408
        """;
        var w = WarpIdentity.TryParse(conf, out var err);
        Assert.Null(err);
        Assert.Equal("172.16.0.2", w!.AddressV4);
        Assert.Null(w.Reserved); // a .conf cannot carry Cloudflare's reserved bytes
    }

    [Fact]
    public void Rejects_bad_keys()
    {
        Assert.Null(WarpIdentity.TryParse("{ \"type\":\"wireguard\", \"private_key\": \"nope\", \"local_address\":[\"172.16.0.2/32\"] }", out _));
        Assert.Null(WarpIdentity.TryParse("{ \"outbounds\": [ { \"type\":\"vless\" } ] }", out var e1));
        Assert.Equal("Chain.Error.NoWireguard", e1);
    }

    /* ── standalone combo export ── */

    [Fact]
    public void Builds_standalone_combo_for_share_links()
    {
        var cfg = ChainConfig.BuildStandalone(
            new TunnelProfile { Name = "ZEUS", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = WorkerVless },
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpGaming, Warp = Warp() }, out var err);
        Assert.Null(err);
        Assert.NotNull(cfg);
        Assert.Equal("proxy", (string)cfg!["route"]!["final"]!);
        Assert.Equal("wireguard", (string)Ob(cfg, ChainConfig.WarpTag)["type"]!);
        Assert.True(HasRule(cfg, r => (string?)r["network"] == "udp" && (string?)r["outbound"] == ChainConfig.WarpTag));
        Assert.Equal([], SingBoxConfigLinter.Lint(cfg).ToArray());
    }

    [Fact]
    public void Builds_standalone_combo_for_panel_jsons()
    {
        var profile = new TunnelProfile { Name = "koala", Engine = EngineKind.SingBox, Source = ProfileSource.SingBoxJson, Content = KoalaJson };
        var cfg = ChainConfig.BuildStandalone(profile,
            new ChainOptions { Enabled = true, Mode = ChainMode.WarpExit, Warp = Warp() }, out var err);
        Assert.Null(err);
        Assert.Equal(ChainConfig.WarpTag, (string)cfg!["route"]!["final"]!);
        Assert.Equal([], SingBoxConfigLinter.Lint(cfg).ToArray());
    }

    [Fact]
    public void Refuses_psiphon_and_disabled_exports()
    {
        var profile = new TunnelProfile { Name = "x", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = WorkerVless };
        Assert.Null(ChainConfig.BuildStandalone(profile, new ChainOptions { Enabled = true, Mode = ChainMode.Psiphon }, out var e1));
        Assert.Equal("Chain.Error.PsiphonExport", e1);
        Assert.Null(ChainConfig.BuildStandalone(profile, new ChainOptions { Enabled = false, Warp = Warp() }, out var e2));
        Assert.Equal("Chain.Error.Disabled", e2);
    }

    /* ── settings round-trip ── */

    [Fact]
    public void Settings_roundtrip_restores_the_identity()
    {
        var w = Warp(reserved: [9, 8, 7]);
        var store = new Dictionary<string, string>
        {
            ["chain.enabled"] = "1", ["chain.mode"] = "warpGaming",
            ["chain.warp.priv"] = w.PrivateKey, ["chain.warp.v4"] = w.AddressV4,
            ["chain.warp.reserved"] = "9,8,7", ["chain.warp.mtu"] = "1280", ["chain.psiphon.port"] = "1080",
        };
        var o = ChainOptions.FromSettings(k => store.TryGetValue(k, out var v) ? v : null);
        Assert.True(o.Enabled);
        Assert.Equal(ChainMode.WarpGaming, o.Mode);
        Assert.NotNull(o.Warp);
        Assert.Equal([9, 8, 7], o.Warp!.Reserved);
        Assert.Equal("172.16.0.2", o.Warp.AddressV4);
        Assert.True(o.IsRunnable);
    }

    [Fact]
    public void Pure_udp_rejects_are_removed_but_conditional_ones_survive()
    {
        var rules = new JsonArray(
            new JsonObject { ["network"] = "udp", ["action"] = "reject" },                 // panel generic — must go
            new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" }, // QUIC block — must stay
            new JsonObject { ["ip_version"] = 6, ["action"] = "reject" });                 // v6 block — must stay
        Assert.Equal(1, ChainConfig.RemovePureUdpRejects(rules));
        Assert.Equal(2, rules.Count);
    }

    // ---- v0.4.47.2 — `sing-box generate wg-keypair` changed shape in 1.13+: plain "PrivateKey: …" lines instead
    // of JSON. Both real outputs below were captured from actual binaries (1.12.x JSON, 1.14.4 lines).

    [Fact]
    public void Keypair_parses_the_new_plain_lines_form_singbox_1_14()
    {
        // verbatim stdout of sing-box 1.14.4 `generate wg-keypair`
        const string out114 =
            "PrivateKey: mPEVtVC2LGTenjYdt0CnprTbgY242JOciwdrID4ZsE8=\n" +
            "PublicKey: /PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=\n";
        var (priv, pub) = WarpIdentity.ParseKeypair(out114);
        Assert.Equal("mPEVtVC2LGTenjYdt0CnprTbgY242JOciwdrID4ZsE8=", priv);
        Assert.Equal("/PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=", pub);
    }

    [Fact]
    public void Keypair_parses_the_old_json_form_singbox_1_12()
    {
        const string out112 = "{\"private_key\":\"mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=\",\"public_key\":\"/PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=\"}";
        var (priv, pub) = WarpIdentity.ParseKeypair(out112);
        Assert.Equal("mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=", priv);
        Assert.Equal("/PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=", pub);
    }

    [Fact]
    public void Keypair_parses_lowercase_snake_lines_and_ignores_log_noise()
    {
        const string noisy =
            "INFO inbound: starting\n" +
            "private_key: mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=\n" +
            "public_key: /PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=\n";
        var (priv, pub) = WarpIdentity.ParseKeypair(noisy);
        Assert.NotNull(priv);
        Assert.NotNull(pub);
    }

    [Fact]
    public void Keypair_returns_null_on_unrecognized_output()
    {
        Assert.Equal((null, null), WarpIdentity.ParseKeypair("Error: unknown command \"wg-keypair\" for \"sing-box generate\"\nRun 'sing-box generate --help' for usage."));
        Assert.Equal((null, null), WarpIdentity.ParseKeypair(""));
        Assert.Equal((null, null), WarpIdentity.ParseKeypair("{\"private_key\":\"not-base64!!\",\"public_key\":\"/PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=\"}"));
    }

    [Fact]
    public void Scrub_keys_never_leaks_key_material_to_logs()
    {
        var scrubbed = WarpIdentity.ScrubKeys("PrivateKey: mPEVtVC2LGTenjYdt0CnprTbgY242JOciwdrID4ZsE8= oops /PJPU49dG6aialuh9L2Ff9ORNuLDzHpEQSjHrLGv4xI=");
        Assert.DoesNotContain("mPEVtVC2LGTenjYdt0", scrubbed);
        Assert.DoesNotContain("/PJPU49dG6aialuh", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
        Assert.Contains("oops", scrubbed);
    }
}
