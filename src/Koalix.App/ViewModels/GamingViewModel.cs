using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

/// <summary>One card of the "system boost" grid (v0.4.3): toggle = apply/revert that single tweak immediately, Pingo-style.</summary>
public sealed partial class BoostItem : ObservableObject
{
    public required GamingTweak Tweak { get; init; }
    public string Glyph { get; init; } = "\uE7FC";
    public string TitleKey { get; init; } = "";
    public string DescKey { get; init; } = "";
    /// <summary>Long tooltip (what the tweak really changes, with honest side effects) — shown on card hover.</summary>
    public string TipKey { get; init; } = "";
    /// <summary>True when the tweak writes HKLM / stops services — the card shows a needs-admin hint while KOALIX is not elevated.</summary>
    public bool NeedsAdmin { get; init; }
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _note;
    /// <summary>0 = idle, 1 = ok/active, 2 = warning, 3 = failed.</summary>
    [ObservableProperty] private int _level;
    internal bool FromUser = true;
    public event Action<BoostItem>? UserToggled;
    partial void OnIsOnChanged(bool value)
    {
        if (FromUser && !Busy) UserToggled?.Invoke(this);
    }
    internal void SetOn(bool on) { FromUser = false; IsOn = on; FromUser = true; }
}

public sealed partial class GamingViewModel : PageViewModel
{
    private readonly GamingOptimizer _opt;
    private readonly SettingsRepository _settings;
    private readonly AppRuleRepository _rules;
    private readonly ConnectionManager _cm;
    private readonly LanShareService _lan;
    private readonly GameAutoService _auto;
    private readonly System.Windows.Threading.DispatcherTimer _lanTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<GamingTweak, BoostItem> _byTweak = [];
    private bool _loading;

    public ObservableCollection<GameProfile> Games { get; } = [];
    public ObservableCollection<BoostItem> Boosts { get; } = [];

