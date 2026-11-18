using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Koalix.App.Localization;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;

namespace Koalix.App.ViewModels;

/// <summary>One ready-made palette card (v0.4.24): a real observable row — the v0.4.23 tuple list could not be
/// bound (WPF reflection never sees the compile-time names Hex/NameKey on a ValueTuple), so all six cards
/// rendered empty and clicks carried a null parameter. IsCurrent drives the card's check badge + ring.
/// v0.4.26 — the colour workshop: IsCustom marks USER-SAVED palettes (hex title + delete button, persisted).</summary>
public sealed partial class PaletteRowVm : ObservableObject
{
    public string Hex { get; init; } = "";
    public string NameKey { get; init; } = "";
    public bool IsCustom { get; init; }
    /// <summary>v0.4.37 — the animated ARGB card (Hex is the sentinel "argb", not a colour).</summary>
    public bool IsRgb { get; init; }
    public string HexLabel => Hex.Length == 9 ? "#" + Hex[3..].ToUpperInvariant() : Hex.ToUpperInvariant();
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>v0.4.26 — one click-away variant of the current accent (vivid / soft / lighter / darker / warm / cool).
/// The tooltip is the variant's own hex — self-explanatory, no localization needed.</summary>
public sealed record ShadeVariantVm(string Hex);

/// <summary>One selectable entry of a sound-event picker: a preset id, "none", or the chosen custom file.</summary>
public sealed partial class SoundOptionVm : ObservableObject
{
    public string Id { get; init; } = "";
    [ObservableProperty] private string _label = "";
    public bool IsFile { get; init; }
}

/// <summary>v0.4.36 — one sound-event row on the Settings page.</summary>
public sealed partial class SoundRowVm : ObservableObject
{
    public required SoundService.SoundEvent Event { get; init; }
    public string EventName => Event.ToString();
    public string Glyph { get; init; } = "";
    public string BrushKey { get; init; } = "AccentBrush";
    public System.Windows.Media.Brush? IconBrush => System.Windows.Application.Current?.TryFindResource(BrushKey) as System.Windows.Media.Brush;
    [ObservableProperty] private string _title = "";
    public ObservableCollection<SoundOptionVm> Options { get; } = [];
    [ObservableProperty] private SoundOptionVm? _selected;
    private bool _syncing;
    public event Action<SoundRowVm, SoundOptionVm>? ChoiceChanged;

    internal void Load(IEnumerable<SoundOptionVm> options, SoundOptionVm? selected)
    {
        _syncing = true;
        Options.Clear();
        foreach (var o in options) Options.Add(o);
        Selected = selected;
        _syncing = false;
        OnPropertyChanged(nameof(IconBrush));
    }

