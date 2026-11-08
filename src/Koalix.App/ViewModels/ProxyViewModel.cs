using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.SystemIntegration;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

/// <summary>System proxy (WinINet) — for apps that do not need TUN. Snapshot is restored only if unchanged by the user.</summary>
public sealed partial class ProxyViewModel(SystemProxyManager proxy, NetworkStateJournal journal, ConnectionManager cm) : PageViewModel
{
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _port = "";

    public override void OnActivated() => Refresh();
    private void Refresh()
    {
        var s = proxy.Read();
        Current = s.Enabled ? $"{L["Common.Enabled"]}: {s.Server}" : L["Common.Disabled"];
        if (s.AutoConfigUrl is not null) Current += $"\nPAC: {s.AutoConfigUrl}";
        Port = cm.Active?.Engine.HealthProxyPort?.ToString() ?? Port;
    }

    [RelayCommand] private void Enable()
    {
        if (!int.TryParse(Port, out var p) || p is < 1 or > 65535) { StatusMessage = L["Proxy.BadPort"]; return; }
        var before = proxy.Read();
        proxy.EnableLocal(p);
        journal.Record(JournalKind.SystemProxy, "wininet", NetworkStateJournal.Serialize(before), NetworkStateJournal.Serialize(proxy.Read()));
        AppHost.Log(LogCategory.WindowsNetwork, "system proxy enabled on 127.0.0.1:" + p);
        Refresh();
    }

    [RelayCommand] private void Restore()
    {
        var entries = journal.Pending().Where(e => e.Kind == JournalKind.SystemProxy).OrderBy(e => e.At).ToList();
        if (entries.Count == 0) { StatusMessage = L["Proxy.NothingToRestore"]; return; }
        var original = NetworkStateJournal.Deserialize<SystemProxyState>(entries[0].Before)!;
        var expected = NetworkStateJournal.Deserialize<SystemProxyState>(entries[^1].Applied)!;
        if (!proxy.RestoreIfUnchanged(original, expected) && !Dialogs.Confirm(L["Proxy.Restore"], L["Proxy.ChangedByUser"])) return;
        proxy.Apply(original);
        foreach (var e in entries) journal.Complete(e.Id);
        StatusMessage = L["Common.Restored"]; Refresh();
    }
}
