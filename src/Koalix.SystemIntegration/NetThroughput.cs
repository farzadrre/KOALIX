using System.Net.NetworkInformation;

namespace Koalix.SystemIntegration;

/// <summary>
/// v0.4.38 — whole-PC download/upload speed for the floating widget, connected or not. Only PHYSICAL adapters are
/// summed (Ethernet / Wi-Fi): a tunnel adapter carries the same bytes again, so counting both would double the
/// number while KOALIX is connected.
/// </summary>
public sealed class NetThroughput
{
    private long _lastRx, _lastTx;
    private DateTime _lastAt;

    public (long DownBps, long UpBps) Sample()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPhysical(n)) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
        }
        catch { return (0, 0); }
        var now = DateTime.UtcNow;
        var dt = (now - _lastAt).TotalSeconds;
        (long, long) r = (0, 0);
        if (_lastAt != default && dt > 0.2 && rx >= _lastRx && tx >= _lastTx)
            r = ((long)((rx - _lastRx) / dt), (long)((tx - _lastTx) / dt));
        (_lastRx, _lastTx, _lastAt) = (rx, tx, now);
        return r;
    }

    public static bool IsPhysical(NetworkInterface n)
    {
        if (n.OperationalStatus != OperationalStatus.Up) return false;
        if (n.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ppp)) return false;
        return !IsVirtualName(n.Name + " " + n.Description);
    }

    private static readonly string[] Virtual =
        ["wintun", "wireguard", "tap-", "tap ", "tun", "openvpn", "sing-box", "koalix", "virtual", "vmware", "hyper-v", "vethernet", "loopback", "npcap", "pseudo", "wan miniport", "bluetooth"];

    public static bool IsVirtualName(string text)
    {
        var t = text.ToLowerInvariant();
        return Virtual.Any(t.Contains);
    }
}