    /// <summary>The v0.4.3 "system boost" grid — same twelve toggles the console boosters have, but reversible and verified.</summary>
    private static IEnumerable<BoostItem> BuildBoosts()
    {
        yield return new() { Tweak = GamingTweak.NagleOff, Glyph = "\uEC05", TitleKey = "Gaming.Boost.Nagle.Title", DescKey = "Gaming.Boost.Nagle.Desc", TipKey = "Gaming.Boost.Nagle.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.KeyboardRepeat, Glyph = "\uE765", TitleKey = "Gaming.Boost.Keyboard.Title", DescKey = "Gaming.Boost.Keyboard.Desc", TipKey = "Gaming.Boost.Keyboard.Tip" };
        yield return new() { Tweak = GamingTweak.PerfCountersOff, Glyph = "\uE9D9", TitleKey = "Gaming.Boost.PerfCounters.Title", DescKey = "Gaming.Boost.PerfCounters.Desc", TipKey = "Gaming.Boost.PerfCounters.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.GameScheduling, Glyph = "\uE768", TitleKey = "Gaming.Boost.Scheduling.Title", DescKey = "Gaming.Boost.Scheduling.Desc", TipKey = "Gaming.Boost.Scheduling.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.ForegroundBoost, Glyph = "\uE769", TitleKey = "Gaming.Boost.Foreground.Title", DescKey = "Gaming.Boost.Foreground.Desc", TipKey = "Gaming.Boost.Foreground.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.MultimediaNetworkThrottling, Glyph = "\uE72E", TitleKey = "Gaming.Boost.Throttling.Title", DescKey = "Gaming.Boost.Throttling.Desc", TipKey = "Gaming.Boost.Throttling.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.GameDvrOff, Glyph = "\uE722", TitleKey = "Gaming.Boost.GameDvr.Title", DescKey = "Gaming.Boost.GameDvr.Desc", TipKey = "Gaming.Boost.GameDvr.Tip" };
        yield return new() { Tweak = GamingTweak.CoreParkingOff, Glyph = "\uE945", TitleKey = "Gaming.Boost.Cores.Title", DescKey = "Gaming.Boost.Cores.Desc", TipKey = "Gaming.Boost.Cores.Tip" };
        yield return new() { Tweak = GamingTweak.NtfsLastAccessOff, Glyph = "\uE916", TitleKey = "Gaming.Boost.Ntfs.Title", DescKey = "Gaming.Boost.Ntfs.Desc", TipKey = "Gaming.Boost.Ntfs.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.UltimatePower, Glyph = "\uE703", TitleKey = "Gaming.Boost.Ultimate.Title", DescKey = "Gaming.Boost.Ultimate.Desc", TipKey = "Gaming.Boost.Ultimate.Tip" };
        yield return new() { Tweak = GamingTweak.SearchIndexingOff, Glyph = "\uE721", TitleKey = "Gaming.Boost.Indexing.Title", DescKey = "Gaming.Boost.Indexing.Desc", TipKey = "Gaming.Boost.Indexing.Tip", NeedsAdmin = true };
        yield return new() { Tweak = GamingTweak.SysMainOff, Glyph = "\uE72C", TitleKey = "Gaming.Boost.SysMain.Title", DescKey = "Gaming.Boost.SysMain.Desc", TipKey = "Gaming.Boost.SysMain.Tip", NeedsAdmin = true };
    }

    [ObservableProperty] private GameProfile? _selected;
    /// <summary>True while KOALIX runs elevated — boost cards that need admin show a hint instead of failing silently.</summary>
    [ObservableProperty] private bool _elevated;

    // Console sharing (v0.4.3): the PC tunnels, the console rides the Windows hotspot.
    [ObservableProperty] private string _pcName = "";
    [ObservableProperty] private string _localIp = "";
    [ObservableProperty] private string _proxyPort = "";
    [ObservableProperty] private string _guideTitle = "";
    [ObservableProperty] private string _guideGlyph = "\uE7FC";
    [ObservableProperty] private bool _guideOpen;
    public ObservableCollection<string> GuideSteps { get; } = [];
    /// <summary>The three generic hotspot steps shown in the console card (rebuilt on language switch).</summary>
    public ObservableCollection<string> ConsoleSteps { get; } = [];

    // v0.4.36 - LAN proxy sharing (console / phone on the same Wi-Fi or this PC's hotspot -> HTTP proxy PC-IP:port).
    [ObservableProperty] private bool _lanEnabled;
    [ObservableProperty] private string _lanPort = LanShareService.DefaultPort.ToString();
    [ObservableProperty] private string _lanPassword = "";
    [ObservableProperty] private string _lanStatus = "";
    /// <summary>0 = off, 1 = sharing, 2 = waiting for the tunnel, 3 = error.</summary>
    [ObservableProperty] private int _lanLevel;
    private bool _lanSyncing;

    public GamingViewModel(GamingOptimizer opt, SettingsRepository settings, AppRuleRepository rules, ConnectionManager cm, LanShareService lan, GameAutoService auto)
    {
        _opt = opt; _settings = settings; _rules = rules; _cm = cm; _lan = lan; _auto = auto;
        _lanSyncing = true;
        LanEnabled = lan.Enabled; LanPort = lan.Port.ToString(); LanPassword = lan.Password;
        _lanSyncing = false;
        lan.Changed += () => Ui(RefreshLanStatus);
        _lanTimer.Tick += (_, _) => RefreshLanStatus();
        foreach (var b in BuildBoosts()) { Boosts.Add(b); b.UserToggled += OnBoostToggled; _byTweak[b.Tweak] = b; }
        foreach (var g in JsonSerializer.Deserialize<List<GameProfile>>(settings.Get("gaming.profiles") ?? "[]") ?? []) Games.Add(g);
        L.LanguageChanged += () => Ui(RebuildConsoleSteps);
    }

    private void RebuildConsoleSteps()
    {
        ConsoleSteps.Clear();
        for (var i = 1; i <= 3; i++)
        {
            var key = $"Gaming.Console.Step{i}";
            var s = L[key];
            if (s != key) ConsoleSteps.Add($"{i}. {s}"); // IRANSansXFaNum renders the Latin digit as Persian.
        }
    }

    private void Save()
    {
        _settings.Set("gaming.profiles", JsonSerializer.Serialize(Games.ToList()));
        _auto.SetProfiles(Games);
    }

    /// <summary>v0.4.37 — the per-game "auto" switch: start the game → KOALIX connects (if down) and boosts it;
    /// close it → KOALIX disconnects again, but only if it was the one that connected.</summary>
    [RelayCommand]
    private void ToggleAuto(GameProfile? g)
    {
        if (g is null) return;
        var i = Games.IndexOf(g);
        if (i < 0) return;
        var on = !g.AutoActivate;
        var updated = g with { AutoActivate = on };
        Games[i] = updated;
        if (Selected == g) Selected = updated;
        Save();
        StatusMessage = L.Format(on ? "Gaming.Auto.Armed" : "Gaming.Auto.Disarmed", g.Name);
    }

    /// <summary>Honest state on open: each card shows what is REALLY in effect right now (inspect), not what the user last clicked.</summary>
    public override async void OnActivated()
    {
        RebuildConsoleSteps();
        RefreshConsoleInfo();
        RefreshLanStatus();
        _lanTimer.Start();
        try { await RefreshStatesAsync(); } catch { }
    }

    private async Task RefreshStatesAsync()
    {
        _loading = true;
        Elevated = ProcessRunner.IsElevated;
        try
        {
            foreach (var s in await _opt.InspectAsync(CancellationToken.None))
                if (_byTweak.TryGetValue(s.Tweak, out var item))
                {
                    var on = s.Outcome == TweakOutcome.AlreadySet;
                    item.SetOn(on);
                    if (on) { item.Note = L["Gaming.Boost.Active"]; item.Level = 1; }
                    else if (s.Note == "needs administrator" && item.NeedsAdmin) { item.Note = L["Gaming.Boost.NeedsAdmin"]; item.Level = 2; }
                    else if (s.Note == "unavailable on this system") { item.Note = L["Gaming.Boost.Unavailable"]; item.Level = 2; }
                    else if (s.Outcome == TweakOutcome.Failed) { item.Note = L["Gaming.Boost.Failed"]; item.Level = 3; }
                    else { item.Note = null; item.Level = 0; }
                }
        }
        finally { _loading = false; }
    }

    /// <summary>Service stop/start is asynchronous in Windows — re-read the real state a moment later so the card settles honestly.</summary>
    private async Task RefreshLaterAsync()
    {
        try
        {
            await Task.Delay(2600);
            Ui(async () => { try { await RefreshStatesAsync(); } catch { } });
        }
        catch { }
    }

    // ======================= boost grid =======================

    private async void OnBoostToggled(BoostItem item)
    {
        if (_loading) return;
        item.Busy = true;
        var want = item.IsOn;
        try
        {
            var r = want ? await _opt.ApplyOneAsync(item.Tweak, CancellationToken.None)
                         : await _opt.RevertOneAsync(item.Tweak, CancellationToken.None);
            switch (r.Outcome)
            {
                case TweakOutcome.Applied:
                    item.Note = r.Note is "stop requested" or "start requested" ? L["Gaming.Boost.Pending"] : (want ? L["Gaming.Boost.Applied"] : L["Gaming.Boost.Reverted"]);
                    item.Level = 1;
                    break;
                case TweakOutcome.AlreadySet:
                    item.Note = want ? L["Gaming.Boost.AlreadySet"] : L["Gaming.Boost.AlreadyOff"];
                    item.Level = 1;
                    break;
                case TweakOutcome.Skipped:
                    item.Note = L[r.Note == "unavailable on this system" ? "Gaming.Boost.Unavailable" : "Gaming.Boost.NeedsAdmin"];
                    item.Level = 2;
                    item.SetOn(!want);
                    break;
                default:
                    item.Note = L["Gaming.Boost.Failed"] + (r.Note is null ? "" : " — " + r.Note);
                    item.Level = 3;
                    item.SetOn(!want);
                    break;
            }
            AppHost.Log(LogCategory.Gaming, $"boost {item.Tweak}: {(want ? "on" : "off")} → {r.Outcome}");
            if (r.Note is "stop requested" or "start requested") _ = RefreshLaterAsync();
        }
        catch (Exception ex)
        {
            item.Note = L["Gaming.Boost.Failed"] + " — " + ex.GetType().Name;
            item.Level = 3;
            item.SetOn(!want);
        }
        finally { item.Busy = false; }
    }

    [RelayCommand] private async Task ApplyAllAsync()
    {
        IsBusy = true;
        try
        {
            // v0.4.35 audit: this used to re-apply only the cards that were ALREADY on (each toggle applies instantly),
            // so the button did nothing visible. It now turns on every card that is off — skipping the admin-only ones
            // while KOALIX is not elevated, and the ones the system reported as unavailable.
            var n = 0;
            foreach (var item in Boosts.Where(b => !b.IsOn && !b.Busy && (Elevated || !b.NeedsAdmin) && b.Note != L["Gaming.Boost.Unavailable"]).ToList())
            {
                item.SetOn(true);
                await RunBoostAsync(item, true);
                if (item.IsOn) n++;
            }
            StatusMessage = L.Format("Gaming.Boost.AllApplied", n);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private async Task RevertAllAsync()
    {
        IsBusy = true;
        try
        {
            await _opt.RevertAsync(CancellationToken.None);
            StatusMessage = L["Gaming.Reverted"];
            OnActivated();
        }
        finally { IsBusy = false; }
    }

    private async Task RunBoostAsync(BoostItem item, bool on)
    {
        item.Busy = true;
        try
        {
            var r = on ? await _opt.ApplyOneAsync(item.Tweak, CancellationToken.None) : await _opt.RevertOneAsync(item.Tweak, CancellationToken.None);
            if (r.Outcome is TweakOutcome.Failed or TweakOutcome.Skipped) item.SetOn(false);
            item.Note = r.Outcome switch
            {
                TweakOutcome.Applied => L["Gaming.Boost.Applied"],
                TweakOutcome.AlreadySet => L["Gaming.Boost.AlreadySet"],
                TweakOutcome.Skipped => L[r.Note == "unavailable on this system" ? "Gaming.Boost.Unavailable" : "Gaming.Boost.NeedsAdmin"],
                _ => L["Gaming.Boost.Failed"],
            };
            item.Level = r.Outcome is TweakOutcome.Failed ? 3 : r.Outcome is TweakOutcome.Skipped ? 2 : 1;
        }
        finally { item.Busy = false; }
    }

    // ======================= games =======================

    [RelayCommand] private void AddGame()
    {
        var f = Dialogs.OpenFile("Game (*.exe)|*.exe");
        if (f is null || !PathValidator.IsValidExecutablePath(f)) return;
        var g = new GameProfile { Id = DateTime.UtcNow.Ticks, Name = Path.GetFileNameWithoutExtension(f), ExecutablePath = f };
        Games.Add(g); Save();
    }

    [RelayCommand] private void RemoveGame() { if (Selected is not null) { Games.Remove(Selected); Save(); } }

    /// <summary>Adds the game (folder-wide, to cover launchers/anti-cheat helpers) to the tunnel rule list.</summary>
    [RelayCommand] private void RouteThroughTunnel()
    {
        if (Selected is null) return;
        _rules.Upsert(new AppRule { DisplayName = Selected.Name, ExecutablePath = Selected.ExecutablePath, Action = AppAction.Tunnel, IncludeFolder = true, Group = "Games" });
        StatusMessage = L["Gaming.Routed"];
    }

    [RelayCommand] private void BoostRunning()
    {
        if (Selected is null) return;
        StatusMessage = L.Format("Gaming.Boosted", GamingOptimizer.BoostProcesses(Selected.ExecutablePath));
    }

    // ======================= console sharing =======================

    private void RefreshConsoleInfo()
    {
        PcName = Environment.MachineName;
        LocalIp = TryGetLocalIp() ?? "-";
        ProxyPort = _cm.Active?.Engine.HealthProxyPort?.ToString() ?? L["Gaming.Console.AfterConnect"];
    }

    /// <summary>The IP of the interface that would carry the tunnel (UDP-connect trick — no packet leaves the machine).</summary>
    private static string? TryGetLocalIp()
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect("8.8.8.8", 53);
            if (udp.Client.LocalEndPoint is System.Net.IPEndPoint ep) return ep.Address.ToString();
        }
        catch { }
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { return null; }
    }

    [RelayCommand] private void OpenHotspot()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = ex.GetType().Name; }
    }

