using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Koalix.Dns;

public sealed record LatencyStats(int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs, double? MedianMs, double? JitterMs)
{
    /// <summary>Packet loss = (transmitted - valid responses) / transmitted.</summary>
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;

    /// <summary>
    /// Jitter = sum(|x[i] - x[i-1]|) / (n - 1) over consecutive VALID measurements, in the order they were measured.
    /// Null when fewer than two valid measurements exist (jitter is unknown, not zero).
    /// </summary>
    public static double? Jitter(IReadOnlyList<double> orderedSamples)
    {
        if (orderedSamples.Count < 2) return null;
        double sum = 0;
        for (var i = 1; i < orderedSamples.Count; i++) sum += Math.Abs(orderedSamples[i] - orderedSamples[i - 1]);
        return sum / (orderedSamples.Count - 1);
    }

    public static double? Median(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return null;
        var s = samples.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    public static LatencyStats From(int sent, IReadOnlyList<double> orderedSamples)
    {
        if (orderedSamples.Count == 0) return new(sent, 0, null, null, null, null, null);
        return new(sent, orderedSamples.Count, orderedSamples.Min(), orderedSamples.Average(), orderedSamples.Max(), Median(orderedSamples), Jitter(orderedSamples));
    }
}

public sealed record DnsServerTestResult(string Server, LatencyStats Icmp, LatencyStats DnsQuery, int DnsSuccess, int DnsAttempts, int DnsValidResponses, int DnsTimeouts, int TcpFallbacks, string? LastDnsError)
{
    /// <summary>ICMP can be blocked while DNS still works (and vice versa); these are reported separately.</summary>
    public bool IcmpReachable => Icmp.Received > 0;
    public bool DnsWorking => DnsSuccess > 0;
    public double DnsSuccessRate => DnsAttempts == 0 ? 0 : DnsSuccess * 100.0 / DnsAttempts;
    public double DnsValidResponseRate => DnsAttempts == 0 ? 0 : DnsValidResponses * 100.0 / DnsAttempts;
    public double DnsTimeoutRate => DnsAttempts == 0 ? 0 : DnsTimeouts * 100.0 / DnsAttempts;
}

public sealed record DnsProfileTestResult(DnsProfile Profile, IReadOnlyList<DnsServerTestResult> Servers, double Score, RankingStrategy Strategy);

public sealed record DnsTestOptions
{
    public int PingCount { get; init; } = 5;
    public int DnsQueries { get; init; } = 6;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(200);
    public int MaxConcurrency { get; init; } = 4;
    public bool TcpFallback { get; init; } = true;
    public string[] TestDomains { get; init; } = ["www.google.com", "www.cloudflare.com", "www.microsoft.com", "www.wikipedia.org"];
    public RankingStrategy Strategy { get; init; } = RankingStrategy.BalancedGaming;
}

public interface IIcmpProber
{
    Task<double?> PingOnceAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct);
}

public sealed class IcmpProber : IIcmpProber
{
    public async Task<double?> PingOnceAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var r = await ping.SendPingAsync(ip, timeout, null, null, ct);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }
}

public sealed class DnsTester(DnsQueryClient client, IIcmpProber icmp)
{
    public async Task<DnsServerTestResult> TestServerAsync(string server, DnsTestOptions o, CancellationToken ct)
    {
        var ip = IPAddress.Parse(server);
        var icmpSamples = new List<double>();
        for (var i = 0; i < o.PingCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await icmp.PingOnceAsync(ip, o.Timeout, ct) is { } ms) icmpSamples.Add(ms);
            if (i < o.PingCount - 1) await Task.Delay(o.Interval, ct);
        }

        var dnsSamples = new List<double>(); int ok = 0, valid = 0, timeouts = 0, tcp = 0; string? lastErr = null;
        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        for (var i = 0; i < o.DnsQueries; i++)
        {
            ct.ThrowIfCancellationRequested();
            var domain = o.TestDomains[i % o.TestDomains.Length];
            var r = o.TcpFallback ? await client.QueryWithFallbackAsync(ip, domain, v6, o.Timeout, ct) : await client.QueryAsync(ip, domain, v6, o.Timeout, ct);
            if (r.ValidResponse) valid++;
            if (r.TimedOut && !r.ValidResponse) timeouts++;
            if (r.Transport == DnsTransport.Tcp && r.ValidResponse) tcp++;
            if (r.Success) { ok++; dnsSamples.Add(r.ElapsedMs!.Value); } else lastErr = r.Error;
            if (i < o.DnsQueries - 1) await Task.Delay(o.Interval, ct);
        }
        return new DnsServerTestResult(server, LatencyStats.From(o.PingCount, icmpSamples), LatencyStats.From(o.DnsQueries, dnsSamples), ok, o.DnsQueries, valid, timeouts, tcp, lastErr);
    }

    /// <summary>Tests all profiles with bounded concurrency. Each finished profile is reported immediately through <paramref name="progress"/>.</summary>
    public async Task<IReadOnlyList<DnsProfileTestResult>> TestAllAsync(IEnumerable<DnsProfile> profiles, DnsTestOptions o, IProgress<DnsProfileTestResult>? progress, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, o.MaxConcurrency));
        var tasks = profiles.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var servers = new List<DnsServerTestResult>();
                foreach (var s in p.AllServers) servers.Add(await TestServerAsync(s, o, ct));
                var result = new DnsProfileTestResult(p, servers, DnsScorer.Score(servers, o.Strategy), o.Strategy);
                progress?.Report(result);
                return result;
            }
            finally { gate.Release(); }
        }).ToList();
        var all = await Task.WhenAll(tasks);
        return all.OrderBy(r => r.Score).ToList();
    }
}
