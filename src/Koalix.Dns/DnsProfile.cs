using System.Net;
using System.Net.Sockets;

namespace Koalix.Dns;

public sealed record DnsProfile
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? PrimaryV4 { get; init; }
    public string? SecondaryV4 { get; init; }
    public string? PrimaryV6 { get; init; }
    public string? SecondaryV6 { get; init; }
    public bool IsFavorite { get; init; }

    public IEnumerable<string> V4Servers => new[] { PrimaryV4, SecondaryV4 }.Where(s => !string.IsNullOrWhiteSpace(s))!;
    public IEnumerable<string> V6Servers => new[] { PrimaryV6, SecondaryV6 }.Where(s => !string.IsNullOrWhiteSpace(s))!;
    public IEnumerable<string> AllServers => V4Servers.Concat(V6Servers);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) errors.Add("Dns.Error.Name");
        if (!V4Servers.Any() && !V6Servers.Any()) errors.Add("Dns.Error.NoServers");
        foreach (var s in V4Servers) if (!IsIPv4(s)) errors.Add("Dns.Error.InvalidV4:" + s);
        foreach (var s in V6Servers) if (!IsIPv6(s)) errors.Add("Dns.Error.InvalidV6:" + s);
        return errors;
    }

    public DnsProfile Duplicate(string suffix = " (copy)") => this with { Id = 0, Name = Name + suffix, IsFavorite = false };

    public static bool IsIPv4(string? s) => s is not null && IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') == 3;
    public static bool IsIPv6(string? s) => s is not null && IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
}
