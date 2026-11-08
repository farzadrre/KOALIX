using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.Tunneling;
using Koalix.Xray;

namespace Koalix.Tests;

/// <summary>
/// v0.4.28 — the audited engine hardening, locked in config shape:
///  1. the Xray config carries an independent DoH DNS module;
///  2. TLS ClientHello fragmentation lands in streamSettings.sockopt for TLS/REALITY dials over TCP transports
///     (never for QUIC/mKCP — UDP has no ClientHello to split — and never when the user turns it off);
///  3. mux appears only for mux-capable protocol/transport combinations and only when opted in;
///  4. sing-box gets the poison-proof DoH resolver (route.default_domain_resolver → koalix-dns-doh) with NO
///     detour — sing-box (1.11+) FATALs at start on a DNS transport detoured to an EMPTY direct outbound
///     ("detour to an empty direct outbound makes no sense"), which killed every domain-profile connect in the
///     field (log 353) — and the configured TUN MTU reaches tun.mtu.
/// </summary>
public class EngineTweaksTests
{
    private static ProxyNode Node(string transport = "ws", string security = "tls", string? flow = null) =>
        new()
        {
            Protocol = ProxyProtocol.VLess, Name = "ZEUS", Server = "demo7sample0.samplezeus.workers.dev",
            Port = 443, Uuid = "50414e45-4c5f-5a45-5553-aaaabbbbcccc", Transport = transport, Security = security,
            Path = "/stream", Host = "demo7sample0.samplezeus.workers.dev", Sni = "demo7sample0.samplezeus.workers.dev",
            Flow = flow,
        };

    // ---- Xray: the independent DNS module --------------------------------------------

    [Fact]
    public void Xray_config_carries_the_doh_dns_module()
    {
        var cfg = XrayConfigBuilder.Build(Node(), 10808);
        var dns = cfg["dns"] as JsonObject;
        Assert.NotNull(dns);
        Assert.Equal("UseIPv4", (string?)dns!["queryStrategy"]);
        Assert.Equal("koalix-dns", (string?)dns["tag"]);
        var servers = dns["servers"] as JsonArray;
        Assert.NotNull(servers);
        Assert.True(servers!.Count >= 3);
        Assert.All(servers.OfType<JsonObject>(), s => Assert.StartsWith("https://", (string?)s["address"] ?? ""));
    }

    // ---- Xray: TLS fragment ------------------------------------------------------------

    [Fact]
    public void Fragment_lands_in_sockopt_for_tls_dials()
    {
        var o = XrayConfigBuilder.Outbound(Node(), new XrayTweaks(Fragment: true, Mux: false));
        var sockopt = o["streamSettings"]?["sockopt"] as JsonObject;
        Assert.NotNull(sockopt);
        Assert.Equal("tlshello", (string?)sockopt!["fragment"]?["packets"]);
        Assert.Equal("100-200", (string?)sockopt["fragment"]?["length"]);
    }

    [Fact]
    public void Fragment_also_applies_to_reality()
    {
        var n = Node(security: "reality");
        var o = XrayConfigBuilder.Outbound(n with { RealityPublicKey = "pubkey" }, new XrayTweaks(Fragment: true, Mux: false));
        Assert.NotNull(o["streamSettings"]?["sockopt"]?["fragment"]);
    }

    [Fact]
    public void Fragment_never_applies_to_udp_or_disabled_or_legacy()
    {
        Assert.Null(XrayConfigBuilder.Outbound(Node(transport: "quic", security: "tls"), new XrayTweaks(true, false))["streamSettings"]?["sockopt"]?["fragment"]);
        Assert.Null(XrayConfigBuilder.Outbound(Node(transport: "mkcp", security: "tls"), new XrayTweaks(true, false))["streamSettings"]?["sockopt"]?["fragment"]);
        Assert.Null(XrayConfigBuilder.Outbound(Node(), new XrayTweaks(Fragment: false, Mux: false))["streamSettings"]?["sockopt"]?["fragment"]);
        Assert.Null(XrayConfigBuilder.Outbound(Node())["streamSettings"]?["sockopt"]?["fragment"]);
    }

