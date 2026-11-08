using System.Net.NetworkInformation;
using Koalix.Core;

namespace Koalix.SystemIntegration;

public enum RepairRisk { Low, Medium, High }

/// <summary>
/// A network repair operation. Text is provided as localization keys (Repair.{Id}.Explain / .Consequence / .Recovery)
/// so the UI can show a Persian or English explanation and ask for confirmation before running it.
/// </summary>
public sealed record RepairAction(string Id, RepairRisk Risk, bool NeedsAdmin, bool NeedsReboot, IReadOnlyList<(string Exe, string[] Args)> Steps)
{
    public string ExplainKey => $"Repair.{Id}.Explain";
    public string ConsequenceKey => $"Repair.{Id}.Consequence";
    public string RecoveryKey => $"Repair.{Id}.Recovery";
}

public sealed record RepairOutcome(string Id, bool Success, string Output, DateTimeOffset At);

public sealed class RepairCenter(IProcessRunner runner)
{
    private static string S(string e) => ProcessRunner.System32(e);

    public static IReadOnlyList<RepairAction> All { get; } =
    [
        new("FlushDns", RepairRisk.Low, false, false, [(S("ipconfig.exe"), ["/flushdns"])]),
        new("ArpClear", RepairRisk.Low, true, false, [(S("netsh.exe"), ["interface", "ip", "delete", "arpcache"])]),
        new("ReleaseRenew", RepairRisk.Medium, false, false, [(S("ipconfig.exe"), ["/release"]), (S("ipconfig.exe"), ["/renew"])]),
        new("WinsockReset", RepairRisk.High, true, true, [(S("netsh.exe"), ["winsock", "reset"])]),
        new("IpReset", RepairRisk.High, true, true, [(S("netsh.exe"), ["int", "ip", "reset"])]),
        new("ProxyReset", RepairRisk.Low, true, false, [(S("netsh.exe"), ["winhttp", "reset", "proxy"])]),
        new("Diagnostics", RepairRisk.Low, false, false, [(S("ipconfig.exe"), ["/all"]), (S("route.exe"), ["print"])]),
    ];

    /// <summary>Runs an action. The caller (UI) MUST have shown the explanation and obtained confirmation (confirmed=true).</summary>
    public async Task<RepairOutcome> RunAsync(RepairAction action, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw new InvalidOperationException("Repair actions require explicit user confirmation.");
        var output = new System.Text.StringBuilder();
        var ok = true;
        foreach (var (exe, args) in action.Steps)
        {
            var r = await runner.RunAsync(exe, args, action.NeedsAdmin, TimeSpan.FromSeconds(90), ct);
            output.AppendLine($"> {Path.GetFileName(exe)} {string.Join(' ', args)} → {r.ExitCode}").AppendLine(r.StdOut).AppendLine(r.StdErr);
            ok &= r.Success;
        }
        return new RepairOutcome(action.Id, ok, Redactor.Redact(output.ToString()), DateTimeOffset.Now);
    }

    /// <summary>Restarts a network adapter by interface name (admin).</summary>
    public async Task<bool> RestartAdapterAsync(string interfaceName, CancellationToken ct)
    {
        if (!NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == interfaceName)) return false;
        var nets = S("netsh.exe");
        var a = await runner.RunAsync(nets, ["interface", "set", "interface", $"name={interfaceName}", "admin=disabled"], true, null, ct);
        await Task.Delay(2000, ct);
        var b = await runner.RunAsync(nets, ["interface", "set", "interface", $"name={interfaceName}", "admin=enabled"], true, null, ct);
        return a.Success && b.Success;
    }

    /// <summary>Pings the default gateways of active adapters.</summary>
    public static async Task<IReadOnlyList<(string Gateway, long? Ms)>> TestGatewaysAsync()
    {
        var res = new List<(string, long?)>();
        foreach (var gw in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up)
                     .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address)
                     .Where(a => !a.Equals(System.Net.IPAddress.Any)).Distinct())
        {
            using var p = new Ping();
            try { var r = await p.SendPingAsync(gw, 1500); res.Add((gw.ToString(), r.Status == IPStatus.Success ? r.RoundtripTime : null)); }
            catch { res.Add((gw.ToString(), null)); }
        }
        return res;
    }
}
