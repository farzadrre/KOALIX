using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>v0.4.17 — the panel's subscription-userinfo header (volume + expiry) and the user's exact VLESS link shape.</summary>
public class SubUserInfoTests
{
    [Fact]
    public void Parses_full_header()
    {
        var u = SubUserInfo.Parse("upload=1234; download=5678; total=10240; expire=1900000000");
        Assert.NotNull(u);
        Assert.Equal(1234, u.UploadBytes);
        Assert.Equal(5678, u.DownloadBytes);
        Assert.Equal(10240, u.TotalBytes);
        Assert.Equal(1900000000, u.ExpireUnix);
        Assert.Equal(6912, u.UsedBytes);
        Assert.False(u.VolumeExhausted);
        Assert.False(u.Expired);
    }

    [Fact]
    public void Volume_exhausted_when_used_reaches_total()
    {
        var u = SubUserInfo.Parse("upload=5000; download=5000; total=10000");
        Assert.NotNull(u);
        Assert.True(u.VolumeExhausted);
    }

    [Fact]
    public void Expired_when_expire_in_the_past()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
        var u = SubUserInfo.Parse("expire=" + past);
        Assert.NotNull(u);
        Assert.True(u.Expired);
    }

    [Fact]
    public void Tolerates_partial_and_messy_headers()
    {
        // panels in the wild: missing fields, unknown fields, spaces, quoted values, trailing separators
        var u = SubUserInfo.Parse(" download=2048 ; extra=\"nope\"; total=4096;; ");
        Assert.NotNull(u);
        Assert.Null(u.UploadBytes);
        Assert.Equal(2048, u.DownloadBytes);
        Assert.Equal(4096, u.TotalBytes);
        Assert.Null(u.ExpireUnix);
        Assert.Equal(2048, u.UsedBytes);
    }

    [Fact]
    public void Null_for_empty_or_nonsense()
    {
        Assert.Null(SubUserInfo.Parse(null));
        Assert.Null(SubUserInfo.Parse(""));
        Assert.Null(SubUserInfo.Parse("   "));
        Assert.Null(SubUserInfo.Parse("garbage-without-equals"));
        Assert.Null(SubUserInfo.Parse("upload=abc")); // no parseable field at all
    }

    // ── the user's exact field-report link shape (v0.4.17): ws path, sni, host header, fragment name ──
    private const string SampleVless =
        "vless://50414e45-4c5f-5a45-5553-ddddeeeeffff@45.131.209.24:443?encryption=none&security=tls&sni=demo7sample0.samplezeus.workers.dev&fp=unsafe&insecure=0&allowInsecure=0&type=ws&host=demo7sample0.samplezeus.workers.dev&path=%2Fstream%2FPANEL_ZEUS%2Fddddeeeeffff%2Floc-0#ajaaaaaaaaaaabbbbbbbbbb";

    [Fact]
    public void Parses_the_reported_vless_ws_tls_link()
    {
        var node = ShareLinkParser.Parse(SampleVless);
        Assert.Equal(ProxyProtocol.VLess, node.Protocol);
        Assert.Equal("45.131.209.24", node.Server);
        Assert.Equal(443, node.Port);
        Assert.Equal("50414e45-4c5f-5a45-5553-ddddeeeeffff", node.Uuid);
        Assert.Equal("tls", node.Security);
        Assert.Equal("demo7sample0.samplezeus.workers.dev", node.Sni);
        Assert.Equal("demo7sample0.samplezeus.workers.dev", node.Host);
        Assert.Equal("ws", node.Transport);
        Assert.Equal("/stream/PANEL_ZEUS/ddddeeeeffff/loc-0", node.Path);
        Assert.Equal("unsafe", node.Fingerprint);
        Assert.Equal("ajaaaaaaaaaaabbbbbbbbbb", node.Name);
        Assert.False(node.AllowInsecure); // insecure=0 and allowInsecure=0 must both read as "no"
        Assert.Empty(node.Validate());
    }

    [Fact]
    public void Vless_ws_tls_is_natively_supported_by_singbox()
    {
        // the reason every subscription link showed up as "SingBox": vless+ws+tls IS sing-box native — the
        // per-subscription engine override (v0.4.17) is what lets the user force Xray instead.
        var node = ShareLinkParser.Parse(SampleVless);
        Assert.True(SingBox.SingBoxConfigBuilder.IsNativelySupported(node, out _));
    }
}
