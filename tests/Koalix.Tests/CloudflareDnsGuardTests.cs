using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.24 — the ZEUS/Cloudflare-Worker DNS guard. Worker-fronted tunnels exit inside Cloudflare's own
/// network, which cannot connect back to Cloudflare's resolvers (1.1.1.1) — the tunnel connects but no
/// DNS answer ever returns, so no site loads. The guard swaps the tunnel DNS to 8.8.8.8 for exactly
/// those profiles, and only while the configured resolver is a Cloudflare one.
/// </summary>
public class CloudflareDnsGuardTests
{
    private static TunnelProfile Profile(string content, ProfileSource source = ProfileSource.ShareLink, string name = "ZEUS") =>
        new() { Id = 1, Name = name, Engine = EngineKind.Xray, Source = source, Content = content };

    [Theory]
    [InlineData("vless://uuid@zeus-panel.workers.dev:443?security=tls&type=ws#ZEUS-1")]
    [InlineData("vless://uuid@104.21.5.10:443?security=tls&type=ws&host=my-sub.pages.dev#CF")]
    [InlineData("vless://uuid@104.21.5.10:443?security=tls&type=ws&sni=gate.workers.dev#CF")]
    [InlineData("trojan://pw@sub.pages.dev:443?security=tls&type=ws#edge")]
    public void Worker_links_are_detected(string link)
    {
        Assert.True(CloudflareDnsGuard.IsWorkerFronted(Profile(link), out _));
        Assert.Equal(CloudflareDnsGuard.FallbackDns, CloudflareDnsGuard.OverrideFor(Profile(link), "1.1.1.1", out _));
    }

    [Fact]
    public void Worker_token_inside_singbox_json_is_detected()
    {
        var json = """
        {
          "outbounds": [
            { "type": "vless", "tag": "w", "server": "104.21.5.10", "server_port": 443,
              "tls": { "enabled": true, "server_name": "gate.workers.dev" },
              "transport": { "type": "ws", "headers": { "Host": "gate.workers.dev" } } }
          ]
        }
        """;
        var p = Profile(json, ProfileSource.SingBoxJson);
        Assert.True(CloudflareDnsGuard.IsWorkerFronted(p, out var token));
        Assert.Equal("workers.dev", token);
    }

    [Fact]
    public void Worker_token_inside_xray_json_is_detected()
    {
        var json = """
        { "outbounds": [ { "protocol": "vless", "settings": { "vnext": [ { "address": "zeus.pages.dev", "port": 443 } ] } } ] }
        """;
        Assert.True(CloudflareDnsGuard.IsWorkerFronted(Profile(json, ProfileSource.XrayJson), out var token));
        Assert.Equal("pages.dev", token);
    }

    [Theory]
    [InlineData("vless://uuid@real-vps.example.com:443?security=tls&type=ws#home")]
    [InlineData("vless://uuid@1.2.3.4:443?security=tls&type=tcp#plain")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzcw==@1.2.3.4:8388#ss")]
    public void Normal_configs_are_left_alone(string link)
    {
        var p = Profile(link);
        Assert.False(CloudflareDnsGuard.IsWorkerFronted(p, out _));
        Assert.Null(CloudflareDnsGuard.OverrideFor(p, "1.1.1.1", out _));
    }

    /// <summary>A Cloudflare-CDN-fronted VPS (IP endpoint, custom SNI) still has a real origin that CAN reach 1.1.1.1 — no override.</summary>
    [Fact]
    public void Cdn_fronted_origin_with_custom_sni_is_left_alone()
    {
        var p = Profile("vless://uuid@104.21.5.10:443?security=tls&type=ws&host=mydomain.example.com&sni=mydomain.example.com#cdn");
        Assert.False(CloudflareDnsGuard.IsWorkerFronted(p, out _));
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("1.0.0.1", true)]
    [InlineData("  1.1.1.1 ", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("9.9.9.9", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_cloudflare_resolvers_count_as_the_problem(string? dns, bool expected)
    {
        Assert.Equal(expected, CloudflareDnsGuard.IsCloudflareDns(dns));
    }

    [Fact]
    public void Custom_non_cloudflare_dns_is_respected_even_for_worker_profiles()
    {
        var p = Profile("vless://uuid@zeus.workers.dev:443?security=tls&type=ws#ZEUS");
        Assert.True(CloudflareDnsGuard.IsWorkerFronted(p, out _));          // worker detected…
        Assert.Null(CloudflareDnsGuard.OverrideFor(p, "9.9.9.9", out _));   // …but the user's resolver already works from inside Cloudflare
    }

    [Fact]
    public void Worker_named_profiles_without_tokens_are_detected_via_share_link_hint()
    {
        // the endpoint is an IP but the link remark names the worker — the structured pass catches it
        var p = Profile("vless://uuid@1.2.3.4:443?security=tls&type=ws#my-worker-gate");
        Assert.True(CloudflareDnsGuard.IsWorkerFronted(p, out _));
    }

    [Fact]
    public void Guard_is_idempotent_after_the_swap()
    {
        var p = Profile("vless://uuid@zeus.workers.dev:443?security=tls&type=ws#ZEUS");
        var first = CloudflareDnsGuard.OverrideFor(p, "1.1.1.1", out _);
        Assert.Equal("8.8.8.8", first);
        // second call (StartAsync after ValidateAsync on the same engine): the DNS is no longer Cloudflare → no-op
        Assert.Null(CloudflareDnsGuard.OverrideFor(p, first, out _));
    }
}
