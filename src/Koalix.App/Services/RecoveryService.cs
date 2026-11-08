using Koalix.App.Localization;
using Koalix.Core;
using Koalix.Dns;
using Koalix.SystemIntegration;

namespace Koalix.App.Services;

/// <summary>
/// Crash recovery: on start-up, any journaled system change that was never reverted (DNS, proxy, kill-switch rules, gaming tweaks,
/// orphaned engines) is shown to the user, who can restore everything with one confirmation.
/// </summary>
public static class RecoveryService
{
    public static async Task RunAsync(bool crashed)
    {
        var journal = AppHost.Get<NetworkStateJournal>();
        var pending = journal.Pending();
        var orphans = System.Diagnostics.Process.GetProcesses().Where(p => p.ProcessName is "sing-box" or "xray").ToList();
        if (pending.Count == 0 && orphans.Count == 0) return;

        var summary = string.Join("\n", pending.Select(p => $"• {Loc.I["Journal." + p.Kind]} — {p.Target}").Concat(orphans.Select(o => $"• {o.ProcessName} (PID {o.Id})")));
        if (!Dialogs.Confirm(Loc.I["Recovery.Title"], Loc.I[crashed ? "Recovery.Crashed" : "Recovery.Pending"] + "\n\n" + summary)) return;

        foreach (var o in orphans) try { o.Kill(true); } catch { }
        var dns = AppHost.Get<WindowsDnsConfigurator>();
        var proxy = AppHost.Get<SystemProxyManager>();
        foreach (var e in pending.OrderByDescending(e => e.At))
        {
            try
            {
                switch (e.Kind)
                {
                    case JournalKind.DnsChange when NetworkStateJournal.Deserialize<DnsSnapshot>(e.Before) is { } s:
                        await dns.RestoreAsync(s); break;
                    case JournalKind.SystemProxy when NetworkStateJournal.Deserialize<SystemProxyState>(e.Before) is { } ps:
                        proxy.Apply(ps); break;
                    case JournalKind.KillSwitch:
                        await AppHost.Get<FirewallKillSwitch>().RemoveAllLeftoversAsync([e.Target], CancellationToken.None); break;
                    case JournalKind.GamingTweak:
                        await AppHost.Get<GamingOptimizer>().RevertAsync(CancellationToken.None); continue; // completes its own entries
                }
                journal.Complete(e.Id);
            }
            catch (Exception ex) { AppHost.Log(LogCategory.Application, $"recovery of {e.Kind} failed: {ex.Message}"); }
        }
        AppHost.Log(LogCategory.Application, "Recovery completed");
    }
}
