using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Koalix.SingBox;

namespace Koalix.SystemIntegration;

public enum Protocol { Tcp, Udp }

/// <summary>Observed route of a flow.</summary>
public enum ObservedRoute
{
    /// <summary>sing-box reports this flow and its outbound chain is a proxy/WireGuard outbound.</summary>
    Tunnel,
    /// <summary>sing-box reports this flow with outbound "direct" (captured by TUN but sent directly).</summary>
    Direct,
    /// <summary>Rejected by a block rule.</summary>
    Blocked,
    /// <summary>Socket is bound to the physical interface and sing-box does not know about it (bypassed TUN).</summary>
    Bypass,
    /// <summary>Cannot be determined with available data. Never guessed.</summary>
    Unknown,
}

public sealed record SocketEntry(Protocol Protocol, IPEndPoint Local, IPEndPoint? Remote, string? State, int Pid);

public sealed record InspectedConnection(
    int Pid, string? ProcessName, string? ProcessPath, Protocol Protocol, string Local, string? Remote, string? Host,
    string? State, ObservedRoute Route, string? Outbound, string? MatchedRule, long Upload, long Download);

public sealed record AppTrafficSummary(string ProcessPath, string? ConfiguredRoute, ObservedRoute ObservedRoute, int Connections, long Upload, long Download);

/// <summary>
/// Routing Inspector backend. Combines (1) the OS TCP/UDP tables with owning PIDs (GetExtendedTcpTable/UdpTable) and
/// (2) sing-box's own connection list (Clash API, includes processPath, chain and matched rule).
/// A flow is only reported as Tunnel/Direct/Blocked when sing-box itself reports it; otherwise Bypass when the local
/// address is a physical adapter address, or Unknown.
/// </summary>
public static class RoutingInspector
{
    public static IReadOnlyList<InspectedConnection> Inspect(IReadOnlyList<SocketEntry> sockets, ClashConnectionsSnapshot? engine,
        IReadOnlySet<IPAddress> tunAddresses, Func<int, string?> pidToPath)
    {
        var result = new List<InspectedConnection>();
        var engineConns = engine?.Connections ?? [];
        var matched = new HashSet<string>();

        foreach (var c in engineConns)
        {
            var route = ClassifyChain(c);
            result.Add(new InspectedConnection(0, c.Metadata.ProcessPath is { } pp ? Path.GetFileName(pp.Replace('\\', '/')) : null,
                c.Metadata.ProcessPath, string.Equals(c.Metadata.Network, "udp", StringComparison.OrdinalIgnoreCase) ? Protocol.Udp : Protocol.Tcp,
                $"{c.Metadata.SourceIP}:{c.Metadata.SourcePort}", $"{c.Metadata.DestinationIP}:{c.Metadata.DestinationPort}", c.Metadata.Host,
                null, route, c.Outbound, c.Rule is null ? null : $"{c.Rule} {c.RulePayload}".Trim(), c.Upload, c.Download));
            if (c.Metadata.DestinationIP is { } d && c.Metadata.DestinationPort is { } dp) matched.Add($"{d}:{dp}");
        }

        foreach (var s in sockets)
        {
            if (s.Pid <= 4 || s.Remote is null || IPAddress.IsLoopback(s.Remote.Address) || s.Remote.Address.Equals(IPAddress.Any)) continue;
            if (matched.Contains(s.Remote.ToString())) continue; // already reported by engine
            var path = pidToPath(s.Pid);
            var route = tunAddresses.Contains(s.Local.Address) ? ObservedRoute.Unknown // in TUN, but engine didn't list it
                : engine is null ? ObservedRoute.Unknown : ObservedRoute.Bypass;
            result.Add(new InspectedConnection(s.Pid, path is null ? null : Path.GetFileName(path), path, s.Protocol,
                s.Local.ToString(), s.Remote.ToString(), null, s.State, route, null, null, 0, 0));
        }
        return result;
    }

