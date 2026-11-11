using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Localization;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SingBox;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

/// <summary>One row of the command palette (v0.4.3): a page, a profile (connect on entry) or a command.</summary>
public sealed record PaletteItem(string Title, string Sub, string Glyph, Action Run);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConnectionManager _cm;
    private readonly DashboardViewModel _dash;
    /// <summary>v0.4.23 — the focus overlay binds the live gaming/ping/traffic numbers through this.</summary>
    public DashboardViewModel Dash => _dash;
    public ObservableCollection<NavItem> Nav { get; }
    [ObservableProperty] private NavItem _selected;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _stateDetail = "";
    [ObservableProperty] private ConnectionState _state;
    /// <summary>The title-bar language button shows the OTHER language (the switch target) — a real observable so it
    /// can never go stale like the plain markup-extension text did when the language was changed from Settings (v0.4.15).</summary>
    [ObservableProperty] private string _langSwitchLabel = "EN";
    // Sidebar status-card chips (v0.4.3): engine version + config lint, visible BEFORE connecting (the BUG-11 lesson).
    // v0.4.19 (user report: «پایین سمت راست که موتور هارو مینویسه فقط سینگ باکس و نوشته بقیه و چرا نمینویسه»):
    // one chip per INSTALLED engine (sing-box, Xray, WireGuard/WireSock, OpenVPN) instead of sing-box alone.
    [ObservableProperty] private string _engineChipText = "";
    public ObservableCollection<string> EngineChips { get; } = [];
    [ObservableProperty] private string _lintChipText = "";
    /// <summary>0 = healthy, 1 = problems found, 2 = not applicable (non sing-box profile).</summary>
    [ObservableProperty] private int _lintLevel;
    // Command palette (v0.4.3, Ctrl+K): one search box over pages, profiles and commands.
    [ObservableProperty] private string _paletteQuery = "";
    public ObservableCollection<PaletteItem> PaletteResults { get; } = [];
    private List<PaletteItem> _paletteAll = [];
    public Loc L => Loc.I;

    public MainViewModel(DashboardViewModel d, DnsViewModel dns, ProfilesViewModel prof, SplitTunnelViewModel split, InspectorViewModel insp,
        PingViewModel ping, RepairViewModel rep, GamingViewModel game, ProxyViewModel proxy, LogsViewModel logs, SettingsViewModel set, AboutViewModel about,
        GuideViewModel guide, ConnectionManager cm)
    {
        _cm = cm; _dash = d;
        Nav =
        [
            new("Nav.Dashboard", "\uE80F", d), new("Nav.Dns", "\uE968", dns), new("Nav.Profiles", "\uE705", prof),
            new("Nav.Split", "\uE8AB", split), new("Nav.Inspector", "\uE9D9", insp), new("Nav.Ping", "\uEC05", ping),
            new("Nav.Gaming", "\uE7FC", game), new("Nav.Repair", "\uE90F", rep), new("Nav.Proxy", "\uE774", proxy),
            new("Nav.Logs", "\uE9F9", logs), new("Nav.Guide", "\uE897", guide), new("Nav.Settings", "\uE713", set),
            new("Nav.About", "\uE946", about),
        ];
        _selected = Nav[0];
        // v0.4.42 - Beginner / Advanced: the sidebar (the ListBox binds the default view) hides the expert pages in Beginner.
        try { UiMode.I.Load(AppHost.Get<SettingsRepository>()); } catch { }
        System.Windows.Data.CollectionViewSource.GetDefaultView(Nav).Filter = o => o is NavItem n && UiMode.IsPageVisible(n.Key);
        UiMode.I.Changed += OnUiModeChanged;
        cm.StateMachine.Changed += c => App.Current.Dispatcher.BeginInvoke(() => { State = c.To; UpdateText(); SoundService.Play(c.To); });
        Loc.I.LanguageChanged += UpdateText;
        Loc.I.LanguageChanged += () => App.Current.Dispatcher.BeginInvoke(() => OnUpdateVersionChanged(UpdateVersion)); // v0.4.42 badge text
        Loc.I.LanguageChanged += () => App.Current.Dispatcher.BeginInvoke(() => LangSwitchLabel = Loc.I.Language == "fa" ? "EN" : "فا");
        LangSwitchLabel = Loc.I.Language == "fa" ? "EN" : "فا";
        d.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DashboardViewModel.SelectedProfile) or nameof(DashboardViewModel.State)) App.Current.Dispatcher.BeginInvoke(UpdateChips);
        };
        d.Profiles.CollectionChanged += (_, _) => App.Current.Dispatcher.BeginInvoke(BuildPalette);
        UpdateText();
        UpdateChips();
        BuildPalette();
    }

    private void UpdateText()
    {
        var s = _cm.StateMachine.State;
        StateText = Loc.I["State." + s];
        StateDetail = s switch
        {
            ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Connecting or ConnectionState.Reconnecting
                => _cm.Active?.Profile.Name ?? Loc.I["Main.Working"],
            ConnectionState.Error => Loc.I["Main.SeeDashboard"],
            _ => Loc.I["Main.NotProtected"],
        };
        UpdateChips();
    }

    /// <summary>Engine version + config lint for the sidebar status card (v0.4.3): config problems should never surprise the user mid-connect.</summary>
    private void UpdateChips()
    {
        EngineChips.Clear();
        try
        {
            var em = AppHost.Get<EngineManager>();
            var sb = em.InstalledVersion(EngineManager.SingBoxWindows);
            var xr = em.InstalledVersion(EngineManager.XrayWindows);
            if (sb is not null) EngineChips.Add("sing-box " + VersionShort(sb));
            else EngineChips.Add(Loc.I["Chip.EngineMissing"]);
            if (xr is not null) EngineChips.Add("Xray " + VersionShort(xr));
            // WireGuard support = the official client or WireSock (either can drive WireGuard profiles).
            if (File.Exists(EnginePaths.WireGuardExe) || EnginePaths.WireSockExe is not null) EngineChips.Add("WireGuard");
            if (EnginePaths.OpenVpnExe is not null) EngineChips.Add("OpenVPN");
            EngineChipText = EngineChips.Count > 0 ? string.Join(" · ", EngineChips) : "";
        }
        catch { EngineChipText = ""; }
        var p = _dash.SelectedTunnel;
        if (p is not { Source: ProfileSource.SingBoxJson })
        {
            LintLevel = 2;
            LintChipText = Loc.I["Chip.Lint.Na"];
            return;
        }
        try
        {
            var cfg = JsonNode.Parse(p.Content) as JsonObject;
            IReadOnlyList<string> errors = cfg is null ? ["Config.Parse"] : SingBoxConfigLinter.Lint(cfg);
            LintLevel = errors.Count == 0 ? 0 : 1;
            LintChipText = errors.Count == 0 ? Loc.I["Chip.Lint.Ok"] : Loc.I.Format("Chip.Lint.Bad", errors.Count);
        }
        catch
        {
            LintLevel = 1;
            LintChipText = Loc.I["Chip.Lint.Bad2"];
        }
    }

    /// <summary>VERSION files carry full GitHub tags ("v1.14.4"); the chips only need the number.</summary>
    private static string VersionShort(string tag) => tag.Trim().TrimStart('v', 'V');

    /// <summary>v0.4.42: the mode flipped — leave a page that just got hidden, re-filter the sidebar and the palette.</summary>
    private void OnUiModeChanged()
    {
        if (!UiMode.IsPageVisible(Selected?.Key ?? "")) Selected = Nav[0];
        System.Windows.Data.CollectionViewSource.GetDefaultView(Nav).Refresh();
        BuildPalette();
        Services.Toasts.Show(Loc.I[UiMode.I.IsSimple ? "Mode.SimpleOn" : "Mode.AdvancedOn"],
            Loc.I[UiMode.I.IsSimple ? "Mode.SimpleOnText" : "Mode.AdvancedOnText"], Services.ToastKind.Info);
    }

    [RelayCommand] private void SetAdvancedMode() => UiMode.I.IsSimple = false;

    // v0.4.42 — update notice (user: «وقتی اپدیت میاد برای طرف بنویسه که اپدیت جدید اومده و هر بار وارد اپ میشه بهش بگه
    // و بعد تو بخش اعلان‌ها بره»). Every launch: a toast (which also lands in the notification center) + a title-bar badge.
    [ObservableProperty] private string _updateVersion = "";
    [ObservableProperty] private string _updateBadgeText = "";
    public bool HasUpdate => UpdateVersion.Length > 0;
    partial void OnUpdateVersionChanged(string value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        UpdateBadgeText = value.Length > 0 ? Loc.I.Format("Update.Badge", value) : "";
    }

    /// <summary>Shows the "new version" toast + badge. Safe to call from any thread.</summary>
    public void AnnounceUpdate(string version)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateVersion = version;
            var u = AppHost.Get<UpdateService>();
            Services.Toasts.Show(Loc.I.Format("Update.NewTitle", version),
                Loc.I.Format(u.IsInstalled ? "Update.NewBodyInstalled" : "Update.NewBodyPortable", version, UpdateService.CurrentVersion),
                Services.ToastKind.Info);
        });
    }

    public void ClearUpdate() => App.Current.Dispatcher.BeginInvoke(() => UpdateVersion = "");

    /// <summary>Badge click: the installed build goes to Settings › Updates (download + install there); the portable build
    /// opens the GitHub release page to download the new exe.</summary>
    [RelayCommand]
    private void OpenUpdate()
    {
        var u = AppHost.Get<UpdateService>();
        if (u.IsInstalled)
        {
            if (Nav.FirstOrDefault(n => n.Key == "Nav.Settings") is { } s) Selected = s;
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ReleasePage) { UseShellExecute = true }); }
        catch (Exception ex) { StartupLog.Trace("open release page: " + ex.Message); }
    }

    partial void OnSelectedChanged(NavItem? oldValue, NavItem newValue)
    {
        // v0.4.42: the sidebar ListBox pushes null back when asked to select a page Beginner mode hides — land on the dashboard.
        if (newValue is null)
        {
            App.Current.Dispatcher.BeginInvoke(() => { if (Selected is null) Selected = Nav[0]; });
            return;
        }
        oldValue?.Page.OnDeactivated();
        // v0.4.27 — a page that fails to activate now shows a LOCALIZED toast (the old one put the raw
        // "NullReferenceException" type name in the title — English tech-speak for a Persian UI).
        try { newValue.Page.OnActivated(); }
        catch (Exception ex)
        {
            StartupLog.Trace("page activation: " + ex);
            Services.Toasts.Show(Loc.I["Toast.PageError.Title"], ex.GetType().Name + " — " + Redactor.Redact(ex.Message), Services.ToastKind.Error);
        }
    }

    [RelayCommand] private void ToggleLanguage()
    {
        var lang = Loc.I.Language == "fa" ? "en" : "fa";
        Loc.I.SetLanguage(lang);
        AppHost.Get<SettingsRepository>().Set("ui.language", lang);
    }

    [RelayCommand] private void ToggleTheme()
    {
        var t = AppHost.Get<ThemeService>();
        var mode = t.Mode == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        t.Apply(mode, AppHost.Get<SettingsRepository>().Get("ui.accent"));
        AppHost.Get<SettingsRepository>().Set("ui.theme", mode.ToString());
    }

    /* ─────────── Command palette (v0.4.3) ─────────── */

    /// <summary>Rebuilds the palette entries (pages, profiles, commands); called on language/profile changes.</summary>
    public void BuildPalette()
    {
        List<PaletteItem> all =
        [
            .. Nav.Where(n => UiMode.IsPageVisible(n.Key)).Select(n => new PaletteItem(Loc.I[n.Key], Loc.I["Palette.Pages"], n.Glyph, () => Selected = n)),
            // The virtual auto row is not a connectable profile — it has its own command below (v0.4.6).
            .. _dash.Profiles.Where(r => !r.IsAuto).Select(r => new PaletteItem(r.Name, Loc.I["Palette.Profiles"] + " · " + r.Engine, "\uE705",
                () => { Selected = Nav[0]; _ = _dash.ConnectToAsync(r.Profile!); })),
            new(Loc.I["Cmd.Connect"], Loc.I["Palette.Commands"], "\uE768", () => { Selected = Nav[0]; _ = _dash.ConnectSelectedAsync(); }),
            new(Loc.I["Cmd.AutoConnect"], Loc.I["Palette.Commands"], "\uEC4A", () => { Selected = Nav[0]; _dash.ToggleAutoFromPalette(); }),
            new(Loc.I["Cmd.Disconnect"], Loc.I["Palette.Commands"], "\uE74D", () => _ = _cm.DisconnectAsync(CancellationToken.None)),
            new(Loc.I["Cmd.TestPing"], Loc.I["Palette.Commands"], "\uEC05", () => { Selected = Nav[0]; _ = _dash.PingAllAsync(); }),
            new(Loc.I["Cmd.RefreshIps"], Loc.I["Palette.Commands"], "\uE72C", () => { Selected = Nav[0]; _ = _dash.RefreshEgressAsync(); }),
            new(Loc.I["Cmd.Diagnose"], Loc.I["Palette.Commands"], "\uE9F9", ExportDiagnostics),
            new(Loc.I["Cmd.ToggleTheme"], Loc.I["Palette.Commands"], "\uE793", () => ToggleThemeCommand.Execute(null)),
            new(Loc.I["Cmd.ToggleLang"], Loc.I["Palette.Commands"], "\uE8C1", () => ToggleLanguageCommand.Execute(null)),
            // v0.4.23 — the gaming focus surface is one command away too.
            new(Loc.I["Focus.Enter"], Loc.I["Palette.Commands"], "\uE7FC", () => { if (System.Windows.Application.Current.MainWindow is MainWindow w) w.ShowFocus(); }),
        ];
        _paletteAll = all;
        FilterPalette();
    }

    private void FilterPalette()
    {
        var q = PaletteQuery.Trim();
        PaletteResults.Clear();
        foreach (var i in _paletteAll.Where(i => q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Sub.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(12))
            PaletteResults.Add(i);
    }

    partial void OnPaletteQueryChanged(string value) => FilterPalette();

    /// <summary>One-click diagnostic report (same file as the Logs page) — also reachable from the notification center and the palette (v0.4.3).</summary>
    public void ExportDiagnostics()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var file = DiagnosticReport.Create(AppHost.Get<AppPaths>(), AppHost.Get<RingSink>().Snapshot().Select(l => $"{l.At:O} [{l.Level}] [{l.Category}] {l.Message}"), dir);
            Toasts.Show(Loc.I["Cmd.Diagnose"], Loc.I.Format("Logs.Exported", file), ToastKind.Success);
        }
        catch (Exception ex) { Toasts.Show(Loc.I["Cmd.Diagnose"], Redactor.Redact(ex.Message), ToastKind.Error); }
    }
}