    partial void OnSelectedChanged(SoundOptionVm? value)
    {
        if (!_syncing && value is not null) ChoiceChanged?.Invoke(this, value);
    }
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsRepository _s;
    private readonly EngineManager _engines;
    private readonly Koalix.Tunneling.EnginePaths _enginePaths;
    private readonly ThemeService _theme;
    public IReadOnlyList<string> Languages { get; } = ["fa", "en"];
    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<Backdrop> Backdrops { get; } = Enum.GetValues<Backdrop>();
    public IReadOnlyList<string> Accents { get; } = ["#FF7C5CFF", "#FF3B82F6", "#FF06B6D4", "#FF10B981", "#FF84CC16", "#FFF59E0B", "#FFF97316", "#FFEF4444", "#FFEC4899", "#FFA855F7",
        // v0.4.12: curated extra palettes for the custom-theme picker (mint, rose, coral, sky, lime, grape, steel, orchid…)
        "#FF2DD4BF", "#FFF472B6", "#FFFB7185", "#FF38BDF8", "#FFA3E635", "#FFC084FC", "#FF94A3B8", "#FFE879F9"];
    private readonly UpdateService _updates;
    [ObservableProperty] private bool _effects;
    [ObservableProperty] private bool _officialWireGuard;
    [ObservableProperty] private bool _tunnelIpv6;
    public IReadOnlyList<string> WireGuardMtus { get; } = ["1200", "1280", "1340", "1380", "1420", "config"];
    [ObservableProperty] private string _wireGuardMtu = "1280";
    // v0.4.28 — TUN MTU for the sing-box front-end (the WireGuard MTU above is a different knob).
    public IReadOnlyList<string> TunMtus { get; } = ["9000", "1500", "1420", "1380", "1280"];
    [ObservableProperty] private string _tunMtu = "9000";
    // v0.4.28 — the audited reliability set: DNS-poisoning shield (default ON), TLS fragmentation (default ON),
    // mux (opt-in — needs a mux-capable server).
    [ObservableProperty] private bool _poisonGuard = true;
    [ObservableProperty] private bool _tlsFragment = true;
    [ObservableProperty] private bool _mux;
    public IReadOnlyList<string> TunStacks { get; } = ["gvisor", "mixed", "system"];
    [ObservableProperty] private string _tunStack = "gvisor";
    // v0.4.15: OpenVPN support status for the Engines card.
    public bool OpenVpnInstalled => Koalix.Tunneling.EnginePaths.OpenVpnExe is not null;
    public const string OpenVpnDownloadUrl = "https://openvpn.net/community-downloads/";
    // v0.4.14: connect-button shape — "rounded" (the 0.4.13 tile) or "circle" (the classic orb).
    public IReadOnlyList<string> ConnectShapes { get; } = ["rounded", "circle", "hex", "shield", "diamond"]; // v0.4.54
    [ObservableProperty] private string _connectShape = "rounded";
    // v0.4.54 — «داخلش انیمیشن و متحرک بودنش هم چن مدل بکن بشه انتخاب کرد»
    public IReadOnlyList<string> ConnectMotions { get; } = ["water", "heartbeat", "tide", "storm", "galaxy", "vortex", "breathe", "pulse", "orbit", "none"]; // v0.4.56 — «انیمیشن بیشتر بیار ۵ تا دیگه»
    [ObservableProperty] private string _connectMotion = "water";
    partial void OnConnectMotionChanged(string value)
    {
        try { _s.Set("ui.connectMotion", value); } catch { }
        Controls.UiPrefs.Set(ConnectShape, value);
    }
    [ObservableProperty] private bool _strictRoute = true;
    [ObservableProperty] private bool _useWireSock;
    public bool WireSockInstalled => Koalix.Tunneling.EnginePaths.WireSockExe is not null;
    public bool OfficialWireGuardInstalled => File.Exists(Koalix.Tunneling.EnginePaths.WireGuardExe);
    [ObservableProperty] private string _customAccent = "";
    [ObservableProperty] private string _updateFeed = "";
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private bool _updateBusy;
    public string AppVersion => UpdateService.CurrentVersion;
    public bool IsInstalledBuild => _updates.IsInstalled;
    [ObservableProperty] private string _language;
    [ObservableProperty] private AppTheme _theme2;
    [ObservableProperty] private Backdrop _backdrop;
    // v0.4.52 — selectable animated backgrounds (user: «بک گراند های متحرک بیشتری و اضافه کنی و قابل انتخاب باشن»).
    // ui.bgKind is the source of truth; ui.liveBg stays in sync for the old Effects toggle.
    [ObservableProperty] private string _liveBgKind = "particles";
    public System.Collections.ObjectModel.ObservableCollection<string> LiveBgKinds { get; } = ["off", "particles", "mesh", "orbits", "meteors", "aurora", "rain", "bubbles", "starfield", "fireflies", "grid", "waves", "nebula", "snow", "kaleido"]; // v0.4.56 — «پس زمینه متحرک ۵ تا دیگه»
    partial void OnLiveBgKindChanged(string value)
    {
        try { _s.Set("ui.bgKind", value); _s.Set("ui.liveBg", value == "off" ? "0" : "1"); } catch { }
        Controls.LiveBackdrop.PushKind(value);
    }
    // v0.4.52 — backup results live INSIDE the backup card (user: «بالای صفخه نیاد بنویسه ذخیره شد بیاد و تو تب
    // بکاپ بنویسه اونو») — the old top StatusMessage banner for backup is gone.
    [ObservableProperty] private string _backupStatus = "";
    public bool HasBackupStatus => BackupStatus.Length > 0;
    partial void OnBackupStatusChanged(string value) => OnPropertyChanged(nameof(HasBackupStatus));
    [ObservableProperty] private string _accent;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private bool _killSwitch;
    /// <summary>v0.4.18 — close every foreign VPN/filter-breaker client KOALIX detects, right before connecting.</summary>
    [ObservableProperty] private bool _killForeignVpn;
    // v0.4.11 toggles: global hotkey, boost revert on exit, subscription auto-refresh.
    [ObservableProperty] private bool _globalHotkey;
    [ObservableProperty] private bool _revertBoostsOnExit;
    [ObservableProperty] private bool _autoRefreshSubs;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _verboseLogs;
    /// <summary>v0.4.23 (suggestion #8): the subtle connect/disconnect/error tones.</summary>
    [ObservableProperty] private bool _sounds;
    /// <summary>v0.4.24 — master sound volume 0–100 (persisted as ui.sounds.volume).</summary>
    [ObservableProperty] private double _soundVolume = 70;
    [ObservableProperty] private string _remoteDns;
    [ObservableProperty] private string _directDns;
    [ObservableProperty] private string _singBoxVersion = "—";
    [ObservableProperty] private string _xrayVersion = "—";
    [ObservableProperty] private double _downloadProgress;

    /* v0.4.38 - floating widget, style presets, glass, reactive ARGB, live dashboard background. */
    private readonly WidgetService _widget;
    private bool _widgetSyncing;
    public IReadOnlyList<string> WidgetModes { get; } = WidgetViewModel.Modes;
    [ObservableProperty] private bool _widgetEnabled;
    [ObservableProperty] private string _widgetMode = "full";
    [ObservableProperty] private double _widgetOpacity = 92;
    [ObservableProperty] private bool _widgetClickThrough;
    [ObservableProperty] private bool _widgetLocked;
    public IReadOnlyList<string> UiStyles { get; } = ThemeService.Styles;
    [ObservableProperty] private string _uiStyle = "default";
    [ObservableProperty] private bool _glass;
    public IReadOnlyList<string> ReactiveModes { get; } = ThemeService.ReactiveModes;
    [ObservableProperty] private string _argbReactive = "off";
    [ObservableProperty] private bool _liveBackground = true;

    private void SyncWidget()
    {
        _widgetSyncing = true;
        WidgetEnabled = _widget.Enabled;
        WidgetMode = _widget.Mode;
        WidgetOpacity = Math.Round(_widget.Opacity * 100);
        WidgetClickThrough = _widget.ClickThrough;
        WidgetLocked = _widget.Locked;
        _widgetSyncing = false;
    }

    partial void OnWidgetEnabledChanged(bool value) { if (!_widgetSyncing) _widget.SetEnabled(value); }
    partial void OnWidgetModeChanged(string value) { if (!_widgetSyncing && value is not null) _widget.SetMode(value); }
    partial void OnWidgetOpacityChanged(double value) { if (!_widgetSyncing) _widget.SetOpacity(value / 100); }
    partial void OnWidgetClickThroughChanged(bool value) { if (!_widgetSyncing) _widget.SetClickThrough(value); }
    partial void OnWidgetLockedChanged(bool value) { if (!_widgetSyncing) _widget.SetLocked(value); }
    [RelayCommand] private void ResetWidgetPosition() => _widget.ResetPosition();

    partial void OnUiStyleChanged(string value)
    {
        if (value is null) return;
        _s.Set(ThemeService.StyleKey, value);
        _theme.SetStyle(value, Glass);
    }

    partial void OnGlassChanged(bool value)
    {
        _s.Set(ThemeService.GlassKey, value ? "1" : "0");
        _theme.SetStyle(UiStyle, value);
    }

    partial void OnArgbReactiveChanged(string value)
    {
        if (value is null) return;
        _s.Set(ThemeService.RgbReactiveKey, value);
        ThemeService.RgbReactive = value;
    }