    public static ObservedRoute ClassifyChain(ClashConnection c)
    {
        var o = c.Outbound;
        if (o is "direct") return ObservedRoute.Direct;
        if (o is "block" or "reject") return ObservedRoute.Blocked;
        if (c.Rule?.Contains("reject", StringComparison.OrdinalIgnoreCase) == true) return ObservedRoute.Blocked;
        if (o is "proxy" or "wg" or "wireguard" || o.StartsWith("proxy", StringComparison.Ordinal)) return ObservedRoute.Tunnel;
        return ObservedRoute.Unknown;
    }

    /// <summary>Per-app aggregation. configuredRoute comes from the split policy preview ("tunnel"/"direct"/"block").</summary>
    public static IReadOnlyList<AppTrafficSummary> Summarize(IEnumerable<InspectedConnection> conns, Func<string, string?> configuredRoute) =>
        conns.Where(c => c.ProcessPath is not null).GroupBy(c => c.ProcessPath!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var routes = g.Select(c => c.Route).Distinct().ToList();
                var observed = routes.Count == 1 ? routes[0] : ObservedRoute.Unknown; // mixed → not verifiable as a single route
                return new AppTrafficSummary(g.Key, configuredRoute(g.Key), observed, g.Count(), g.Sum(c => c.Upload), g.Sum(c => c.Download));
            }).OrderByDescending(s => s.Upload + s.Download).ToList();

    public static IReadOnlySet<IPAddress> TunAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Name.Contains("koalix-tun", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("sing-tun", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();

    // ---- Windows socket tables ----
    private const int AF_INET = 2;
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)] private struct TcpRow { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct UdpRow { public uint LocalAddr, LocalPort, Pid; }

    private static int Port(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));
    private static readonly string[] TcpStates = ["", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB"];

    /// <summary>IPv4 TCP (TCP_TABLE_OWNER_PID_ALL=5) and UDP (UDP_TABLE_OWNER_PID=1) sockets with owning PID.</summary>
    public static IReadOnlyList<SocketEntry> GetSockets()
    {
        var list = new List<SocketEntry>();
        if (!OperatingSystem.IsWindows()) return list;
        Read(true, 5, Marshal.SizeOf<TcpRow>(), ptr =>
        {
            var r = Marshal.PtrToStructure<TcpRow>(ptr);
            list.Add(new SocketEntry(Protocol.Tcp, new IPEndPoint(r.LocalAddr, Port(r.LocalPort)), new IPEndPoint(r.RemoteAddr, Port(r.RemotePort)),
                r.State < TcpStates.Length ? TcpStates[r.State] : r.State.ToString(), (int)r.Pid));
        });
        Read(false, 1, Marshal.SizeOf<UdpRow>(), ptr =>
        {
            var r = Marshal.PtrToStructure<UdpRow>(ptr);
            list.Add(new SocketEntry(Protocol.Udp, new IPEndPoint(r.LocalAddr, Port(r.LocalPort)), null, null, (int)r.Pid));
        });
        return list;
    }

    private static void Read(bool tcp, int cls, int rowSize, Action<IntPtr> row)
    {
        int size = 0;
        _ = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, cls, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, cls, 0);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var rc = tcp ? GetExtendedTcpTable(buf, ref size, false, AF_INET, cls, 0) : GetExtendedUdpTable(buf, ref size, false, AF_INET, cls, 0);
            if (rc != 0) return;
            var count = Marshal.ReadInt32(buf);
            for (int i = 0; i < count; i++) row(buf + 4 + i * rowSize);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>Result of verifying the egress IP for a route. Unknown when it cannot be measured — never assumed.</summary>
public sealed record EgressVerification(string? DirectPublicIp, string? TunnelPublicIp, string Verdict);

public static class EgressVerifier
{
    /// <summary>
    /// Verdicts: "Verified" (tunnel IP differs from direct IP and both measured), "Same" (both equal → tunnel not changing egress),
    /// "Unknown" (any side unmeasurable).
    /// </summary>
    public static EgressVerification Compare(string? direct, string? tunnel) =>
        direct is null || tunnel is null ? new(direct, tunnel, "Unknown")
        : direct == tunnel ? new(direct, tunnel, "Same") : new(direct, tunnel, "Verified");
}