    // ---- Xray: v0.4.33 IPv4-first dial of the server's own name ------------------------

    [Fact]
    public void Domain_server_is_dialed_ipv4_first_and_merges_with_fragment()
    {
        Assert.Equal("UseIPv4", (string?)XrayConfigBuilder.Outbound(Node())["streamSettings"]?["sockopt"]?["domainStrategy"]);
        var both = XrayConfigBuilder.Outbound(Node(), new XrayTweaks(Fragment: true, Mux: false))["streamSettings"]?["sockopt"];
        Assert.Equal("UseIPv4", (string?)both?["domainStrategy"]);
        Assert.NotNull(both?["fragment"]);
    }

    [Fact]
    public void Server_name_is_pinned_in_dns_hosts_and_system_resolver_precedes_doh()
    {
        // v0.4.34 field bug: a DoH-only module + domainStrategy = every dial failed "failed to resolve ip"
        var cfg = XrayConfigBuilder.Build(Node(), 10808, serverIps: ["172.67.149.2", "104.21.29.126", "2606:4700::1"]);
        var hosts = cfg["dns"]?["hosts"]?["demo7sample0.samplezeus.workers.dev"] as JsonArray;
        Assert.NotNull(hosts);
        Assert.Equal(["172.67.149.2", "104.21.29.126"], hosts!.Select(h => (string)h!).ToArray()); // IPv4 only
        Assert.Equal("localhost", (string?)cfg["dns"]?["servers"]?[0]);
    }

    [Fact]
    public void No_hosts_pin_without_resolved_ips()
    {
        Assert.Null(XrayConfigBuilder.Build(Node(), 10808)["dns"]?["hosts"]);
        Assert.Null(XrayConfigBuilder.Build(Node() with { Server = "104.21.32.1" }, 10808, serverIps: ["1.2.3.4"])["dns"]?["hosts"]);
    }

    [Fact]
    public void Ip_literal_server_gets_no_domain_strategy()
    {
        var n = Node() with { Server = "104.21.32.1" };
        Assert.Null(XrayConfigBuilder.Outbound(n)["streamSettings"]?["sockopt"]);
    }

    // ---- Xray: mux ----------------------------------------------------------------------

    [Fact]
    public void Mux_applies_only_when_opted_in_and_supported()
    {
        var on = XrayConfigBuilder.Outbound(Node(), new XrayTweaks(false, Mux: true));
        Assert.True((bool?)on["mux"]?["enabled"]);

        Assert.Null(XrayConfigBuilder.Outbound(Node())["mux"]);                            // not opted in
        Assert.Null(XrayConfigBuilder.Outbound(Node(), new XrayTweaks(false, false))["mux"]); // explicit off
        // Vision flow must never be muxed
        Assert.Null(XrayConfigBuilder.Outbound(Node(flow: "xtls-rprx-vision"), new XrayTweaks(false, true))["mux"]);
        // XHTTP/mKCP transports must never be muxed
        Assert.Null(XrayConfigBuilder.Outbound(Node(transport: "xhttp"), new XrayTweaks(false, true))["mux"]);
        Assert.Null(XrayConfigBuilder.Outbound(Node(transport: "mkcp"), new XrayTweaks(false, true))["mux"]);
    }

    // ---- sing-box: poison-proof resolver + MTU -------------------------------------------

    private static SingBoxBuildOptions Options(bool doh = true, int mtu = 1500) => new()
    {
        HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s", ResolveViaDoh = doh, Mtu = mtu,
    };

    private static readonly SplitPolicy Policy = new(SplitMode.FullTunnel, []);

