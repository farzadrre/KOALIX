using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Koalix.Tunneling;

public sealed record HealthResult(bool Ok, double? LatencyMs, string? Error);

/// <summary>Performs controlled connectivity tests either through the engine's private health proxy or over the direct physical interface.</summary>
public sealed class HealthChecker
{
    /// <summary>
    /// v0.4.28 — probes are HTTPS now: the old http:// probes tested port 80 while the real tunnel traffic runs
    /// TLS on 443 — the field log showed the probe SUCCEEDING on 80 while the 443 handshake was being EOF'd, so
    /// the app said "connected" over a tunnel that carried nothing. Testing the same layer as the real traffic
    /// (TLS over the engine proxy) makes the health verdict honest; the Degraded thresholds absorb the extra
    /// TLS-handshake latency (default 900 ms, worker plan 2500 ms).
    /// </summary>
    public static readonly Uri ProbeUrl = new("https://cp.cloudflare.com/generate_204");
    /// <summary>Second probe when the first gives no answer (some exits block or hijack cp.cloudflare.com).</summary>
    public static readonly Uri FallbackProbeUrl = new("https://www.gstatic.com/generate_204");
    public static readonly Uri[] PublicIpUrls = [new("https://api.ipify.org"), new("https://icanhazip.com")];

    /// <summary>The default probe chain: Cloudflare first (fast when reachable), Google as the fallback.</summary>
    public static readonly Uri[] DefaultProbeUrls = [ProbeUrl, FallbackProbeUrl];

    /// <summary>
    /// v0.4.27 (ZEUS worker configs, round 2 — «وصل میشن ولی بعد ۴۰ ثانیه قطه»): a tunnel whose exit lives
    /// INSIDE a Cloudflare Worker can never reach Cloudflare's own network, so the cp.cloudflare.com primary
    /// probe burns its full timeout on EVERY check — connects turn slow and flappy, the monitor keeps marking
    /// the tunnel Degraded, and the 40-second auto-failover cuts a perfectly working connection. The same
    /// class of problem the DNS guard (v0.4.24) fixed for resolvers. Worker-fronted connections probe
    /// Google directly instead — both endpoints are plain HTTP 204 generators reachable from inside Cloudflare.
    /// </summary>
    public static readonly Uri[] WorkerProbeUrls = [FallbackProbeUrl, new("https://connectivitycheck.gstatic.com/generate_204")];

    /// <summary>
    /// v0.4.27 — the per-connection health plan. Worker-fronted profiles get the Cloudflare-free probe chain
    /// and a latency threshold that matches their topology (Iran → worker → target is routinely 400–1500 ms;
    /// the flat 600 ms default called those healthy links "Degraded" and armed the auto-failover against them).
    /// Returns the effective probe list and the effective Degraded latency in milliseconds.
    /// </summary>
    public static (IReadOnlyList<Uri> Probes, int DegradedMs, bool WorkerFronted, string Token) ProbePlan(TunnelProfile profile, int configuredDegradedMs)
    {
        if (CloudflareDnsGuard.IsWorkerFronted(profile, out var token))
            return (WorkerProbeUrls, Math.Max(configuredDegradedMs, WorkerDegradedLatencyMs), true, token);
        return (DefaultProbeUrls, configuredDegradedMs, false, "");
    }

    /// <summary>Worker-fronted links are judged with this ceiling before they may be called Degraded (v0.4.27).</summary>
    public const int WorkerDegradedLatencyMs = 2500;

    public async Task<HealthResult> CheckThroughProxyAsync(int proxyPort, TimeSpan timeout, CancellationToken ct, IReadOnlyList<Uri>? probeUrls = null)
    {
        using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true, PooledConnectionLifetime = TimeSpan.Zero };
        return await ProbeAsync(handler, timeout, ct, probeUrls);
    }

    public async Task<HealthResult> CheckDirectAsync(TimeSpan timeout, CancellationToken ct, IReadOnlyList<Uri>? probeUrls = null)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        return await ProbeAsync(handler, timeout, ct, probeUrls);
    }

    private static async Task<HealthResult> ProbeAsync(HttpMessageHandler handler, TimeSpan timeout, CancellationToken ct, IReadOnlyList<Uri>? probeUrls = null)
    {
        using var http = new HttpClient(handler, false) { Timeout = timeout };
        string? lastError = null;
        foreach (var url in probeUrls ?? DefaultProbeUrls)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                // 5xx (502 Bad Gateway) is what the local engine proxy itself answers when the upstream is dead.
                if ((int)r.StatusCode >= 500) { lastError = $"HTTP {(int)r.StatusCode}"; continue; }
                // Any other HTTP answer proves the tunnel carries traffic end-to-end. Some exits/CDNs answer 404/403 instead of 204 —
                // that must not be treated as a dead tunnel (it previously tore down working VLESS/Trojan connections).
                return new(true, sw.Elapsed.TotalMilliseconds, r.StatusCode == HttpStatusCode.NoContent ? null : $"HTTP {(int)r.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { lastError = ex.Message; if (ct.IsCancellationRequested) break; }
        }
        return new(false, null, lastError);
    }

    /// <summary>Public egress IP through the tunnel (via private health proxy).</summary>
    public Task<string?> PublicIpViaProxyAsync(int proxyPort, CancellationToken ct) =>
        PublicIpAsync(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true }, ct);

    /// <summary>
    /// Public egress IP over a specific physical interface: the socket is bound to the interface's local address,
    /// so the result reflects the DIRECT path even while a TUN adapter owns the default route.
    /// </summary>
    public Task<string?> PublicIpDirectAsync(IPAddress? bindAddress, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        if (bindAddress is not null)
            handler.ConnectCallback = async (ctx, token) =>
            {
                var s = new Socket(bindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    s.Bind(new IPEndPoint(bindAddress, 0));
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, bindAddress.AddressFamily, token);
                    await s.ConnectAsync(addrs, ctx.DnsEndPoint.Port, token);
                    return new NetworkStream(s, true);
                }
                catch { s.Dispose(); throw; }
            };
        return PublicIpAsync(handler, ct);
    }

    private static async Task<string?> PublicIpAsync(HttpMessageHandler handler, CancellationToken ct)
    {
        using var http = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(8) };
        foreach (var u in PublicIpUrls)
        {
            try
            {
                var s = (await http.GetStringAsync(u, ct)).Trim();
                if (IPAddress.TryParse(s, out _)) return s;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        return null;
    }
}