    partial void OnLiveBackgroundChanged(bool value)
    {
        _s.Set(Controls.LiveBackdrop.SettingKey, value ? "1" : "0");
        Controls.LiveBackdrop.Allowed = value;
    }

    public SettingsViewModel(SettingsRepository s, EngineManager engines, Koalix.Tunneling.EnginePaths enginePaths, ThemeService theme, UpdateService updates, WidgetService widget)
    {
        _s = s; _engines = engines; _enginePaths = enginePaths; _theme = theme; _updates = updates; _widget = widget;
        // v0.4.47.1 — the manual sing-box.exe path feeds EnginePaths.FindSingBoxExe (system installs are auto-detected).
        _singBoxPathText = s.Get("engine.singbox.path") ?? "";
        SyncWidget();
        widget.Changed += () => Ui(SyncWidget);
        _uiStyle = ThemeService.StyleName;
        _glass = theme.Glass;
        _argbReactive = ThemeService.RgbReactive;
        _liveBackground = s.Get(Controls.LiveBackdrop.SettingKey) != "0";
        _effects = s.Get("ui.effects") != "0";
        _updateFeed = s.Get(UpdateService.FeedKey) is { Length: > 0 } feed ? feed : UpdateService.DefaultFeed;
        _autoUpdate = updates.AutoCheck;
        _language = s.Get("ui.language") ?? "fa";
        _theme2 = Enum.TryParse<AppTheme>(s.Get("ui.theme"), out var t) ? t : AppTheme.Dark;
        _backdrop = Enum.TryParse<Backdrop>(s.Get("ui.backdrop"), out var b) ? b : Backdrop.Mica;
        _liveBgKind = s.Get("ui.bgKind") ?? (s.Get("ui.liveBg") == "0" ? "off" : "particles"); // v0.4.52
        _accent = s.Get("ui.accent") ?? Accents[0];
        _autoReconnect = s.Get("conn.autoReconnect") != "0";
        _killSwitch = s.Get("conn.killSwitch") == "1";
        _killForeignVpn = s.Get("vpn.killForeign") == "1";
        _globalHotkey = s.Get("ui.hotkey") != "0";
        _revertBoostsOnExit = s.Get("gaming.revertOnExit") != "0";
        _autoRefreshSubs = s.Get("subs.auto") != "0";
        _minimizeToTray = s.Get("ui.tray") != "0";
        _verboseLogs = s.Get("log.verbose") == "1";
        _sounds = s.Get("ui.sounds") != "0";
        _soundVolume = int.TryParse(s.Get(SoundService.KeyVolume), out var sv) ? Math.Clamp(sv, 0, 100) : 70;
        _remoteDns = s.Get("tunnel.remoteDns") ?? "1.1.1.1";
        _directDns = s.Get("tunnel.directDns") ?? "1.1.1.1";
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        _startWithWindows = run?.GetValue("KOALIX") is not null;
        _customAccent = _accent;
        LoadCustomPalettes();
        RefreshPaletteBadges(_accent);
        // v0.4.57 — the beauty batch: restore auto day/night, the accent hue and OLED once at startup
        try
        {
            _autoTheme = _s.Get(ThemeService.AutoKey) == "1";
            _autoSunrise = double.TryParse(_s.Get(ThemeService.SunriseKey), System.Globalization.CultureInfo.InvariantCulture, out var sr) ? sr : 7;
            _autoSunset = double.TryParse(_s.Get(ThemeService.SunsetKey), System.Globalization.CultureInfo.InvariantCulture, out var ss) ? ss : 19;
            _accentHueOn = _s.Get(ThemeService.HueOnKey) == "1";
            _accentHue = double.TryParse(_s.Get(ThemeService.HueKey), System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 210;
            _oled = _s.Get(ThemeService.OledKey) == "1";
            _theme.SetAutoTheme(_autoTheme, _autoSunrise, _autoSunset);
            if (_accentHueOn) _theme.SetAccentLive(ThemeService.HueHex(_accentHue));
            _theme.SetOled(_oled);
        }
        catch { }
        RefreshShades(_accent);
        _officialWireGuard = s.Get("wireguard.official") == "1";
        _tunnelIpv6 = s.Get("tunnel.ipv6") == "1";
        _tunStack = s.Get("tun.stack") is "mixed" or "system" ? s.Get("tun.stack")! : "gvisor";
        // v0.4.15 default mode: WireSock defaults to ON when it is installed (better MTU/obfuscation behaviour).
        // v0.4.30: strict routing defaults ON — only an explicit off ("0") disables it (see AppHost's engine mapping).
        _strictRoute = s.Get("tun.strict") != "0";
        _connectShape = s.Get("ui.connectShape") is { Length: > 0 } cs && cs is "circle" or "hex" or "shield" or "diamond" ? cs : "rounded";
        _connectMotion = s.Get("ui.connectMotion") is { Length: > 0 } cm && cm is "breathe" or "pulse" or "orbit" or "none" ? cm : "water"; // v0.4.54
        _useWireSock = s.Get("wireguard.wiresock") is { } ws ? ws == "1" : WireSockInstalled;
        _wireGuardMtu = s.Get("wireguard.mtu") is { Length: > 0 } wm && WireGuardMtus.Contains(wm) ? wm : "1280";
        _tunMtu = s.Get("tun.mtu") is { Length: > 0 } tm && TunMtus.Contains(tm) ? tm : "9000";
        _poisonGuard = s.Get("net.poisonGuard") != "0";
        _tlsFragment = s.Get("net.fragment") != "0";
        _mux = s.Get("net.mux") == "1";
        // v0.4.15 — the language/theme sync bug: the title-bar buttons changed language/theme WITHOUT telling this
        // page, so the Settings comboboxes kept showing the old values (and the reverse direction likewise lagged).
        // Both directions now flow through the shared single sources of truth (Loc.I / ThemeService): this page
        // follows external changes here, and the persisted property setters below only act on REAL changes.
        Loc.I.LanguageChanged += () => Ui(() =>
        {
            if (_uiSyncing || Loc.I.Language == Language) return;
            _uiSyncing = true; Language = Loc.I.Language; _uiSyncing = false;
        });
        ThemeService.Changed += () => Ui(() =>
        {
            if (_uiSyncing || Theme2 == theme.Mode) return;
            _uiSyncing = true; Theme2 = theme.Mode; _uiSyncing = false;
        });
        // v0.4.24 — the sound pickers' labels follow the language, the palette cards follow the active accent.
        Loc.I.LanguageChanged += () => Ui(RebuildSoundOptions);
        RebuildSoundOptions();
    }

    /// <summary>Guard against feedback loops while syncing Language/Theme2 from an external change.</summary>
    private bool _uiSyncing;

    partial void OnOfficialWireGuardChanged(bool value)
    {
        _s.Set("wireguard.official", value ? "1" : "0");
        Koalix.WireGuard.WireGuardEngine.PreferOfficial = value;
    }
    partial void OnTunStackChanged(string value) => _s.Set("tun.stack", value);
    partial void OnUseWireSockChanged(bool value)
    {
        _s.Set("wireguard.wiresock", value ? "1" : "0");
        Koalix.WireGuard.WireGuardEngine.UseWireSock = value;
    }
    partial void OnStrictRouteChanged(bool value) => _s.Set("tun.strict", value ? "1" : "0");
    partial void OnTunMtuChanged(string value) => _s.Set("tun.mtu", value);
    partial void OnPoisonGuardChanged(bool value) => _s.Set("net.poisonGuard", value ? "1" : "0");
    partial void OnTlsFragmentChanged(bool value) => _s.Set("net.fragment", value ? "1" : "0");
    partial void OnMuxChanged(bool value) => _s.Set("net.mux", value ? "1" : "0");
    partial void OnWireGuardMtuChanged(string value)
    {
        _s.Set("wireguard.mtu", value);
        Koalix.WireGuard.WireGuardEngine.MtuOverride = Koalix.WireGuard.WireGuardEngine.ParseMtu(value);
    }
    partial void OnTunnelIpv6Changed(bool value) => _s.Set("tunnel.ipv6", value ? "1" : "0");
    partial void OnConnectShapeChanged(string value)
    {
        _s.Set("ui.connectShape", value);
        Controls.UiPrefs.Set(value, _connectMotion);
    }

    partial void OnEffectsChanged(bool value)
    {
        _s.Set("ui.effects", value ? "1" : "0");
        Controls.Fx.Enabled = value;
        if (App.Current.MainWindow is MainWindow w) w.StartAurora();
        if (_theme.Glass || UiStyle == "neon") _theme.SetStyle(UiStyle, _theme.Glass); // soft shadows / neon glow follow the effects switch
    }
    partial void OnCustomAccentChanged(string value)
    {
        var v = value.Trim();
        if (v.Length == 7 && v[0] == '#') v = "#FF" + v[1..];
        if (v.Length == 9 && v[0] == '#' && int.TryParse(v[1..], System.Globalization.NumberStyles.HexNumber, null, out _) && v != Accent) Accent = v;
    }
    partial void OnUpdateFeedChanged(string value) => _s.Set(UpdateService.FeedKey, value.Trim());
    partial void OnAutoUpdateChanged(bool value) => _s.Set(UpdateService.AutoKey, value ? "1" : "0");

    /// <summary>v0.4.22 — replay the first-run starter tour (user: «تو تنظیمات هم بزارش بتونن بزنن روش دوباره هم ببینن»).</summary>
    [RelayCommand]
    private void ShowTour()
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow w) w.ShowOnboarding();
    }

