using System.Text.Json.Nodes;
using Koalix.SingBox;
using Koalix.Tunneling;
using Koalix.Xray;

namespace Koalix.Tests;

/// <summary>
/// v0.4.22 — the ZEUS-panel subscription round (user: «فقط این نوع کانفیگ و مشکل داره … با کلاینت های دیگه
/// وصل میشن ولی اینجا نمیشه»). Two field facts drive every test here:
///  1. every link carries <c>fp=unsafe</c> — Xray-core FATALs on it («initialize outbound[0]: unknown uTLS
///     fingerprint: unsafe», logs.txt) while other clients silently drop the field and connect;
///  2. the subscription is padded with placeholder rows pointing at 0.0.0.0:1 (volume reminders, ads, bot
///     promos) that can never connect and only pollute the server list.
/// </summary>
public class UtlsAndNoticeTests
{
    // ── the user's EXACT pasted subscription (7 links: 4 real servers + 3 panel notices) ──
    private const string ZeusSubscription = """
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@0.0.0.0:1?encryption=none&security=none&type=ws&host=demo7sample0.samplezeus.workers.dev&path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc#%F0%9F%93%8A%20remaining%20%7C%20%E2%80%8E90000.00GB%20%7C%20%E2%80%8E90Days%20(Not%20Started)%20%7C%20%E2%80%8E%E2%88%9E
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443?path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc%2Floc-0&security=tls&encryption=none&host=demo7sample0.samplezeus.workers.dev&type=ws&insecure=0&fp=unsafe&allowInsecure=0&sni=demo7sample0.samplezeus.workers.dev#ZEUS%20%7C%20%F0%9F%87%BA%F0%9F%87%B8%20%7C%20man
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443?path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc%2Floc-1&security=tls&encryption=none&host=demo7sample0.samplezeus.workers.dev&type=ws&insecure=0&fp=unsafe&allowInsecure=0&sni=demo7sample0.samplezeus.workers.dev#ZEUS%20%7C%20%F0%9F%87%BA%F0%9F%87%B8%20%7C%20man
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443?path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc%2Floc-2&security=tls&encryption=none&host=demo7sample0.samplezeus.workers.dev&type=ws&insecure=0&fp=unsafe&allowInsecure=0&sni=demo7sample0.samplezeus.workers.dev#ZEUS%20%7C%20%F0%9F%87%A9%F0%9F%87%AA%20%7C%20man
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443?path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc&security=tls&encryption=none&host=demo7sample0.samplezeus.workers.dev&type=ws&insecure=0&fp=unsafe&allowInsecure=0&sni=demo7sample0.samplezeus.workers.dev#ZEUS%20%7C%20%F0%9F%8C%90%20%7C%20man
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@0.0.0.0:1?encryption=none&security=none&type=ws&host=demo7sample0.samplezeus.workers.dev&path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc#%E2%9A%A0%EF%B8%8F%20%D9%BE%D9%86%D9%84%20%D8%B1%D8%A7%DB%8C%DA%AF%D8%A7%D9%86%20%D9%88%20%D8%BA%DB%8C%D8%B1%20%D9%82%D8%A7%D8%A8%D9%84%20%D9%81%D8%B1%D9%88%D8%B4%20%E2%9A%A0%EF%B8%8F
        vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@0.0.0.0:1?encryption=none&security=none&type=ws&host=demo7sample0.samplezeus.workers.dev&path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc#%F0%9F%9A%80%D8%B3%D8%A7%D8%AE%D8%AA%20%D8%B1%D8%A7%DB%8C%DA%AF%D8%A7%D9%86%20%40ZEUS_PANEL_BOT%20%F0%9F%9A%80
        """;

    private static ProxyNode FirstReal() => ShareLinkParser.ParseMany(ZeusSubscription).Nodes[0];

