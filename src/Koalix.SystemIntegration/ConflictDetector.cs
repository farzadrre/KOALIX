using System.Diagnostics;
using System.Net.NetworkInformation;
using Koalix.Core;

namespace Koalix.SystemIntegration;

/// <summary>Pre-connect checks: orphaned engines, stale/foreign TUN adapters, other VPN clients, port conflicts, multiple default routes, system proxy.</summary>
public sealed class ConflictDetector(Func<SystemProxyState?>? readProxy = null) : INetworkConflictDetector
{
    private static readonly string[] EngineNames = ["sing-box", "xray"];
    /// <summary>Security-suite processes that filter traffic at the driver level. They commonly prompt (or silently
    /// decide) per executable, and KOALIX's engines live under AppData — a wrong verdict shows up as instant RSTs
    /// ("forcibly closed by the remote host") exactly like censorship. Zone Alarm was confirmed on a field system.</summary>
    private static readonly string[] Firewalls = ["vsmon", "zlclient", "zatray", "zatutor", // ZoneAlarm
        "ekrn", // ESET
        "AvastSvc", "AvastUI", "avgsvc", "avgsvcx", // Avast / AVG
        "vsserv", "bdservicehost", // Bitdefender
        "mcshield", "MFEMMS", // McAfee
        "kxetray", "avp"]; // Kingsoft / Kaspersky
    private static readonly string[] ForeignVpn = ["openvpn", "openvpn-gui", "openvpn-connect", "v2rayN", "nekoray", "clash", "clash-verge", "hiddify", "outline", "proxifier", "warp-svc", "netbird",
        // Lantern (field log, v0.3.6): its service stays resident, sets a system proxy and intercepts traffic/DNS
        // even when its window is closed — a running Lantern next to a TUN tunnel makes browsers behave as if
        // nothing (or only half of the web) is connected. It was missing from this list.
        "LanternSvc", "lantern",
        "Windscribe", "WindscribeService", "ProtonVPN", "ProtonVPNService", "nordvpn", "nordvpn-service", "expressvpn", "expressvpnd", "Surfshark", "Surfshark.Service",
        "psiphon3", "psiphon-tunnel-core", "openconnect", "FortiClient", "vpnui", "tailscaled", "zerotier-one_x64", "hotspotshield", "Mullvad VPN", "mullvad-daemon",
        // v0.4.18 — seen on the field system: VeePN (WireSock-based) and its helpers stay resident and grab routes.
        "veevpn", "VeePNService", "wiresock"];
    /// <summary>Interface names/descriptions that indicate another VPN owns (or may grab) the default route.</summary>
    private static readonly string[] ForeignVpnAdapters = ["Windscribe", "IKEv2", "ProtonVPN", "NordLynx", "Mullvad", "Surfshark", "ExpressVPN", "Cloudflare WARP", "OpenVPN", "Tailscale", "ZeroTier", "Psiphon"];

    /// <summary>Names of up interfaces that belong to other VPN products (used to warn while connected).
    /// v0.4.18 — <paramref name="excludeOwnOpenVpn"/>: while KOALIX ITSELF runs an OpenVPN tunnel, the adapter the
    /// engine just brought up (“OpenVPN Data Channel Offload” — the same name a foreign OpenVPN client would use)
    /// must not be reported as foreign; the field log showed KOALIX scaring the user about its own tunnel on every
    /// connect.</summary>
    public static IReadOnlyList<string> ForeignVpnInterfaces(bool excludeOwnOpenVpn = false) =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ppp || ForeignVpnAdapters.Any(a => (n.Description + " " + n.Name).Contains(a, StringComparison.OrdinalIgnoreCase)))
            .Where(n => !excludeOwnOpenVpn || !(n.Description + " " + n.Name).Contains("OpenVPN", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Name).ToList();

    /// <summary>
    /// v0.4.18 — the user-requested “close every filter-breaker KOALIX detects” feature: the running processes
    /// behind the ForeignVpn name list. KOALIX's own engines (sing-box/xray/openvpn.exe under KOALIX's folders)
    /// are never reported: a process whose path resolves under the app's own directory is skipped, and an
    /// unreadable path (protected service) is only reported when it cannot be one of ours by name context.
    /// Returns (processName, pid) pairs, sorted for a stable log/toast.
    /// </summary>
    public static IReadOnlyList<(string Name, int Pid)> ForeignVpnProcesses()
    {
        var ownDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        var found = new List<(string, int)>();
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                if (!ForeignVpn.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                var mine = false;
                try { mine = p.MainModule?.FileName is { } f && ownDir.Length > 0 && f.StartsWith(ownDir, StringComparison.OrdinalIgnoreCase); }
                catch { /* access denied — foreign service, keep it */ }
                if (!mine) found.Add((p.ProcessName, p.Id));
            }
        return found.OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<IReadOnlyList<NetworkConflict>> DetectAsync(IReadOnlyCollection<int> requiredLocalPorts, CancellationToken ct)
    {
        var list = new List<NetworkConflict>();
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                if (EngineNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    list.Add(new("Conflict.OrphanEngine", $"{p.ProcessName} (PID {p.Id})", true));
                else if (ForeignVpn.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    list.Add(new("Conflict.ForeignVpn", p.ProcessName, false));
                else if (Firewalls.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    list.Add(new("Conflict.Firewall", p.ProcessName, false));
            }

        var nics = NetworkInterface.GetAllNetworkInterfaces();
        foreach (var n in nics.Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var d = n.Description + " " + n.Name;
            if (n.Name.Equals("koalix-tun", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("nexus-tun", StringComparison.OrdinalIgnoreCase))
                list.Add(new("Conflict.StaleKoalixTun", n.Name, true));
            else if (n.NetworkInterfaceType == NetworkInterfaceType.Ppp || ForeignVpnAdapters.Any(a => d.Contains(a, StringComparison.OrdinalIgnoreCase)))
                list.Add(new("Conflict.ForeignVpnAdapter", n.Name, false));
            else if (d.Contains("Wintun", StringComparison.OrdinalIgnoreCase) || d.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) || d.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
                list.Add(new("Conflict.ForeignTun", n.Name, false));
        }

        var defaults = nics.Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Count(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)));
        if (defaults > 1) list.Add(new("Conflict.MultipleDefaultRoutes", defaults.ToString(), false));

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        foreach (var port in requiredLocalPorts.Where(listeners.Contains))
            list.Add(new("Conflict.PortInUse", port.ToString(), true));

        if (readProxy?.Invoke() is { Enabled: true } px)
            list.Add(new("Conflict.SystemProxyActive", px.Server ?? "", false));

        return Task.FromResult<IReadOnlyList<NetworkConflict>>(list);
    }
}