    [RelayCommand] private async Task CheckUpdatesAsync()
    {
        if (UpdateBusy) return;
        UpdateBusy = true; UpdateReady = false; UpdateStatus = L["Update.Checking"];
        try
        {
            var v = await _updates.CheckAsync();
            if (v is null) { UpdateStatus = L["Update.UpToDate"]; return; }
            UpdateStatus = L.Format("Update.Downloading", v);
            await _updates.DownloadAsync(p => Ui(() => UpdateProgress = p), CancellationToken.None);
            UpdateReady = true;
            UpdateStatus = L.Format("Update.Ready", v);
            Toasts.Show(L["Update.ReadyTitle"], L.Format("Update.Ready", v), ToastKind.Success);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Update.", StringComparison.Ordinal)) { UpdateStatus = L[ex.Message]; }
        catch (Exception ex) { UpdateStatus = L["Common.Error"] + ": " + ex.Message; AppHost.Log(LogCategory.Application, "update: " + ex); }
        finally { UpdateBusy = false; }
    }

    [RelayCommand] private async Task InstallUpdateAsync()
    {
        if (!UpdateReady) return;
        UpdateStatus = L["Update.Restarting"];
        // Restore DNS / proxy / firewall and stop the engine before the files are swapped.
        await AppHost.Get<Koalix.Tunneling.ConnectionManager>().DisconnectAsync(CancellationToken.None);
        File.Delete(AppHost.Get<AppPaths>().CrashMarker);
        _updates.ApplyAndRestart();
    }

    /// <summary>v0.4.47.1 — manual sing-box.exe path. Empty = auto-detect (override → KOALIX engines folder → PATH →
    /// common install locations). Set but missing files are ignored by the resolver, never a hard failure.</summary>
    [ObservableProperty] private string _singBoxPathText = "";

    partial void OnSingBoxPathTextChanged(string value)
    {
        var v = value.Trim();
        _s.Set("engine.singbox.path", v);
        _enginePaths.SingBoxPathOverride = v;
    }

    public override void OnActivated()
    {
        SingBoxVersion = _engines.InstalledVersion(EngineManager.SingBoxWindows) ?? L["Settings.NotInstalled"];
        XrayVersion = _engines.InstalledVersion(EngineManager.XrayWindows) ?? L["Settings.NotInstalled"];
    }

    partial void OnLanguageChanged(string value)
    {
        _s.Set("ui.language", value);
        if (!_uiSyncing && value != Loc.I.Language) Loc.I.SetLanguage(value);
    }
    partial void OnTheme2Changed(AppTheme value)
    {
        _s.Set("ui.theme", value.ToString());
        if (!_uiSyncing && value != _theme.Mode) _theme.Apply(value, Accent);
    }
    partial void OnBackdropChanged(Backdrop value) { if (App.Current.MainWindow is { } w) _theme.ApplyBackdrop(w, value); _s.Set("ui.backdrop", value.ToString()); }

    /* ── v0.4.57 — the beauty batch (ideas 1, 2, 5, 6, 8): auto day/night, a free accent hue, OLED pure black,
       the personal background image and compact density. ── */

    /// <summary>Idea 1: Light between sunrise and sunset, Dark otherwise — checked once a minute.</summary>
    public bool AutoTheme { get => _autoTheme; set { if (!SetProperty(ref _autoTheme, value)) return; _theme.SetAutoTheme(value, AutoSunrise, AutoSunset); _s.Set(ThemeService.AutoKey, value ? "1" : "0"); } }
    private bool _autoTheme;
    public double AutoSunrise { get => _autoSunrise; set { if (!SetProperty(ref _autoSunrise, Math.Clamp(value, 0, 23.99))) return; _theme.SetAutoTheme(AutoTheme, AutoSunrise, AutoSunset); _s.Set(ThemeService.SunriseKey, _autoSunrise.ToString(System.Globalization.CultureInfo.InvariantCulture)); } }
    private double _autoSunrise = 7;
    public double AutoSunset { get => _autoSunset; set { if (!SetProperty(ref _autoSunset, Math.Clamp(value, 0, 23.99))) return; _theme.SetAutoTheme(AutoTheme, AutoSunrise, AutoSunset); _s.Set(ThemeService.SunsetKey, _autoSunset.ToString(System.Globalization.CultureInfo.InvariantCulture)); } }
    private double _autoSunset = 19;

    /// <summary>Idea 2: any hue on the colour wheel as the accent — the derived pair (accent2, soft, gradient,
    /// aurora…) regenerates live via <see cref="ThemeService.SetAccentLive"/>.</summary>
    public bool AccentHueOn { get => _accentHueOn; set { if (!SetProperty(ref _accentHueOn, value)) return; _s.Set(ThemeService.HueOnKey, value ? "1" : "0"); if (value) _theme.SetAccentLive(ThemeService.HueHex(AccentHue)); else _theme.Apply(Theme2, _accent); } }
    private bool _accentHueOn;
    public double AccentHue { get => _accentHue; set { if (!SetProperty(ref _accentHue, value)) return; _s.Set(ThemeService.HueKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); if (AccentHueOn) _theme.SetAccentLive(ThemeService.HueHex(value)); } }
    private double _accentHue = 210;

    /// <summary>Idea 5: OLED pure black. Toggling off reloads the theme dictionary so the original surfaces return.</summary>
    public bool Oled { get => _oled; set { if (!SetProperty(ref _oled, value)) return; _s.Set(ThemeService.OledKey, value ? "1" : "0"); _theme.SetOled(value); if (!value) _theme.Apply(Theme2, Accent); } }
    private bool _oled;

    /// <summary>Idea 8: compact density lives on the shell (it scales the page host); proxied here for the toggle.</summary>
    public bool Compact { get => AppHost.Get<MainViewModel>().Compact; set { AppHost.Get<MainViewModel>().Compact = value; OnPropertyChanged(); } }

    /// <summary>Idea 6: pick a personal background image — blurred and dimmed behind everything.</summary>
    [RelayCommand] private void PickBgImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
        if (dlg.ShowDialog() != true) return;
        AppHost.Get<MainViewModel>().LoadBgImage(dlg.FileName);
        _s.Set("ui.bgImage", dlg.FileName);
    }
    [RelayCommand] private void ClearBgImage() { AppHost.Get<MainViewModel>().ClearBgImage(); _s.Set("ui.bgImage", ""); }
    partial void OnAccentChanged(string value)
    {
        StopRgb(); // v0.4.37: picking any colour (swatch, shade, die, hex box) ends the ARGB cycle
        _theme.Apply(Theme2, value); _s.Set("ui.accent", value);
        if (!string.Equals(CustomAccent, value, StringComparison.OrdinalIgnoreCase)) CustomAccent = value;
        RefreshPaletteBadges(value);
        RefreshShades(value);
    }
    partial void OnAutoReconnectChanged(bool value) => _s.Set("conn.autoReconnect", value ? "1" : "0");
    partial void OnKillSwitchChanged(bool value) => _s.Set("conn.killSwitch", value ? "1" : "0");
    partial void OnKillForeignVpnChanged(bool value) => _s.Set("vpn.killForeign", value ? "1" : "0");
    partial void OnGlobalHotkeyChanged(bool value)
    {
        _s.Set("ui.hotkey", value ? "1" : "0");
        if (App.Current.MainWindow is MainWindow w) w.SetHotkey(value);
    }
    partial void OnRevertBoostsOnExitChanged(bool value) => _s.Set("gaming.revertOnExit", value ? "1" : "0");
    partial void OnAutoRefreshSubsChanged(bool value) => _s.Set("subs.auto", value ? "1" : "0");
    partial void OnMinimizeToTrayChanged(bool value) => _s.Set("ui.tray", value ? "1" : "0");
    partial void OnVerboseLogsChanged(bool value) => _s.Set("log.verbose", value ? "1" : "0");
    partial void OnSoundsChanged(bool value)
    {
        _s.Set("ui.sounds", value ? "1" : "0");
        SoundService.Enabled = value;
    }