    [Fact]
    public void Zeus_subscription_parses_to_four_real_servers_and_skips_three_notices()
    {
        var r = ShareLinkParser.ParseMany(ZeusSubscription);
        Assert.Empty(r.Errors);
        Assert.Equal(4, r.Nodes.Count);          // loc-0, loc-1, loc-2, the root path
        Assert.Equal(3, r.Notices);              // remaining-volume row, "free panel" ad, bot promo
        Assert.All(r.Nodes, n => Assert.Equal("demo7sample0.samplezeus.workers.dev", n.Server));
        Assert.All(r.Nodes, n => Assert.Equal(443, n.Port));
        Assert.All(r.Nodes, n => Assert.Empty(n.Validate()));
        // the three kept paths are exactly the panel's real entry points
        Assert.Equal(new[] { "/stream/PANEL_ZEUS/aaaabbbbcccc/loc-0", "/stream/PANEL_ZEUS/aaaabbbbcccc/loc-1", "/stream/PANEL_ZEUS/aaaabbbbcccc/loc-2", "/stream/PANEL_ZEUS/aaaabbbbcccc" },
            r.Nodes.Select(n => n.Path).ToArray());
    }

    [Fact]
    public void Placeholder_notices_are_detected()
    {
        Assert.True(ShareLinkParser.IsPlaceholderNotice(ShareLinkParser.Parse(
            "vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@0.0.0.0:1?security=none&type=ws&path=%2F#ad")));
        Assert.False(ShareLinkParser.IsPlaceholderNotice(ShareLinkParser.Parse(
            "vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443?security=tls&type=ws&path=%2F#real")));
    }

    [Fact]
    public void Unsafe_fingerprint_is_sanitized_to_chrome_for_both_cores()
    {
        // THE crash: xray.exe died with "initialize outbound[0]: unknown uTLS fingerprint: unsafe" before this fix.
        var n = FirstReal();
        Assert.Equal("unsafe", n.Fingerprint);      // the raw parsed value is preserved…
        Assert.Equal("chrome", n.SafeFingerprint);  // …but no core ever sees anything unknown

        var xray = XrayConfigBuilder.Outbound(n);
        Assert.Equal("chrome", (string?)xray["streamSettings"]!["tlsSettings"]!["fingerprint"]);

        var sb = SingBoxConfigBuilder.Outbound(n);
        Assert.Equal("chrome", (string?)sb["tls"]!["utls"]!["fingerprint"]);
    }

    [Fact]
    public void Known_fingerprints_pass_through_untouched()
    {
        foreach (var fp in Utls.KnownFingerprints)
            Assert.Equal(fp, Utls.Normalize(fp));
        // case/whitespace tolerance — links in the wild are sloppy
        Assert.Equal("firefox", Utls.Normalize(" Firefox "));
        Assert.Equal("ios", Utls.Normalize("IOS"));
    }

    [Fact]
    public void Unknown_and_missing_fingerprints_fall_back_to_chrome()
    {
        Assert.Equal("chrome", Utls.Normalize("unsafe"));
        Assert.Equal("chrome", Utls.Normalize("chrome123-not-a-thing"));
        Assert.Equal("chrome", Utls.Normalize(""));
        Assert.Equal("chrome", Utls.Normalize(null));
    }

    [Fact]
    public void Xray_reality_links_also_never_carry_an_invalid_fingerprint()
    {
        var n = ShareLinkParser.Parse(
            "vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@example.com:443?security=reality&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&sid=ab12&fp=unsafe&sni=example.com&type=tcp#reality");
        var xray = XrayConfigBuilder.Outbound(n);
        Assert.Equal("chrome", (string?)xray["streamSettings"]!["realitySettings"]!["fingerprint"]);
        var sb = SingBoxConfigBuilder.Outbound(n);
        Assert.Equal("chrome", (string?)sb["tls"]!["utls"]!["fingerprint"]);
    }

    [Fact]
    public void A_subscription_of_only_notices_counts_as_empty()
    {
        // the refresh path keeps old servers when Nodes.Count == 0 — a feed of pure ads must not wipe them
        var r = ShareLinkParser.ParseMany(
            "vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@0.0.0.0:1?security=none&type=ws&path=%2F#ad1\n" +
            "vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@0.0.0.0:1?security=none&type=ws&path=%2F#ad2");
        Assert.Empty(r.Nodes);
        Assert.Equal(2, r.Notices);
    }
}
