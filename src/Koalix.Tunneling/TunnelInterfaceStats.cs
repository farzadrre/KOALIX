using System.Net;
using System.Net.NetworkInformation;

namespace Koalix.Tunneling;

/// <summary>
/// Tunnel-adapter byte counters (v0.4.10, hardened v0.4.11): the FALLBACK traffic source for tunnels that carry
/// packets outside sing-box (WireSock per-app routing, official WireGuard service). The v0.4.10 matcher required the
/// adapter to be "Up" and knew only four name hints — in the field WireSock's filter-mode sessions expose no such
/// adapter, so the dashboard stayed frozen at zero. Matching is now relaxed (any operational status; the WireGuard
/// config's interface address first, then a wider hint list scanned over name AND description), and the caller
/// (WireGuardTraffic) can exclude a frozen adapter to switch candidates.
/// </summary>
public static class TunnelInterfaceStats
{
    private static readonly string[] NameHints = ["wiresock", "wsclient", "wintun", "wireguard", "koalix", "winpkfilter"];

    /// <summary>Best adapter: exact match on the config's interface address first (any status), then name/description hints.</summary>
    public static NetworkInterface? FindAdapter(string? wireGuardConf, string? excludeId = null)
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return null; }
        var ips = InterfaceAddresses(wireGuardConf);
        foreach (var nic in nics)
        {
            if (nic.Id == excludeId) continue;
            try
            {
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (ips.Count > 0 && ips.Contains(a.Address)) return nic;
            }
            catch (NetworkInformationException) { }
        }
        NetworkInterface? hint = null;
        foreach (var nic in nics)
        {
            if (nic.Id == excludeId) continue;
            var hay = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (NameHints.Any(hay.Contains)) hint ??= nic;
        }
        return hint;
    }

    /// <summary>Unicast addresses of the [Interface] "Address = …" lines — the tunnel adapter carries exactly these.</summary>
    private static HashSet<IPAddress> InterfaceAddresses(string? conf)
    {
        var set = new HashSet<IPAddress>();
        if (string.IsNullOrEmpty(conf)) return set;
        foreach (var raw in conf.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (!t.StartsWith("Address", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            foreach (var part in t[(eq + 1)..].Split(','))
            {
                var ip = part.Trim();
                var slash = ip.IndexOf('/');
                if (slash >= 0) ip = ip[..slash];
                if (IPAddress.TryParse(ip, out var a)) set.Add(a);
            }
        }
        return set;
    }

    /// <summary>Current byte counters of an adapter id; null when the adapter is gone or unreadable.</summary>
    public static (long Down, long Up)? ReadCounters(string id)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == id);
            if (nic is null) return null;
            var st = nic.GetIPStatistics();
            return (st.BytesReceived, st.BytesSent);
        }
        catch { return null; }
    }
}