    /* ─────────── Sound settings (v0.4.24): per-event presets + test play + custom file + volume ─────────── */

    /// <summary>v0.4.36 — one row per sound event (7 now), each with its picker, ▶ test and custom-file button.</summary>
    public ObservableCollection<SoundRowVm> SoundRows { get; } = [];

    private static readonly (SoundService.SoundEvent Ev, string Glyph, string Brush)[] SoundRowDefs =
    [
        (SoundService.SoundEvent.Connect, "\uE768", "SuccessBrush"),
        (SoundService.SoundEvent.Disconnect, "\uE711", "DangerBrush"),
        (SoundService.SoundEvent.Error, "\uE7BA", "WarningBrush"),
        (SoundService.SoundEvent.Reconnecting, "\uE72C", "AccentBrush"),
        (SoundService.SoundEvent.PingSpike, "\uE945", "WarningBrush"),
        (SoundService.SoundEvent.AutoSwitch, "\uE8AB", "AccentBrush"),
        (SoundService.SoundEvent.FocusOn, "\uE71D", "AccentBrush"),
    ];

    private void RebuildSoundOptions()
    {
        if (SoundRows.Count == 0)
            foreach (var d in SoundRowDefs)
            {
                var row = new SoundRowVm { Event = d.Ev, Glyph = d.Glyph, BrushKey = d.Brush };
                row.ChoiceChanged += OnSoundChoiceChanged;
                SoundRows.Add(row);
            }
        foreach (var row in SoundRows) RebuildRow(row);
    }

