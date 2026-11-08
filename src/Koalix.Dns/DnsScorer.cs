namespace Koalix.Dns;

public enum RankingStrategy
{
    LowestMedianLatency,
    LowestJitter,
    LowestPacketLoss,
    HighestDnsSuccess,
    /// <summary>Weighted ESTIMATE combining DNS median, DNS jitter, DNS failure and ICMP loss.</summary>
    BalancedGaming,
}

/// <summary>
/// Lower score is better. Scores for <see cref="RankingStrategy.BalancedGaming"/> are an estimate derived from the measured
/// values, which are always displayed alongside it. A server that blocks ICMP but resolves correctly is NOT penalised.
/// </summary>
public static class DnsScorer
{
    public const double Unusable = 1_000_000;

    public static double Score(IReadOnlyList<DnsServerTestResult> servers, RankingStrategy strategy)
    {
        if (servers.Count == 0) return Unusable;
        var scores = servers.Select(s => ScoreServer(s, strategy)).ToList();
        var best = scores.Min();
        if (best >= Unusable) return Unusable;
        // Redundancy: profiles with a working secondary are preferred slightly.
        var penalty = scores.Count(x => x < Unusable) >= 2 ? 0 : 5;
        return Math.Round(best + penalty, 2);
    }

    public static double ScoreServer(DnsServerTestResult s, RankingStrategy strategy)
    {
        if (!s.DnsWorking) return Unusable;
        var median = s.DnsQuery.MedianMs ?? 2000;
        var jitter = s.DnsQuery.JitterMs ?? 50; // unknown jitter is treated conservatively
        var dnsFail = 100 - s.DnsSuccessRate;
        var icmpLoss = s.IcmpReachable ? s.Icmp.LossPercent : 0;
        return strategy switch
        {
            RankingStrategy.LowestMedianLatency => median + dnsFail * 0.01,
            RankingStrategy.LowestJitter => jitter + median * 0.001,
            RankingStrategy.LowestPacketLoss => Math.Max(icmpLoss, 100 - s.DnsValidResponseRate) * 100 + median * 0.01,
            RankingStrategy.HighestDnsSuccess => dnsFail * 100 + median * 0.01,
            _ => median + jitter * 1.5 + dnsFail * 20 + icmpLoss * 5,
        };
    }
}
