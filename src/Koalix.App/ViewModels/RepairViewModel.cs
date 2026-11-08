using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.SystemIntegration;

namespace Koalix.App.ViewModels;

public sealed record RepairItem(RepairAction Action)
{
    public string Title => Localization.Loc.I[$"Repair.{Action.Id}.Title"];
    public string Explain => Localization.Loc.I[Action.ExplainKey];
    public string Consequence => Localization.Loc.I[Action.ConsequenceKey];
    public string Risk => Localization.Loc.I["Risk." + Action.Risk];
}

public sealed partial class RepairViewModel : PageViewModel
{
    private readonly RepairCenter _center;
    private readonly SystemSelfCheck _selfCheck;
    public ObservableCollection<RepairItem> Items { get; } = [];
    [ObservableProperty] private string _output = "";

    public RepairViewModel(RepairCenter center, SystemSelfCheck selfCheck)
    {
        _center = center;
        _selfCheck = selfCheck;
        Fill();
        Localization.Loc.I.LanguageChanged += Fill;
    }

    /// <summary>One-click system self-diagnosis (v0.4.0): DNS poisoning, foreign-VPN conflicts, direct reachability.</summary>
    [RelayCommand]
    private async Task SelfCheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var items = await _selfCheck.RunAsync(CancellationToken.None);
            var lines = items.Select(i => $"{i.Status switch { SelfCheckStatus.Ok => "✓", SelfCheckStatus.Warn => "⚠", _ => "✗" }} {L[i.Code]}{(i.Detail.Length > 0 ? " — " + i.Detail : "")}");
            Output = $"[{DateTime.Now:HH:mm:ss}] {L["Repair.SelfCheck"]}:\n" + string.Join("\n", lines) + "\n" + Output;
            AppHost.Log(LogCategory.Repair, "self-check: " + string.Join("; ", items.Select(i => $"{i.Code}={i.Status}")));
        }
        catch (Exception ex) { Output = ex.Message + "\n" + Output; }
        finally { IsBusy = false; }
    }
    private void Fill() { Items.Clear(); foreach (var a in RepairCenter.All) Items.Add(new RepairItem(a)); }

    [RelayCommand]
    private async Task RunAsync(RepairItem? item)
    {
        if (item is null || IsBusy) return;
        var a = item.Action;
        var msg = $"{item.Explain}\n\n{L["Repair.Consequence"]}: {item.Consequence}\n{L["Repair.Recovery"]}: {L[a.RecoveryKey]}" +
                  (a.NeedsReboot ? "\n\n" + L["Repair.NeedsReboot"] : "") + (a.NeedsAdmin ? "\n" + L["Repair.NeedsAdmin"] : "");
        if (!Dialogs.Confirm(item.Title, msg)) return;
        IsBusy = true;
        try
        {
            var r = await _center.RunAsync(a, confirmed: true, CancellationToken.None);
            Output = $"[{r.At:HH:mm:ss}] {item.Title}: {(r.Success ? "✓" : "✗")}\n{r.Output}\n" + Output;
            AppHost.Log(LogCategory.Repair, $"{a.Id} success={r.Success}");
        }
        catch (Exception ex) { Output = ex.Message + "\n" + Output; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestGatewayAsync()
    {
        var res = await RepairCenter.TestGatewaysAsync();
        Output = string.Join("\n", res.Select(g => $"{L["Repair.Gateway"]} {g.Gateway}: {(g.Ms is { } ms ? ms + " ms" : L["Ping.Timeout"])}")) + "\n" + Output;
    }
}
