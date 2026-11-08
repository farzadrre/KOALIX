using System.Text;

namespace Koalix.Dns;

public sealed record ParseIssue(int Line, string Text, string Code);

public sealed record DnsParseResult(IReadOnlyList<DnsProfile> Profiles, IReadOnlyList<ParseIssue> Issues);

/// <summary>
/// Parses the TXT format:
/// <code>
/// Cloudflare
/// 1.1.1.1
/// 1.0.0.1
/// 2606:4700:4700::1111   (optional IPv6)
/// </code>
/// A line that is not an IP address starts a new profile. Blank lines and lines starting with # or // are ignored.
/// Every malformed record is reported with its line number instead of being silently dropped.
/// </summary>
public static class DnsTxtFormat
{
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public static DnsParseResult Parse(string text)
    {
        var profiles = new List<DnsProfile>();
        var issues = new List<ParseIssue>();
        string? name = null; int nameLine = 0;
        var v4 = new List<string>(); var v6 = new List<string>();

        void Flush()
        {
            if (name is null) return;
            if (v4.Count == 0 && v6.Count == 0) issues.Add(new(nameLine, name, "Dns.Parse.NoServers"));
            else
            {
                if (v4.Count > 2) issues.Add(new(nameLine, name, "Dns.Parse.ExtraV4Ignored"));
                if (v6.Count > 2) issues.Add(new(nameLine, name, "Dns.Parse.ExtraV6Ignored"));
                profiles.Add(new DnsProfile
                {
                    Name = name,
                    PrimaryV4 = v4.ElementAtOrDefault(0), SecondaryV4 = v4.ElementAtOrDefault(1),
                    PrimaryV6 = v6.ElementAtOrDefault(0), SecondaryV6 = v6.ElementAtOrDefault(1),
                });
            }
            name = null; v4.Clear(); v6.Clear();
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim().TrimStart('\uFEFF');
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith("//")) continue;
            // Allow "1.1.1.1, 1.0.0.1" on one line.
            var tokens = raw.Split([',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.All(t => DnsProfile.IsIPv4(t) || DnsProfile.IsIPv6(t)))
            {
                if (name is null) { issues.Add(new(i + 1, raw, "Dns.Parse.AddressWithoutName")); continue; }
                foreach (var t in tokens) (DnsProfile.IsIPv4(t) ? v4 : v6).Add(t);
                continue;
            }
            if (LooksLikeAddress(raw))
            {
                issues.Add(new(i + 1, raw, "Dns.Parse.InvalidAddress"));
                continue;
            }
            Flush();
            if (raw.Length > 100) { issues.Add(new(i + 1, raw[..40], "Dns.Parse.NameTooLong")); continue; }
            name = raw; nameLine = i + 1;
        }
        Flush();
        return new DnsParseResult(profiles, issues);
    }

    private static bool LooksLikeAddress(string s) =>
        s.All(c => char.IsAsciiDigit(c) || c is '.' or ':' or ',' or ' ' or ';' || char.IsAsciiHexDigit(c)) && (s.Contains('.') || s.Contains(':'));

    public static string Export(IEnumerable<DnsProfile> profiles)
    {
        var sb = new StringBuilder();
        foreach (var p in profiles)
        {
            sb.AppendLine(p.Name);
            foreach (var s in p.AllServers) sb.AppendLine(s);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