    [Fact]
    public void Singbox_routes_outbound_server_names_through_the_doh_resolver()
    {
        var cfg = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(Node()), false, Policy, Options(doh: true));
        Assert.Equal(SingBoxConfigBuilder.DohDnsTag, (string?)cfg["route"]?["default_domain_resolver"]);
        var server = ((cfg["dns"]?["servers"] as JsonArray) ?? []).OfType<JsonObject>()
            .FirstOrDefault(s => (string?)s["tag"] == SingBoxConfigBuilder.DohDnsTag);
        Assert.NotNull(server);
        Assert.Equal("8.8.8.8", (string?)server!["server"]);
        Assert.Equal("https", (string?)server["type"]);
    }

    [Fact]
    public void Singbox_doh_resolver_never_detours_to_the_empty_direct_outbound()
    {
        // v0.4.29 field regression (log 353): the v0.4.28 DoH server carried "detour": "direct" — and sing-box
        // refuses to start with FATAL "start dns-transport: start dns/https[koalix-dns-doh]: detour to an empty
        // direct outbound makes no sense". Every connect of a domain-based profile died at engine start; the
        // IP-based profile (which skips the DoH injection) connected fine seconds later. The detour is also
        // redundant: DNS transports dial the physical interface directly (auto_detect_interface), never the tunnel.
        Assert.Null(((SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(Node()), false, Policy, Options(doh: true))
            ["dns"]?["servers"] as JsonArray) ?? []).OfType<JsonObject>()
            .First(s => (string?)s["tag"] == SingBoxConfigBuilder.DohDnsTag)["detour"]);

        // merged panel configs get the same detour-free shape (and the linter stays clean — a "direct" detour on
        // a config without a direct outbound would also trip Lint.UnknownOutbound at engine start).
        var user = JsonNode.Parse("""
        {
          "outbounds": [{ "type": "vless", "tag": "proxy", "server": "panel.example.com", "server_port": 443, "uuid": "50414e45-4c5f-5a45-5553-aaaabbbbcccc" }],
          "route": { "final": "proxy" }
        }
        """)!.AsObject();
        var merged = SingBoxConfigBuilder.MergeUserConfig(user, "proxy", Policy, Options(doh: true));
        Assert.Null(((merged["dns"]?["servers"] as JsonArray) ?? []).OfType<JsonObject>()
            .First(s => (string?)s["tag"] == SingBoxConfigBuilder.DohDnsTag)["detour"]);
        Assert.Empty(SingBoxConfigLinter.Lint(merged));
    }

    [Fact]
    public void Singbox_keeps_the_local_resolver_without_the_doh_option()
    {
        var cfg = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(Node()), false, Policy, Options(doh: false));
        Assert.Equal("dns-local", (string?)cfg["route"]?["default_domain_resolver"]);
        Assert.DoesNotContain(((cfg["dns"]?["servers"] as JsonArray) ?? []).OfType<JsonObject>(),
            s => (string?)s["tag"] == SingBoxConfigBuilder.DohDnsTag);
    }

    [Fact]
    public void Tun_mtu_reaches_the_config()
    {
        var cfg = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(Node()), false, Policy, Options(mtu: 1380));
        Assert.Equal(1380, (int?)cfg["inbounds"]![0]!["mtu"]);
    }

    [Fact]
    public void Merged_user_configs_get_the_doh_resolver_only_when_the_author_did_not_pick_one()
    {
        var user = JsonNode.Parse("""
        {
          "outbounds": [{ "type": "vless", "tag": "proxy", "server": "panel.example.com", "server_port": 443, "uuid": "50414e45-4c5f-5a45-5553-aaaabbbbcccc" }],
          "route": { "final": "proxy" }
        }
        """)!.AsObject();
        var cfg = SingBoxConfigBuilder.MergeUserConfig(user, "proxy", Policy, Options(doh: true));
        Assert.Equal(SingBoxConfigBuilder.DohDnsTag, (string?)cfg["route"]?["default_domain_resolver"]);

        var withOwn = JsonNode.Parse("""
        {
          "outbounds": [{ "type": "vless", "tag": "proxy", "server": "panel.example.com", "server_port": 443, "uuid": "50414e45-4c5f-5a45-5553-aaaabbbbcccc" }],
          "dns": { "servers": [{ "type": "https", "tag": "my-own", "server": "9.9.9.9" }] },
          "route": { "final": "proxy", "default_domain_resolver": "my-own" }
        }
        """)!.AsObject();
        var cfg2 = SingBoxConfigBuilder.MergeUserConfig(withOwn, "proxy", Policy, Options(doh: true));
        Assert.Equal("my-own", (string?)cfg2["route"]?["default_domain_resolver"]); // the author's intent wins
    }
}