    [RelayCommand] private void CopyIp()
    {
        try { System.Windows.Clipboard.SetText(LocalIp); StatusMessage = L["Gaming.Console.IpCopied"]; }
        catch { }
    }

    private string? _guideId;

    [RelayCommand] private void ShowGuide(string id)
    {
        _guideId = id;
        GuideGlyph = "\uE7FC";
        GuideTitle = L[$"Gaming.Console.{id}.Title"];
        RebuildGuideSteps();
        GuideOpen = true;
    }

    private void RebuildGuideSteps()
    {
        if (_guideId is null) return;
        GuideSteps.Clear();
        for (var i = 1; i <= 5; i++)
        {
            var key = $"Gaming.Console.{_guideId}.S{i}";
            var s = L[key];
            if (s != key) GuideSteps.Add(s); // Loc returns the key itself when missing.
        }
    }

    [RelayCommand] private void CloseGuide() => GuideOpen = false;

    public override void OnDeactivated() => _lanTimer.Stop();

    partial void OnLanEnabledChanged(bool value) { if (!_lanSyncing) _ = SaveLanAsync(); }

    [RelayCommand] private async Task SaveLanAsync()
    {
        if (!int.TryParse(LanPort.Trim(), out var port) || port is <= 1024 or >= 65536)
        {
            LanStatus = L["Gaming.Lan.BadPort"]; LanLevel = 3;
            return;
        }
        await _lan.SaveAsync(LanEnabled, port, LanPassword.Trim());
        RefreshLanStatus();
    }

    [RelayCommand] private void CopyLanAddress()
    {
        try { System.Windows.Clipboard.SetText($"{LocalIp}:{LanPort}"); StatusMessage = L["Gaming.Console.IpCopied"]; }
        catch { }
    }

    private void RefreshLanStatus()
    {
        if (!_lan.Enabled) { LanStatus = L["Gaming.Lan.Off"]; LanLevel = 0; return; }
        if (!_lan.IsRunning)
        {
            LanStatus = _lan.LastError == "port-in-use" ? L["Gaming.Lan.PortInUse"] : L.Format("Gaming.Lan.Error", _lan.LastError ?? "?");
            LanLevel = 3; return;
        }
        if (_cm.Active?.Engine.HealthProxyPort is null) { LanStatus = L["Gaming.Lan.Waiting"]; LanLevel = 2; return; }
        LanStatus = L.Format("Gaming.Lan.On", _lan.DevicesSeen, _lan.ActiveClients);
        LanLevel = 1;
    }
}
