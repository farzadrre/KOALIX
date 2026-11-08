using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.Tunneling;
using Xunit;

namespace Koalix.Tests;

/// <summary>v0.4.35 — the proxy server's own addresses are routed outside the TUN (log koalix-20261108).</summary>
public class ServerBypassTests
{
    private static SingBoxBuildOptions Opts(params string[] ips) =>
        new() { HealthPort = 18081, ClashApiPort = 19091, ClashApiSecret = "s", BypassServerIps = ips };

    private static JsonObject Tun(JsonObject cfg) =>
        cfg["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => (string?)i["type"] == "tun");

    [Fact]
    public void Server_ips_become_tun_route_exclusions()
    {
        var node = ShareLinkParser.Parse("vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443?type=ws&security=tls&path=%2F#w");
        var cfg = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, new SplitPolicy(SplitMode.FullTunnel, []),
            Opts("172.67.149.2", "104.21.29.126", "172.67.149.2", "2606:4700:3030::6815:1d7e"));
        var ex = Tun(cfg)["route_exclude_address"]!.AsArray().Select(x => (string)x!).ToArray();
        Assert.Equal(["172.67.149.2/32", "104.21.29.126/32", "2606:4700:3030::6815:1d7e/128"], ex);
    }

    [Fact]
    public void No_exclusion_without_server_ips()
    {
        var node = ShareLinkParser.Parse("vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@1.2.3.4:443?type=ws&security=tls#w");
        var cfg = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, new SplitPolicy(SplitMode.FullTunnel, []), Opts());
        Assert.Null(Tun(cfg)["route_exclude_address"]);
    }

    [Theory]
    [InlineData("10.10.34.35")]      // ISP sinkhole
    [InlineData("192.168.1.1")]
    [InlineData("172.19.0.1")]       // our own TUN address
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("fdfe:dcba:9876::1")]
    [InlineData("fe80::1")]
    [InlineData("not-an-ip")]
    public void Private_or_bogus_addresses_never_bypass(string ip) =>
        Assert.Empty(SingBoxBuildOptions.BypassCidrs([ip]));

    [Fact]
    public void Merged_user_config_gets_the_same_exclusions()
    {
        var user = JsonNode.Parse("""{"outbounds":[{"type":"vless","tag":"proxy","server":"1.2.3.4","server_port":443,"uuid":"50414e45-4c5f-5a45-5553-aaaabbbbcccc"}]}""")!.AsObject();
        var cfg = SingBoxConfigBuilder.MergeUserConfig(user, "proxy", new SplitPolicy(SplitMode.FullTunnel, []), Opts("1.2.3.4"));
        Assert.Equal("1.2.3.4/32", (string?)Tun(cfg)["route_exclude_address"]?[0]);
    }
}
