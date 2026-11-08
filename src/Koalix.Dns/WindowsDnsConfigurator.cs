using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Koalix.Core;

namespace Koalix.Dns;

public sealed record AdapterInfo(string Id, string Name, string Description, NetworkInterfaceType Type, OperationalStatus Status,
    int? IPv4Index, int? IPv6Index, IReadOnlyList<string> DnsV4, IReadOnlyList<string> DnsV6, bool? V4IsDhcpDns, bool? V6IsDhcpDns)
{
    public bool IsUp => Status == OperationalStatus.Up;
    public bool IsVirtualTunnel => Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) || Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
        || Name.StartsWith("koalix", StringComparison.OrdinalIgnoreCase) || Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Snapshot of the DNS configuration of one adapter, captured before any change.</summary>
public sealed record DnsSnapshot(string AdapterId, string AdapterName, int? IPv4Index, int? IPv6Index,
    IReadOnlyList<string> DnsV4, IReadOnlyList<string> DnsV6, bool V4WasDhcp, bool V6WasDhcp, DateTimeOffset TakenAt);

public enum IpFamily { IPv4, IPv6 }

public interface IDnsConfigurator
{
    IReadOnlyList<AdapterInfo> GetAdapters(bool includeDown = false);
    DnsSnapshot Snapshot(AdapterInfo adapter);
    Task<ProcessResult> ApplyAsync(AdapterInfo adapter, IpFamily family, IReadOnlyList<string> servers, CancellationToken ct = default);
    Task<ProcessResult> SetDhcpAsync(AdapterInfo adapter, IpFamily family, CancellationToken ct = default);
    Task<ProcessResult> RestoreAsync(DnsSnapshot snapshot, CancellationToken ct = default);
}

/// <summary>
/// Applies DNS through netsh with validated arguments (IP addresses and interface indexes only — no user text reaches the command line).
/// Elevation is requested per operation via the process runner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDnsConfigurator(IProcessRunner runner) : IDnsConfigurator
{
    public IReadOnlyList<AdapterInfo> GetAdapters(bool includeDown = false)
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!includeDown && ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            int? v4 = null, v6 = null;
            try { v4 = props.GetIPv4Properties()?.Index; } catch (NetworkInformationException) { }
            try { v6 = props.GetIPv6Properties()?.Index; } catch (NetworkInformationException) { }
            var dns = props.DnsAddresses;
            list.Add(new AdapterInfo(ni.Id, ni.Name, ni.Description, ni.NetworkInterfaceType, ni.OperationalStatus, v4, v6,
                dns.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
                dns.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal).Select(a => a.ToString().Split('%')[0]).ToList(),
                IsDhcpDns(ni.Id, false), IsDhcpDns(ni.Id, true)));
        }
        return list;
    }

    /// <summary>Static DNS is stored in the NameServer registry value; empty means DHCP-provided DNS.</summary>
    private static bool? IsDhcpDns(string id, bool v6)
    {
        var path = v6 ? $@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\{id}" : $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{id}";
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        if (key is null) return null;
        return string.IsNullOrWhiteSpace(key.GetValue("NameServer") as string);
    }

    public DnsSnapshot Snapshot(AdapterInfo a) =>
        new(a.Id, a.Name, a.IPv4Index, a.IPv6Index, a.DnsV4, a.DnsV6, a.V4IsDhcpDns ?? true, a.V6IsDhcpDns ?? true, DateTimeOffset.Now);

    public async Task<ProcessResult> ApplyAsync(AdapterInfo adapter, IpFamily family, IReadOnlyList<string> servers, CancellationToken ct = default)
    {
        var index = family == IpFamily.IPv4 ? adapter.IPv4Index : adapter.IPv6Index;
        if (index is null) return new ProcessResult(-1, "", $"Adapter has no {family} interface");
        if (servers.Count == 0) return new ProcessResult(-1, "", "No servers");
        foreach (var s in servers)
            if (family == IpFamily.IPv4 ? !DnsProfile.IsIPv4(s) : !DnsProfile.IsIPv6(s)) return new ProcessResult(-1, "", "Invalid address " + s);

        var fam = family == IpFamily.IPv4 ? "ipv4" : "ipv6";
        // Run all netsh steps through one elevated cmd-free batch: netsh supports "-c" contexts but not chaining, so we use multiple calls.
        var r = await runner.RunAsync("netsh", ["interface", fam, "set", "dnsservers", $"name={index}", "source=static", $"address={servers[0]}", "register=primary", "validate=no"], elevated: true, ct: ct);
        if (!r.Success) return r;
        for (var i = 1; i < servers.Count; i++)
        {
            r = await runner.RunAsync("netsh", ["interface", fam, "add", "dnsservers", $"name={index}", $"address={servers[i]}", $"index={i + 1}", "validate=no"], elevated: true, ct: ct);
            if (!r.Success) return r;
        }
        return r;
    }

    public Task<ProcessResult> SetDhcpAsync(AdapterInfo adapter, IpFamily family, CancellationToken ct = default)
    {
        var index = family == IpFamily.IPv4 ? adapter.IPv4Index : adapter.IPv6Index;
        if (index is null) return Task.FromResult(new ProcessResult(-1, "", $"Adapter has no {family} interface"));
        return runner.RunAsync("netsh", ["interface", family == IpFamily.IPv4 ? "ipv4" : "ipv6", "set", "dnsservers", $"name={index}", "source=dhcp"], elevated: true, ct: ct);
    }

    public async Task<ProcessResult> RestoreAsync(DnsSnapshot s, CancellationToken ct = default)
    {
        var adapter = GetAdapters(true).FirstOrDefault(a => a.Id == s.AdapterId);
        if (adapter is null) return new ProcessResult(-1, "", "Adapter no longer exists");
        var r1 = s.V4WasDhcp || s.DnsV4.Count == 0 ? await SetDhcpAsync(adapter, IpFamily.IPv4, ct) : await ApplyAsync(adapter, IpFamily.IPv4, s.DnsV4, ct);
        if (adapter.IPv6Index is null) return r1;
        var r2 = s.V6WasDhcp || s.DnsV6.Count == 0 ? await SetDhcpAsync(adapter, IpFamily.IPv6, ct) : await ApplyAsync(adapter, IpFamily.IPv6, s.DnsV6, ct);
        return r1.Success ? r2 : r1;
    }
}
