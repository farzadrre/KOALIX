using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

public sealed partial class SplitTunnelViewModel : PageViewModel
{
    private readonly AppRuleRepository _rules;
    private readonly SettingsRepository _settings;
    private readonly ConnectionManager _cm;
    public ObservableCollection<AppRule> Rules { get; } = [];
    public ObservableCollection<RunningApp> Running { get; } = [];
    public IReadOnlyList<SplitMode> Modes { get; } = Enum.GetValues<SplitMode>();
    public IReadOnlyList<AppAction> Actions { get; } = Enum.GetValues<AppAction>();
    [ObservableProperty] private SplitMode _mode;
    [ObservableProperty] private AppRule? _selected;
    [ObservableProperty] private RunningApp? _selectedRunning;
    [ObservableProperty] private AppAction _newAction = AppAction.Tunnel;
    [ObservableProperty] private bool _newIncludeFolder;
    [ObservableProperty] private bool _tunnelDns = true;
    [ObservableProperty] private bool _blockIpv6;
    [ObservableProperty] private bool _blockQuic;
    [ObservableProperty] private bool _iranDirect;
    [ObservableProperty] private string _proxyDomainsText = "";
    [ObservableProperty] private string _directDomainsText = "";
    [ObservableProperty] private string _domainsSummary = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _restartWarning = "";

    public SplitTunnelViewModel(AppRuleRepository rules, SettingsRepository settings, ConnectionManager cm)
    {
        _rules = rules; _settings = settings; _cm = cm;
        _mode = Enum.TryParse<SplitMode>(settings.Get("split.mode"), out var m) ? m : SplitMode.FullTunnel;
        _tunnelDns = settings.Get("split.tunnelDns") != "0";
        _blockIpv6 = settings.Get("split.blockIpv6") == "1";
        _blockQuic = settings.Get("route.blockQuic") != "0";
        _iranDirect = settings.Get("route.iranDirect") == "1";
        _proxyDomainsText = settings.Get("route.proxyDomains") ?? "";
        _directDomainsText = settings.Get("route.directDomains") ?? "";
        UpdateDomainsSummary();
    }

    public override void OnActivated() { Reload(); RefreshRunning(); }

    partial void OnModeChanged(SplitMode value) { _settings.Set("split.mode", value.ToString()); UpdatePreview(); }
    partial void OnTunnelDnsChanged(bool value) => _settings.Set("split.tunnelDns", value ? "1" : "0");
    partial void OnBlockIpv6Changed(bool value) => _settings.Set("split.blockIpv6", value ? "1" : "0");
    partial void OnBlockQuicChanged(bool value) => _settings.Set("route.blockQuic", value ? "1" : "0");
    partial void OnIranDirectChanged(bool value) => _settings.Set("route.iranDirect", value ? "1" : "0");
    partial void OnProxyDomainsTextChanged(string value) { _settings.Set("route.proxyDomains", value ?? ""); UpdateDomainsSummary(); }
    partial void OnDirectDomainsTextChanged(string value) { _settings.Set("route.directDomains", value ?? ""); UpdateDomainsSummary(); }

    private void UpdateDomainsSummary()
    {
        var p = SplitPolicy.ParseDomains(ProxyDomainsText).Count;
        var d = SplitPolicy.ParseDomains(DirectDomainsText).Count;
        DomainsSummary = L.Format("Split.Domains.Summary", p, d) + (_cm.Active is not null && (p + d) > 0 ? " · " + L["Quick.AppliedNextConnect"] : "");
    }

    private void Reload() { Rules.Clear(); foreach (var r in _rules.All()) Rules.Add(r); UpdatePreview(); }

    [RelayCommand] private void RefreshRunning()
    {
        Running.Clear();
        foreach (var a in ProcessScanner.GetRunning().Where(a => a.ExecutablePath is not null && !a.ExecutablePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                     .GroupBy(a => a.ExecutablePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(a => a.Name))
            Running.Add(a);
    }

    [RelayCommand] private void Browse()
    {
        var f = Dialogs.OpenFile("Programs (*.exe)|*.exe");
        if (f is not null) AddPath(f);
    }

    /// <summary>Folder rule: every program inside the folder (and sub-folders) follows the rule — ideal for game launchers.</summary>
    [RelayCommand] private void BrowseFolder()
    {
        var f = Dialogs.OpenFolder(L["Split.PickFolder"]);
        if (f is null) return;
        f = f.TrimEnd('\\');
        if (!PathValidator.IsValidFolderPath(f) || !Directory.Exists(f)) { StatusMessage = L["Split.Error.InvalidFolder"]; return; }
        var name = new DirectoryInfo(f).Name;
        _rules.Upsert(new AppRule { DisplayName = name, ExecutablePath = f, Action = NewAction, IncludeFolder = true });
        StatusMessage = null;
        Reload();
        Toasts.Show(L["Split.FolderAdded"], L.Format("Split.FolderAddedText", name), ToastKind.Success);
    }

    [RelayCommand] private void AddRunning() { if (SelectedRunning?.ExecutablePath is { } p) AddPath(p); }

    private void AddPath(string path)
    {
        if (!PathValidator.IsValidExecutablePath(path) || !File.Exists(path)) { StatusMessage = L["Split.Error.InvalidPath"]; return; }
        var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
        _rules.Upsert(new AppRule { DisplayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name, ExecutablePath = path, Action = NewAction, IncludeFolder = NewIncludeFolder });
        StatusMessage = null;
        Reload();
    }

    [RelayCommand] private void SetAction(string? action)
    {
        if (Selected is null || !Enum.TryParse<AppAction>(action, out var a)) return;
        _rules.Upsert(Selected with { Action = a }); Reload();
    }

    [RelayCommand] private void ToggleFolder()
    {
        if (Selected is null || Selected.IsFolderRule) return; // folder rules always cover the whole folder
        _rules.Upsert(Selected with { IncludeFolder = !Selected.IncludeFolder }); Reload();
    }

    [RelayCommand] private void Remove() { if (Selected is not null) { _rules.Delete(Selected.Id); Reload(); } }

    private void UpdatePreview()
    {
        var policy = new SplitPolicy(Mode, Rules.ToList(), TunnelDns, BlockIpv6, IranDirect);
        var (t, d, b, def) = policy.Preview();
        string Names(IEnumerable<AppRule> r) => r.Any() ? string.Join("، ", r.Select(x => x.DisplayName)) : "—";
        PreviewText = $"{L["Split.Preview.Tunnel"]}: {Names(t)}\n{L["Split.Preview.Direct"]}: {Names(d)}\n{L["Split.Preview.Blocked"]}: {Names(b)}\n{L["Split.Preview.Default"]}: {L["Split.Route." + def]}";
        var errs = policy.Validate();
        if (errs.Count > 0) PreviewText += "\n⚠ " + string.Join("\n⚠ ", errs.Select(e => L[e]));
        if (_cm.Active is { } a)
        {
            var stale = ProcessScanner.NeedsRestart(Rules, a.Since.LocalDateTime);
            RestartWarning = stale.Count > 0 ? L.Format("Split.RestartApps", string.Join(", ", stale.Select(s => s.Name).Distinct())) : "";
        }
        else RestartWarning = _cm.StateMachine.State == ConnectionState.Disconnected ? "" : L["Split.ReconnectToApply"];
    }
}
