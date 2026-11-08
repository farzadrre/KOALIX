using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.27 — the worker-fronted health-probe plan («کانفیگ‌های ویتوری وصل میشن ولی بعد ۴۰ ثانیه قطه»).
///
/// A tunnel that exits INSIDE a Cloudflare Worker can never reach Cloudflare's own network —
/// the old primary probe (cp.cloudflare.com/generate_204) therefore burned its full timeout on
/// EVERY health check of a ZEUS workers.dev config: connects turned slow and flappy, the monitor
/// kept marking healthy tunnels Degraded (the flat 600 ms threshold also misjudged the normal
/// Iran → worker → target latency), and the 40-second auto-failover cut working connections.
/// The plan gives worker-fronted profiles a Cloudflare-free probe chain and a fair threshold.
/// </summary>
public class HealthProbePlanTests
{
    private static TunnelProfile Profile(string content, ProfileSource source = ProfileSource.ShareLink) =>
        new() { Id = 1, Name = "ZEUS", Engine = EngineKind.Xray, Source = source, Content = content };

    // The exact link shape from the user's report: vless + ws + tls, host/sni = the worker domain,
    // path = /stream/PANEL_ZEUS/<id>/loc-0, fp=unsafe — only the UUID is replaced.
    private const string ZeusLink =
        "vless://50414e45-4c5f-5a45-5553-aaaabbbbcccc@demo7sample0.samplezeus.workers.dev:443" +
        "?path=%2Fstream%2FPANEL_ZEUS%2Faaaabbbbcccc%2Floc-0&security=tls&encryption=none" +
        "&host=demo7sample0.samplezeus.workers.dev&type=ws&insecure=0&fp=unsafe&allowInsecure=0" +
        "&sni=demo7sample0.samplezeus.workers.dev#ZEUS%20%7C%20man";

    [Fact]
    public void The_reported_zeus_link_gets_the_worker_plan()
    {
        var (probes, degradedMs, worker, token) = HealthChecker.ProbePlan(Profile(ZeusLink), 600);
        Assert.True(worker);
        Assert.Equal("workers.dev", token);
        Assert.Equal(HealthChecker.WorkerDegradedLatencyMs, degradedMs);
        Assert.DoesNotContain(probes, u => u.Host.EndsWith("cloudflare.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("www.gstatic.com", probes[0].Host); // Google first — reachable from inside a Worker
    }

    [Theory]
    [InlineData("vless://uuid@zeus-panel.workers.dev:443?security=tls&type=ws#ZEUS-1")]
    [InlineData("trojan://pw@sub.pages.dev:443?security=tls&type=ws#edge")]
    public void Worker_links_never_probe_cloudflare(string link)
    {
        var (probes, _, worker, _) = HealthChecker.ProbePlan(Profile(link), 600);
        Assert.True(worker);
        Assert.DoesNotContain(probes, u => u.Host.EndsWith("cloudflare.com", StringComparison.OrdinalIgnoreCase));
        Assert.All(probes, u => Assert.EndsWith("/generate_204", u.AbsolutePath, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("vless://uuid@real-vps.example.com:443?security=tls&type=ws#home")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzcw==@1.2.3.4:8388#ss")]
    public void Normal_links_keep_the_default_plan_untouched(string link)
    {
        var (probes, degradedMs, worker, _) = HealthChecker.ProbePlan(Profile(link), 600);
        Assert.False(worker);
        Assert.Equal(600, degradedMs);                 // the configured threshold is respected exactly
        Assert.Same(HealthChecker.DefaultProbeUrls, probes);
        Assert.Equal("cp.cloudflare.com", probes[0].Host); // fast Cloudflare primary stays for everyone else
    }

    /// <summary>A user-configured threshold above the worker floor must win — never lowered by the plan.</summary>
    [Fact]
    public void A_higher_configured_threshold_is_never_lowered_for_workers()
    {
        var p = Profile("vless://uuid@zeus.workers.dev:443?security=tls&type=ws#ZEUS");
        var (_, degradedMs, _, _) = HealthChecker.ProbePlan(p, 4000);
        Assert.Equal(4000, degradedMs);
        var (_, low, _, _) = HealthChecker.ProbePlan(p, 600);
        Assert.Equal(HealthChecker.WorkerDegradedLatencyMs, low); // …but a tight default is relaxed for workers
    }

    /// <summary>The worker chain itself: no Cloudflare endpoints, every URL an HTTPS 204 generator (v0.4.28 —
    /// the probe must exercise the same TLS-on-443 layer the real traffic uses, not a lying port-80 HTTP check).</summary>
    [Fact]
    public void Worker_probe_chain_is_cloudflare_free_and_https()
    {
        Assert.All(HealthChecker.WorkerProbeUrls, u =>
        {
            Assert.DoesNotContain("cloudflare", u.Host, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("https", u.Scheme);
            Assert.EndsWith("/generate_204", u.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(HealthChecker.WorkerProbeUrls, u => u.Host == "www.gstatic.com");
        Assert.Equal(2, HealthChecker.WorkerProbeUrls.Length); // primary + one fallback, no dead weight
    }

    /// <summary>Default chain unchanged for everyone else: Cloudflare primary, Google fallback (v0.4.x behavior).</summary>
    [Fact]
    public void Default_chain_still_starts_with_the_cloudflare_primary()
    {
        Assert.Equal(HealthChecker.ProbeUrl, HealthChecker.DefaultProbeUrls[0]);
        Assert.Equal(HealthChecker.FallbackProbeUrl, HealthChecker.DefaultProbeUrls[1]);
    }

    /// <summary>v0.4.28 — every probe (default AND worker) is TLS on 443: the old http:// probes tested port 80
    /// while real traffic dies on the 443 handshake — the app said "connected" over a dead tunnel.</summary>
    [Fact]
    public void Every_probe_url_is_https_same_layer_as_real_traffic()
    {
        Assert.All(HealthChecker.DefaultProbeUrls, u => Assert.Equal("https", u.Scheme));
        Assert.All(HealthChecker.WorkerProbeUrls, u => Assert.Equal("https", u.Scheme));
    }
}
