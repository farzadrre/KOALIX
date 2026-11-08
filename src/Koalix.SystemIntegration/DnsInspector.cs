using System.Text.RegularExpressions;

namespace Koalix.SystemIntegration;

public sealed record DnsLookupRecord(DateTimeOffset At, string Domain, string? QueryType, string? Server, string? Answer, string? Process);

/// <summary>
/// Parses sing-box log lines (log level debug) for DNS exchanges. sing-box log format varies between versions,
/// so unparsable lines are ignored rather than guessed.
/// </summary>
public static partial class DnsInspector
{
    // e.g. "dns: exchange example.com. IN A" / "dns: exchanged example.com A 93.184.216.34 60"
    [GeneratedRegex(@"dns:\s+(?:lookup|exchange)\w*\s+(?:domain\s+)?(?<d>[A-Za-z0-9\-\._]+?)\.?(?:\s+IN)?\s+(?<t>A|AAAA|HTTPS|CNAME|TXT|MX|SRV)?(?:\s+(?<a>[0-9a-fA-F\.:]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex Exchange();
    [GeneratedRegex(@"outbound/(?:dns|direct|\w+)\[(?<s>[\w\-]+)\]")]
    private static partial Regex Server();
    [GeneratedRegex(@"process:\s*(?<p>[^\s\]]+)")]
    private static partial Regex Proc();

    public static DnsLookupRecord? TryParse(string line, DateTimeOffset at)
    {
        var m = Exchange().Match(line);
        if (!m.Success) return null;
        return new DnsLookupRecord(at, m.Groups["d"].Value, m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value : null,
            Server().Match(line) is { Success: true } s ? s.Groups["s"].Value : null,
            m.Groups["a"].Success && m.Groups["a"].Length > 0 ? m.Groups["a"].Value : null,
            Proc().Match(line) is { Success: true } p ? p.Groups["p"].Value : null);
    }
}