    private static bool IsFileChoice(string c) => c.StartsWith("file:", StringComparison.Ordinal);

    private void RebuildRow(SoundRowVm row)
    {
        var ev = row.Event;
        row.Title = L["Settings.Sound" + ev];
        var choice = SoundService.Choice(ev);
        SoundOptionVm? reselect = null;
        var list = new List<SoundOptionVm> { new() { Id = "none", Label = L["Sound.None"] } };
        if (choice is "none") reselect = list[0];
        foreach (var p in SoundService.Presets(ev))
        {
            var o = new SoundOptionVm { Id = p, Label = L["Sound." + char.ToUpperInvariant(p[0]) + p[1..]] };
            list.Add(o);
            if (choice == p) reselect = o;
        }
        if (IsFileChoice(choice))
        {
            var f = new SoundOptionVm { Id = choice, Label = "\uD83C\uDFB5 " + System.IO.Path.GetFileName(choice[5..]), IsFile = true };
            list.Add(f);
            reselect = f;
        }
        row.Load(list, reselect);
    }

    partial void OnSoundVolumeChanged(double value)
    {
        var v = (int)Math.Clamp(Math.Round(value), 0, 100);
        SoundService.Volume = v;
        _s.Set(SoundService.KeyVolume, v.ToString());
    }

    private void OnSoundChoiceChanged(SoundRowVm row, SoundOptionVm value)
    {
        SoundService.SetChoice(row.Event, value.Id);
        _s.Set(SoundService.KeyFor(row.Event), value.Id);
        RebuildRow(row);
    }

    /// <summary>The ▶ test button next to each event picker.</summary>
    [RelayCommand]
    private void PlaySound(string evt)
    {
        if (Enum.TryParse<SoundService.SoundEvent>(evt, true, out var e)) SoundService.Test(e);
    }

    /// <summary>Picks a custom .wav/.mp3 for one event (v0.4.24 user list: «امکان انتخاب فایل wav/mp3 دلخواه»).</summary>
    [RelayCommand]
    private void PickSoundFile(string evt)
    {
        if (!Enum.TryParse<SoundService.SoundEvent>(evt, true, out var e)) return;
        var file = Dialogs.OpenFile("Audio (*.wav;*.mp3)|*.wav;*.mp3|All files|*.*");
        if (file is null) return;
        var choice = "file:" + file;
        SoundService.SetChoice(e, choice);
        _s.Set(SoundService.KeyFor(e), choice);
        if (SoundRows.FirstOrDefault(r => r.Event == e) is { } row) RebuildRow(row);
        SoundService.Test(e);
    }

    /// <summary>v0.4.23 (suggestion #9) → v0.4.24: the six named one-click palettes (records + live active badge) —
    /// each just presets the accent, so theme derivation, aurora and swatch selection all follow the single source of truth.</summary>
    public ObservableCollection<PaletteRowVm> Palettes { get; } =
    [
        new() { Hex = "#FF7C5CFF", NameKey = "Palette.Koala" },
        new() { Hex = "#FF10B981", NameKey = "Palette.Emerald" },
        new() { Hex = "#FFF97316", NameKey = "Palette.Sunset" },
        new() { Hex = "#FF38BDF8", NameKey = "Palette.Ice" },
        new() { Hex = "#FFF472B6", NameKey = "Palette.Rose" },
        new() { Hex = "#FFF59E0B", NameKey = "Palette.Gold" },
        new() { Hex = ArgbSentinel, NameKey = "Palette.Argb", IsRgb = true },
    ];

    public const string ArgbSentinel = "argb";

