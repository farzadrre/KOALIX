using System.Net;
using System.Net.Sockets;
using Koalix.Core;

namespace Koalix.SystemIntegration;

public enum SelfCheckStatus { Ok, Warn, Fail }

/// <summary>One line of the in-app system self-check. Code is a localization key; Detail is raw data (IPs, names).</summary>
public sealed record SelfCheckItem(string Code, SelfCheckStatus Status, string Detail);

/// <summary>
/// In-app self-diagnosis (v0.4.0) — the checks that previously required reading a 40k-line log file by hand:
///  1. ISP DNS poisoning: public domains must never resolve to the filtering sinkhole (10.10.34.0/24 in Iran)
///     or to any private address on the plain system resolver. (While KOALIX is connected with Tunnel DNS on,
///     hijacked queries are answered inside the tunnel, so this check then passes — that difference is itself
///     the diagnosis.)
///  2. Everything the conflict detector knows: foreign VPN/proxy processes (Lantern, WARP, Windscribe…),
///     orphan tunnel cores, stale TUN adapters, multiple default routes, a leftover system proxy.
///  3. Plain direct reachability (TCP 443 to 1.1.1.1) so "no internet at all" is separated from tunnel issues.
/// </summary>
public sealed class SystemSelfCheck(INetworkConflictDetector conflicts)
{
    private static readonly string[] ProbeHosts = ["www.youtube.com", "www.instagram.com"];

    public static bool IsSinkhole(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [10, 10, 34, _];

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    public async Task<IReadOnlyList<SelfCheckItem>> RunAsync(CancellationToken ct)
    {
        var items = new List<SelfCheckItem>();

        // 1) DNS poisoning via the system resolver.
        SelfCheckItem? dnsItem = null;
        foreach (var host in ProbeHosts)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                var sink = addrs.FirstOrDefault(IsSinkhole);
                if (sink is not null) { dnsItem = new("SelfCheck.DnsPoison", SelfCheckStatus.Fail, $"{host} → {sink}"); break; }
                var priv = addrs.FirstOrDefault(IsPrivate);
                if (priv is not null) dnsItem ??= new("SelfCheck.DnsPrivate", SelfCheckStatus.Warn, $"{host} → {priv}");
                else dnsItem ??= new("SelfCheck.DnsClean", SelfCheckStatus.Ok, $"{host} → {addrs.FirstOrDefault()}");
            }
            catch (SocketException) { dnsItem ??= new("SelfCheck.DnsClean", SelfCheckStatus.Warn, host + " (no answer)"); }
        }
        if (dnsItem is not null) items.Add(dnsItem);

        // 2) Conflicts (foreign VPNs, orphans, adapters, routes, system proxy…).
        var found = await conflicts.DetectAsync([], ct);
        if (found.Count == 0) items.Add(new("SelfCheck.NoConflicts", SelfCheckStatus.Ok, ""));
        else foreach (var c in found)
            items.Add(new(c.Code, c.Blocking ? SelfCheckStatus.Fail : SelfCheckStatus.Warn, c.Detail));

        // 3) Direct internet reachability.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, cts.Token);
            items.Add(new("SelfCheck.InternetOk", SelfCheckStatus.Ok, "1.1.1.1:443"));
        }
        catch { items.Add(new("SelfCheck.NoInternet", SelfCheckStatus.Fail, "1.1.1.1:443")); }

        return items;
    }
}
