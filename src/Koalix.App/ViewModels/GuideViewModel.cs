using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Localization;
using Koalix.App.Services;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

/// <summary>v0.4.42 — one quick-start checklist line on the آموزش page: done/undone (live) + a one-click action.</summary>
public sealed partial class GuideStep : ObservableObject
{
    public string Key { get; }
    public int Number { get; }
    public string Glyph { get; }
    private readonly Action _run;
    [ObservableProperty] private bool _done;
    public string Title => Loc.I[$"Guide.Check.{Key}"];
    public string Text => Loc.I[$"Guide.Check.{Key}Text"];
    public string ActionText => Loc.I[$"Guide.Check.{Key}Do"];

    public GuideStep(string key, int number, string glyph, Action run) { Key = key; Number = number; Glyph = glyph; _run = run; }

    [RelayCommand] private void Run() => _run();
    public void Relabel() => OnPropertyChanged(string.Empty);
}

/// <summary>v0.4.42 — one lesson / troubleshooting card: a clickable header that unfolds a step-by-step body.</summary>
public sealed partial class GuideTopic : ObservableObject
{
    public string Key { get; }
    public string Glyph { get; }
    /// <summary>Lesson about an Advanced-mode feature — shown with a small «پیشرفته» chip.</summary>
    public bool Advanced { get; }
    private readonly Action? _run;
    [ObservableProperty] private bool _isOpen;
    public string Title => Loc.I[$"Guide.{Key}.Title"];
    public string Body => Loc.I[$"Guide.{Key}.Body"];
    public bool HasAction => _run is not null;
    public string ActionText => _run is null ? "" : Loc.I[$"Guide.{Key}.Do"];

    public GuideTopic(string key, string glyph, bool advanced = false, Action? run = null) { Key = key; Glyph = glyph; Advanced = advanced; _run = run; }

    [RelayCommand] private void Toggle() => IsOpen = !IsOpen;
    [RelayCommand] private void Run() => _run?.Invoke();
    public void Relabel() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// v0.4.42 — the آموزش (Guide) page (user: «بخش اموزش هم میخوام کامل تر بشه»). Everything a newcomer needs in one place:
/// pick Beginner/Advanced, replay the animated tour, a LIVE quick-start checklist (engine → config → ping test → connect)
/// whose unfinished steps each have a one-click button, step-by-step lessons, a troubleshooting list and the shortcuts.
/// </summary>
public sealed partial class GuideViewModel : PageViewModel
{
    private readonly EnginePaths _engines;
    private readonly DashboardViewModel _dash;

    public ObservableCollection<GuideStep> Steps { get; } = [];
    public ObservableCollection<GuideTopic> Lessons { get; } = [];
    public ObservableCollection<GuideTopic> Troubles { get; } = [];
    public UiMode Mode => UiMode.I;

    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _allDone;

    public GuideViewModel(EnginePaths engines, DashboardViewModel dash)
    {
        _engines = engines; _dash = dash;
        Steps.Add(new GuideStep("Engine", 1, "\uE896", () => Go("Nav.Settings")));
        Steps.Add(new GuideStep("Config", 2, "\uE710", () => _dash.OpenAddConfigCommand.Execute(null)));
        Steps.Add(new GuideStep("Test", 3, "\uEC05", () => { Go("Nav.Dashboard"); _ = _dash.PingAllAsync(); }));
        Steps.Add(new GuideStep("Connect", 4, "\uE7E8", () => { Go("Nav.Dashboard"); _ = _dash.ConnectSelectedAsync(); }));

        Lessons.Add(new GuideTopic("AddConfig", "\uE71B", run: () => _dash.OpenAddConfigCommand.Execute(null)));
        Lessons.Add(new GuideTopic("Connect", "\uE7E8", run: () => Go("Nav.Dashboard")));
        Lessons.Add(new GuideTopic("Ping", "\uEC05"));
        Lessons.Add(new GuideTopic("Subs", "\uE753", run: () => Go("Nav.Profiles")));
        Lessons.Add(new GuideTopic("Split", "\uE8AB", run: () => Go("Nav.Split")));
        Lessons.Add(new GuideTopic("Modes", "\uE9E9"));
        Lessons.Add(new GuideTopic("Tray", "\uE7F4"));
        Lessons.Add(new GuideTopic("Map", "\uE909", advanced: true));
        Lessons.Add(new GuideTopic("Gaming", "\uE7FC", advanced: true, run: () => GoAdvanced("Nav.Gaming")));
        Lessons.Add(new GuideTopic("Dns", "\uE968", advanced: true, run: () => GoAdvanced("Nav.Dns")));

        Troubles.Add(new GuideTopic("NoConnect", "\uE7BA", run: () => Go("Nav.Repair")));
        Troubles.Add(new GuideTopic("Slow", "\uE916", run: () => { Go("Nav.Dashboard"); _ = _dash.PingAllAsync(); }));
        Troubles.Add(new GuideTopic("NoInternet", "\uE774", run: () => Go("Nav.Repair")));
        Troubles.Add(new GuideTopic("EngineMissing", "\uE896", run: () => Go("Nav.Settings")));
        Troubles.Add(new GuideTopic("Admin", "\uE7EF"));
        Troubles.Add(new GuideTopic("SubFail", "\uE72C", run: () => Go("Nav.Profiles")));

        Loc.I.LanguageChanged += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var s in Steps) s.Relabel();
            foreach (var t in Lessons) t.Relabel();
            foreach (var t in Troubles) t.Relabel();
            Refresh();
        });
        _dash.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DashboardViewModel.State) or nameof(DashboardViewModel.SelectedProfile))
                Application.Current?.Dispatcher.BeginInvoke(Refresh);
        };
    }

    public override void OnActivated() => Refresh();

    private void Refresh()
    {
        try
        {
            Steps[0].Done = File.Exists(_engines.SingBoxExe) || File.Exists(_engines.XrayExe)
                            || File.Exists(EnginePaths.WireGuardExe) || EnginePaths.WireSockExe is not null;
            Steps[1].Done = _dash.Profiles.Any(p => !p.IsAuto);
            Steps[2].Done = _dash.Profiles.Any(p => !p.IsAuto && !string.IsNullOrWhiteSpace(p.Ping));
            Steps[3].Done = _dash.IsConnected;
        }
        catch { /* informational only */ }
        DoneCount = Steps.Count(s => s.Done);
        Progress = Steps.Count == 0 ? 0 : (double)DoneCount / Steps.Count;
        AllDone = DoneCount == Steps.Count;
        ProgressText = Loc.I.Format("Guide.ProgressFmt", DoneCount, Steps.Count);
    }

    private static void Go(string key)
    {
        var main = AppHost.Get<MainViewModel>();
        main.Selected = main.Nav.First(n => n.Key == key);
    }

    /// <summary>A lesson about an expert page: switch to Advanced first so the page is actually in the sidebar.</summary>
    private static void GoAdvanced(string key)
    {
        if (UiMode.I.IsSimple) UiMode.I.IsSimple = false;
        Go(key);
    }

    [RelayCommand] private void ReplayTour()
    {
        if (Application.Current.MainWindow is MainWindow mw) mw.ShowOnboarding();
    }

    [RelayCommand] private void ChooseSimple() => UiMode.I.IsSimple = true;
    [RelayCommand] private void ChooseAdvanced() => UiMode.I.IsSimple = false;
    [RelayCommand] private void OpenRepair() => Go("Nav.Repair");
}