    private void RefreshPaletteBadges(string accent)
    {
        var rgb = _theme.RgbOn;
        foreach (var p in Palettes) p.IsCurrent = rgb ? p.IsRgb : string.Equals(p.Hex, accent, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SetPalette(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        if (hex == ArgbSentinel)
        {
            // v0.4.37 — the ARGB palette: the accent keeps cycling the hue wheel; any other pick stops it.
            _theme.SetRgb(true); _s.Set(ThemeService.RgbKey, "1");
            RefreshPaletteBadges(Accent);
            return;
        }
        var same = string.Equals(hex, Accent, StringComparison.OrdinalIgnoreCase);
        StopRgb();
        Accent = hex; // OnAccentChanged applies it live and persists
        if (same) RefreshPaletteBadges(Accent);
    }

    private void StopRgb()
    {
        if (!_theme.RgbOn) return;
        _s.Set(ThemeService.RgbKey, "0");
        _theme.SetRgb(false);
    }

    /* ─────────── v0.4.26 — the COLOUR WORKSHOP (user: «بخش رنگ تاکیدی و پالت رنگی یکی ان باهم ادغامشون کن
       و قابلیتشونو بیشتر کن»): the palette cards and the accent picker are ONE section now, and it grew real
       capabilities: save the current colour as your own palette card (persisted + deletable), a random-accent
       die, and six click-away shade variants of whatever accent is live. ─────────── */

    private const string CustomPalettesKey = "ui.customPalettes";

    private void LoadCustomPalettes()
    {
        try
        {
            foreach (var raw in (_s.Get(CustomPalettesKey) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (raw[0] == '#' && raw.Length is 7 or 9 && int.TryParse(raw.AsSpan(raw.Length - 6), System.Globalization.NumberStyles.HexNumber, null, out _))
                    Palettes.Add(new PaletteRowVm { Hex = NormalizeHex(raw), NameKey = "", IsCustom = true });
        }
        catch { /* a corrupt list must never keep the page from opening */ }
    }

    private static string NormalizeHex(string hex)
    {
        var body = hex.TrimStart('#').ToUpperInvariant();
        return "#" + (body.Length == 6 ? "FF" + body : body);
    }

    private void PersistCustomPalettes() =>
        _s.Set(CustomPalettesKey, string.Join(";", Palettes.Where(p => p.IsCustom).Select(p => p.Hex)));

    /// <summary>🎲 a pleasing random accent (any hue, controlled saturation/lightness) — applied instantly like any other pick.</summary>
    [RelayCommand]
    private void RandomAccent()
    {
        var h = Random.Shared.NextDouble() * 360;
        var s = 0.55 + Random.Shared.NextDouble() * 0.30;
        var l = 0.50 + Random.Shared.NextDouble() * 0.10;
        Accent = HslToHex(h, s, l);
    }

    /// <summary>Saves the current accent as a CUSTOM palette card — persisted across restarts, deletable with the card's ×.</summary>
    [RelayCommand]
    private void SaveCurrentAsPalette()
    {
        if (string.IsNullOrWhiteSpace(Accent)) return;
        var hex = NormalizeHex(Accent);
        if (Palettes.Any(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)))
        { StatusMessage = L["Palette.AlreadyExists"]; return; }
        Palettes.Add(new PaletteRowVm { Hex = hex, NameKey = "", IsCustom = true });
        PersistCustomPalettes();
        RefreshPaletteBadges(Accent);
        StatusMessage = L["Palette.Saved"];
    }

    /// <summary>Removes a custom palette card (built-ins are permanent).</summary>
    [RelayCommand]
    private void DeletePalette(string? hex)
    {
        var row = Palettes.FirstOrDefault(p => p.IsCustom && string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        Palettes.Remove(row);
        PersistCustomPalettes();
        RefreshPaletteBadges(Accent);
    }

    /// <summary>Public ObservableCollection of shade variants — rebuilt on every accent change.</summary>
    public ObservableCollection<ShadeVariantVm> ShadeVariants { get; } = [];

    /// <summary>Six one-click variants of the live accent: vivid / soft / lighter / darker / warm / cool.</summary>
    private void RefreshShades(string accent)
    {
        ShadeVariants.Clear();
        if (!TryHexToHsl(accent, out var h, out var s, out var l)) return;
        ShadeVariants.Add(new ShadeVariantVm(HslToHex(h, ClampS(s * 1.35), ClampL(l))));
        ShadeVariants.Add(new ShadeVariantVm(HslToHex(h, ClampS(s * 0.55), ClampL(l))));
        ShadeVariants.Add(new ShadeVariantVm(HslToHex(h, ClampS(s), ClampL(l + 0.12))));
        ShadeVariants.Add(new ShadeVariantVm(HslToHex(h, ClampS(s), ClampL(l - 0.12))));
        ShadeVariants.Add(new ShadeVariantVm(HslToHex(NormHue(h + 20), ClampS(s), ClampL(l))));
        ShadeVariants.Add(new ShadeVariantVm(HslToHex(NormHue(h - 20), ClampS(s), ClampL(l))));
    }

    private static double ClampS(double v) => Math.Clamp(v, 0.05, 1.0);
    private static double ClampL(double v) => Math.Clamp(v, 0.03, 0.97);
    private static double NormHue(double h) => (h % 360 + 360) % 360;

    internal static bool TryHexToHsl(string? hex, out double h, out double s, out double l)
    {
        h = s = l = 0;
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#') return false;
        var body = hex.Length == 9 ? hex[3..] : hex.Length == 7 ? hex[1..] : "";
        if (body.Length != 6 || !int.TryParse(body, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        double r = ((rgb >> 16) & 0xFF) / 255.0, g = ((rgb >> 8) & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max - min < 1e-9) { s = 0; return true; }
        s = l > 0.5 ? (max - min) / (2 - max - min) : (max - min) / (max + min);
        if (max == r) h = 60 * (((g - b) / (max - min)) % 6);
        else if (max == g) h = 60 * ((b - r) / (max - min) + 2);
        else h = 60 * ((r - g) / (max - min) + 4);
        if (h < 0) h += 360;
        return true;
    }

    internal static string HslToHex(double h, double s, double l)
    {
        double HueToRgb(double p, double q, double t)
        {
            t = (t % 1 + 1) % 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s <= 0) r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h / 360 + 1 / 3.0);
            g = HueToRgb(p, q, h / 360);
            b = HueToRgb(p, q, h / 360 - 1 / 3.0);
        }
        return $"#{0xFF:X2}{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }
    partial void OnRemoteDnsChanged(string value) { if (System.Net.IPAddress.TryParse(value, out _)) _s.Set("tunnel.remoteDns", value); }
    partial void OnDirectDnsChanged(string value) { if (System.Net.IPAddress.TryParse(value, out _)) _s.Set("tunnel.directDns", value); }
    partial void OnStartWithWindowsChanged(bool value)
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (value && Environment.ProcessPath is { } exe) run.SetValue("KOALIX", $"\"{exe}\" --minimized"); else run.DeleteValue("KOALIX", false);
    }

    [RelayCommand] private Task InstallSingBoxAsync() => InstallAsync(EngineManager.SingBoxWindows);
    [RelayCommand] private Task InstallXrayAsync() => InstallAsync(EngineManager.XrayWindows);

    /// <summary>Opens the official OpenVPN community download page (v0.4.15) — KOALIX never downloads an
    /// unverifiable binary itself; the official installer also brings the TAP/TUN drivers OpenVPN needs.</summary>
    [RelayCommand]
    private void OpenVpnDownload()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OpenVpnDownloadUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
    }

    /* ─────────── Default mode (v0.4.15) ─────────── */

    /// <summary>The KOALIX default profile: WireSock ON (when installed), strict routing ON (v0.4.30 — the Windows
    /// DNS-leak fix; see AppHost), gvisor stack, no IPv6, MTU 1280, auto-reconnect on, kill-switch off, stock DNS.</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        UseWireSock = WireSockInstalled;
        StrictRoute = true; // v0.4.30: the DNS-leak fix is part of the safe default now (was OFF since v0.4.15)
        TunStack = "gvisor";
        TunnelIpv6 = false;
        OfficialWireGuard = false;
        WireGuardMtu = "1280";
        AutoReconnect = true;
        KillSwitch = false;
        Effects = true;
        RemoteDns = "1.1.1.1";
        DirectDns = "1.1.1.1";
        StatusMessage = L["Settings.DefaultsReset"];
        Toasts.Show(L["Settings.DefaultMode"], L["Settings.DefaultsReset"], ToastKind.Success);
    }

    /* ─────────── One-click backup / restore (v0.4.12) ─────────── */

    private Koalix.Infrastructure.BackupService Backup => AppHost.Get<Koalix.Infrastructure.BackupService>();
    private bool TunnelActive => AppHost.Get<Koalix.Tunneling.ConnectionManager>().Active is not null;

    /// <summary>v0.4.50 — the tunnel gate used to be a silent StatusMessage nobody ever saw (the «کار نمیکنه»
    /// report): the backup/restore buttons LOOKED dead while a tunnel was up. Now it ASKS, with the disconnect
    /// action right inside the dialog, and continues automatically once the tunnel is down.</summary>
    private async Task<bool> EnsureTunnelDisconnected()
    {
        if (!TunnelActive) return true;
        if (!Dialogs.ConfirmCustom(L["Backup.Disconnect.Title"], L["Backup.Disconnect.Msg"], L["Backup.Disconnect.Now"], L["Common.Cancel"], dangerous: true)) return false;
        IsBusy = true;
        try { await AppHost.Get<Koalix.Tunneling.ConnectionManager>().DisconnectAsync(CancellationToken.None); }
        finally { IsBusy = false; }
        return true;
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (!await EnsureTunnelDisconnected()) return;
        // v0.4.50 («یک قابلیت بدون رمز هم بزار») — an EMPTY password now means the passwordless «بدون رمز» file.
        // v0.4.52 — «موقع گرفتن بکاپ ازم بپرسه چه چیزایی تو فایل ذخیره شه»: the picker decides what the file
        // carries — settings on by default; profiles/subscriptions/split-tunnel rules/DNS off; logs never.
        var scope = Controls.BackupScopeDialog.Show();
        if (scope is null) return;
        var pw = Dialogs.Prompt(L["Backup.Title"], L["Backup.PasswordOrEmpty"], password: true);
        if (pw is null) return;
        string? pwUse = pw.Trim().Length == 0 ? null : pw;
        if (pwUse is not null)
        {
            var pw2 = Dialogs.Prompt(L["Backup.Title"], L["Backup.PasswordRepeat"], password: true);
            if (pw2 is null || pw2 != pwUse) { BackupStatus = L["Backup.PasswordMismatch"]; return; }
        }
        var file = Dialogs.SaveFile("KOALIX backup (*.koalixbak)|*.koalixbak", $"koalix-backup-{DateTime.Now:yyyyMMdd-HHmm}.koalixbak");
        if (file is null) return;
        IsBusy = true;
        try
        {
            await Task.Run(() => Backup.Export(file, pwUse, scope)); // v0.4.52 — the picked sections only
            BackupStatus = L.Format("Backup.Done", file); // inside the card, not the top banner
            Toasts.Show(L["Backup.Title"], L["Backup.DoneToast"], ToastKind.Success);
        }
        catch (Exception ex) { BackupStatus = L["Common.Error"] + ": " + Redactor.Redact(ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!await EnsureTunnelDisconnected()) return;
        var file = Dialogs.OpenFile("KOALIX backup (*.koalixbak)|*.koalixbak|All|*.*");
        if (file is null) return;
        string? pw = null;
        if (!Koalix.Infrastructure.BackupService.IsPasswordless(file)) // v0.4.50 — passwordless files skip the prompt
        {
            pw = Dialogs.Prompt(L["Backup.Title"], L["Backup.PasswordOpen"], password: true);
            if (string.IsNullOrWhiteSpace(pw)) return;
        }
        if (!Dialogs.Confirm(L["Backup.Restore"], L.Format("Backup.ConfirmRestore", file))) return;
        IsBusy = true;
        try
        {
            await Task.Run(() => Backup.Import(file, pw));
            BackupStatus = L["Backup.Restored"]; // v0.4.52 — inside the card
            Toasts.Show(L["Backup.Title"], L["Backup.Restored"], ToastKind.Success);
            AppHost.Get<ViewModels.ProfilesViewModel>().OnActivated();
            // v0.4.50 — restore swaps the whole settings table: the running UI keeps old values until a restart.
            Dialogs.Info(L["Backup.Restore"], L["Backup.RestoreRestart"]);
        }
        catch (System.Security.Cryptography.CryptographicException) { BackupStatus = L["Backup.WrongPassword"]; }
        catch (Exception ex) { BackupStatus = L["Common.Error"] + ": " + Redactor.Redact(ex.Message); }
        finally { IsBusy = false; }
    }

    private async Task InstallAsync(EngineSpec spec)
    {
        if (IsBusy) return;
        IsBusy = true; DownloadProgress = 0;
        try
        {
            var r = await _engines.InstallAsync(spec, new Progress<double>(p => DownloadProgress = p * 100), CancellationToken.None);
            StatusMessage = r.Ok ? L.Format("Settings.Installed", spec.Name, r.Version ?? "") : L[r.Error ?? "Common.Error"];
            AppHost.Log(LogCategory.Application, $"engine {spec.Name} install: {r.Ok} {r.Version} {r.Error}");
            OnActivated();
        }
        catch (Exception ex) { StatusMessage = L["Common.Error"] + ": " + ex.Message; }
        finally { IsBusy = false; }
    }
}
