using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SingBox;
using Koalix.SystemIntegration;
using Koalix.Tunneling;
using Koalix.WireGuard;

namespace Koalix.App.ViewModels;

/// <summary>One app's session traffic (v0.4.12): aggregated from sing-box's Clash /connections per-process counters.</summary>
public sealed partial class AppUsageRow : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private string _total = "";
    [ObservableProperty] private double _barWidth;
    [ObservableProperty] private string _detail = "";
    public AppUsageRow(string name) => Name = name;
}

/// <summary>
/// A dashboard group chip (v0.4.20): a profile tag ("work", "gaming", a merged-subscription label…).
/// Chips that belong to a subscription carry its panel-reported volume/expiry — clicking them filters the server
/// list AND opens the account info strip; plain tags just filter, exactly like on the Tunnels page.
/// </summary>
public sealed partial class GroupChip : ObservableObject
{
    public string Tag { get; init; } = "";
    [ObservableProperty] private int _count;
    /// <summary>True when the tag's profiles come from a subscription (the chip then shows the account info).</summary>
    public bool IsSub { get; init; }
    public bool IsAll { get; init; }
    /// <summary>Set by the view model — drives the chip's accent highlight (v0.4.20).</summary>
    [ObservableProperty] private bool _isSelectedChip;
}

/// <summary>v0.4.23 (suggestion #6) — one country row of the dashboard "server map" card: flag, localized name,
/// how many servers live there and the best measured ping among them (empty when none was measured yet).</summary>
public sealed partial class CountryRow : ObservableObject
{
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public int Count { get; init; }
    /// <summary>"{0} سرور" / "{0} servers" — localized in the view model (the loc indexer only resolves keys).</summary>
    public string CountText { get; init; } = "";
    public string Best { get; init; } = "";
    /// <summary>v0.4.40 — best measured ping in ms (null = none measured): colours the world-map pin.</summary>
    public double? BestMs { get; init; }
    /// <summary>v0.4.40 (user: «وقتی میزنیم رو پرچما سرور هاش بالا بیاد بشه انتخاب کرد») — the servers of this
    /// country, fastest measured first; the map/flag click opens them in a picker.</summary>
    public IReadOnlyList<ProfileRow> Servers { get; init; } = [];
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly ConnectionManager _cm;
    private readonly TunnelProfileRepository _profiles;
    private readonly AppRuleRepository _rules;
    private readonly SettingsRepository _settings;
    private readonly HistoryRepository _history;
    private readonly UsageRepository _usage;
    private readonly HealthChecker _health;
    private readonly SystemProxyManager _proxy;
    private readonly GamingOptimizer _gaming;
    private readonly NetworkStateJournal _journal;
    private readonly GeoLookup _geo;
    private readonly SubscriptionRepository _subsRepo;
    private CancellationTokenSource? _trafficCts;
    private CancellationTokenSource? _pingCts;
    private CancellationTokenSource? _perAppCts;
    private long? _historyId;
    /// <summary>Session byte totals (v0.4.11) — written to connection_history on disconnect for the usage line.</summary>
    private long _sessionDown, _sessionUp;
    // Per-app usage (v0.4.12): last seen per-connection counters → deltas keep closed connections counted too.
    private readonly Dictionary<string, (long Up, long Down)> _lastConnBytes = [];
    private readonly Dictionary<string, (long Up, long Down)> _perAppTotals = [];
    /// <summary>What of each app's session total has ALREADY been persisted to app_usage (v0.4.15) — only deltas go to the DB.</summary>
    private readonly Dictionary<string, (long Up, long Down)> _persistedPerApp = [];
    /// <summary>Live cumulative counters of the OpenVPN management stream (v0.4.15) → per-second deltas.</summary>
    private long _ovpnLastUp, _ovpnLastDown;
    /// <summary>Guards the quick-switch partial methods while KOALIX itself syncs their state from settings/registry.</summary>
    private bool _syncQuick;
    // Auto smart-connect (v0.4.6): virtual "best server" row + failover watchdog.
    private ProfileRow? _autoRow;
    private long? _lastTriedId;
    private readonly List<(long Id, DateTimeOffset At)> _autoDead = [];
    private readonly System.Windows.Threading.DispatcherTimer _failoverTimer = new() { Interval = TimeSpan.FromSeconds(40) };
    private DateTimeOffset _autoStableSince;
    private int _autoSwitches;
    private bool _autoBusy;

    /// <summary>Same row model as the Tunnels page, so each server also shows its last measured ping right here (v0.4.0).</summary>
    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    /// <summary>Dashboard group chips (v0.4.20): "All" + every profile tag — click to filter, subscription groups open their account info.</summary>
    public ObservableCollection<GroupChip> Groups { get; } = [];
    [ObservableProperty] private GroupChip? _selectedGroup;
    // ── v0.4.20: the subscription account info strip (visible while a subscription group is selected) ──
    [ObservableProperty] private bool _groupInfoVisible;
    [ObservableProperty] private string _groupVolumeText = "";
    [ObservableProperty] private double _groupVolumePct;
    [ObservableProperty] private int _groupVolumeLevel;   // 0 none, 1 ok, 2 ≥80%, 3 exhausted
    [ObservableProperty] private string _groupExpireText = "";
    [ObservableProperty] private int _groupExpireLevel;    // 0 none, 1 ok, 2 ≤7d, 3 expired
    [ObservableProperty] private string _groupEngineText = "";
    [ObservableProperty] private bool _groupIsDead;
    public bool GroupVolumeWarn => GroupVolumeLevel == 2;
    public bool GroupVolumeDead => GroupVolumeLevel == 3;
    public bool GroupExpireWarn => GroupExpireLevel == 2;
    public bool GroupExpireDead => GroupExpireLevel == 3;
    /// <summary>Every enabled profile as a row, before the group filter (v0.4.20).</summary>
    private List<ProfileRow> _allRows = [];
    /// <summary>v0.4.19 (user: «تو قسمت صفحه اصلی هم یک قسمت sort بزار… بر اساس نوعشون یا پینگشون»): how the server
    /// cards are ordered — Custom keeps the manual drag-and-drop order, the others re-categorize live.</summary>
    public IReadOnlyList<string> SortOptions { get; } = ["Custom", "Ping", "Name", "Engine", "Protocol"];
    [ObservableProperty] private string _sortMode = "Ping";
    public ObservableCollection<double?> LatencyHistory { get; } = [];
    /// <summary>v0.4.36 — true for ~4 s after a sudden ping spike (focus overlay flashes red, sound plays).</summary>
    [ObservableProperty] private bool _pingSpiking;
    [ObservableProperty] private string _pingSpikeText = "";
    private readonly PingSpikeDetector _spike = new();
    private DateTimeOffset _spikeUntil;
    /// <summary>Live download/upload speed history (bytes per second) for the SpeedChart (v0.4.3).</summary>
    public ObservableCollection<double?> DownHistory { get; } = [];
    public ObservableCollection<double?> UpHistory { get; } = [];
    public ObservableCollection<string> Conflicts { get; } = [];
    [ObservableProperty] private ProfileRow? _selectedProfile;
    public TunnelProfile? SelectedTunnel => SelectedProfile?.Profile;
    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private string _latency = "—";
    [ObservableProperty] private string _upload = "0 B/s";
    [ObservableProperty] private string _download = "0 B/s";
    [ObservableProperty] private string _sessionTraffic = "";
    /// <summary>Today/this-week usage next to the session totals (v0.4.11) — "امروز ↓… • هفته ↓…".</summary>
    [ObservableProperty] private string _usageSummary = "";
    // ─── Persistent usage card (v0.4.15): today / 3 days / week / month / all-time + per-app totals ───
    public IReadOnlyList<string> UsagePeriods { get; } = ["Today", "Days3", "Week", "Month", "All"];
    [ObservableProperty] private string _usagePeriod = "Today";
    [ObservableProperty] private string _usageTotals = "";
    [ObservableProperty] private string _usageCardNote = "";
    /// <summary>Per-app usage for the SELECTED PERIOD (persisted, survives restarts) — unlike AppUsage, which is the live session.</summary>
    public ObservableCollection<AppUsageRow> HistoryAppUsage { get; } = [];
    /// <summary>Live per-app session usage (v0.4.12) — top talkers while a sing-box-based tunnel is connected.</summary>
    public ObservableCollection<AppUsageRow> AppUsage { get; } = [];
    [ObservableProperty] private bool _hasAppUsage;
    [ObservableProperty] private string _appUsageNote = "";
    [ObservableProperty] private string _directIp = "—";
    [ObservableProperty] private string _tunnelIp = "—";
    [ObservableProperty] private string _egressVerdict = "Unknown";
    // Geo of the two egress IPs (v0.4.6): country + flag next to the IPs once known.
    [ObservableProperty] private GeoInfo? _directGeo;
    [ObservableProperty] private GeoInfo? _tunnelGeo;
    [ObservableProperty] private string _splitSummary = "";
    [ObservableProperty] private bool _isAdmin = ProcessRunner.IsElevated;
    [ObservableProperty] private bool _hasRecovery;
    [ObservableProperty] private string _orbLabel = "";
    [ObservableProperty] private string _orbHint = "";
    [ObservableProperty] private string _heroTitle = "";
    [ObservableProperty] private string _heroText = "";
    [ObservableProperty] private string _uptime = "00:00:00";
    [ObservableProperty] private bool _egressVerified;
    [ObservableProperty] private bool _isPinging;
    // Quick switches on the dashboard (v0.4.3): the four toggles users should not have to hunt for across pages.
    [ObservableProperty] private bool _quickSystemProxy;
    [ObservableProperty] private bool _quickKillSwitch;
    [ObservableProperty] private bool _quickIranDirect;
    [ObservableProperty] private bool _quickGameMode;
    public bool CanRescue => State is ConnectionState.Error or ConnectionState.Degraded;
    private readonly System.Windows.Threading.DispatcherTimer _uptimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HashSet<string> _knownForeign = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConnected => State is ConnectionState.Connected or ConnectionState.Degraded;

    /* ── Geo display helpers (v0.4.6) ── */
    public bool HasDirectGeo => DirectGeo is not null;
    public bool HasTunnelGeo => TunnelGeo is not null;
    public string DirectFlag => DirectGeo is null ? "" : GeoLookup.FlagEmoji(DirectGeo.Code);
    public string TunnelFlag => TunnelGeo is null ? "" : GeoLookup.FlagEmoji(TunnelGeo.Code);
    public string DirectGeoText => GeoText(DirectGeo);
    public string TunnelGeoText => GeoText(TunnelGeo);
    private string GeoText(GeoInfo? g) => g is null ? "" : L.Language == "fa" && Services.GeoNames.Fa.TryGetValue(g.Code, out var fa) ? fa : g.Country;
    partial void OnDirectGeoChanged(GeoInfo? value) { OnPropertyChanged(nameof(HasDirectGeo)); OnPropertyChanged(nameof(DirectFlag)); OnPropertyChanged(nameof(DirectGeoText)); }
    partial void OnTunnelGeoChanged(GeoInfo? value) { OnPropertyChanged(nameof(HasTunnelGeo)); OnPropertyChanged(nameof(TunnelFlag)); OnPropertyChanged(nameof(TunnelGeoText)); }

    /// <summary>The server the auto card currently points at (localized display name for the hero, v0.4.6).</summary>
    public string AutoPickName => _autoRow?.AutoPick ?? "";

    /// <summary>True while the virtual "Auto — best server" row is the selected one (v0.4.6).</summary>
    public bool AutoOn => SelectedProfile?.IsAuto == true;
    private bool AutoActive() => _autoRow is not null && SelectedProfile == _autoRow;

    public DashboardViewModel(ConnectionManager cm, TunnelProfileRepository profiles, AppRuleRepository rules, SettingsRepository settings,
        HistoryRepository history, UsageRepository usage, HealthChecker health, NetworkStateJournal journal, SystemProxyManager proxy, GamingOptimizer gaming, GeoLookup geo,
        SubscriptionRepository subsRepo)
    {
        _cm = cm; _profiles = profiles; _rules = rules; _settings = settings; _history = history; _usage = usage; _health = health;
        _journal = journal; _proxy = proxy; _gaming = gaming; _geo = geo; _subsRepo = subsRepo;
        HasRecovery = journal.Pending().Count > 0;
        cm.StateMachine.Changed += c => Ui(() => OnState(c));
        cm.ConflictsDetected += list => Ui(() => { Conflicts.Clear(); foreach (var x in list) Conflicts.Add(L[x.Code] + " — " + x.Detail); });
        _uptimeTimer.Tick += (_, _) =>
        {
            Uptime = _cm.Active is { } a ? (DateTimeOffset.Now - a.Since).ToString(@"hh\:mm\:ss") : "00:00:00";
            // Latency card + chart (v0.4.11): fed here at a steady 1/s for EVERY mode — sing-box paths update
            // _cm.LastLatencyMs via the health monitor, WireGuard/WireSock via the endpoint ping probe.
            if (IsConnected)
            {
                Latency = _cm.LastLatencyMs is { } l ? $"{l:0} ms" : "—";
                LatencyHistory.Add(_cm.LastLatencyMs);
                while (LatencyHistory.Count > 90) LatencyHistory.RemoveAt(0);
                // v0.4.36 — spike alert + ping-coloured tray icon.
                var baseline = _spike.Baseline;
                if (_spike.Add(_cm.LastLatencyMs))
                {
                    PingSpikeText = L.Format("Focus.Spike", Math.Round(baseline ?? 0), Math.Round(_cm.LastLatencyMs ?? 0));
                    PingSpiking = true;
                    _spikeUntil = DateTimeOffset.Now.AddSeconds(4);
                    SoundService.Play(SoundService.SoundEvent.PingSpike);
                    AppHost.Log(LogCategory.Application, $"Ping spike: {baseline:0} → {_cm.LastLatencyMs:0} ms");
                }
                else if (PingSpiking && DateTimeOffset.Now > _spikeUntil) PingSpiking = false;
                try { AppHost.Get<TrayService>().SetPing(_cm.LastLatencyMs); } catch { }
                ThemeService.ReportPing(_cm.LastLatencyMs);
            }
            // Auto watchdog (v0.4.6): after 3 stable minutes the switch budget and the dead-list expire again.
            if (IsConnected && _autoStableSince != default && DateTimeOffset.Now - _autoStableSince > TimeSpan.FromMinutes(3))
            {
                _autoStableSince = DateTimeOffset.Now;
                if (_autoSwitches > 0) _autoSwitches = 0;
                _autoDead.RemoveAll(d => DateTimeOffset.Now - d.At > TimeSpan.FromMinutes(5));
            }
        };
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Ui(CheckForeignVpn);
        L.LanguageChanged += UpdateHero;
        L.LanguageChanged += () => Ui(RefreshAutoTexts);
        L.LanguageChanged += () => _ = LoadUsageCardAsync(); // the usage card is fully localized (v0.4.15)
        _failoverTimer.Tick += (_, _) => _ = AutoFailoverAsync(false);
        _syncQuick = true;
        _quickKillSwitch = settings.Get("conn.killSwitch") == "1";
        _quickIranDirect = settings.Get("route.iranDirect") == "1";
        try { _quickSystemProxy = proxy.Read().Enabled; } catch { }
        _syncQuick = false;
        UpdateHero();
    }

    /// <summary>Another VPN (e.g. Windscribe IKEv2, WARP) coming up while connected steals the default route — warn loudly.
    /// v0.4.18: when the active tunnel is KOALIX's own OpenVPN engine, the adapter it just created (“OpenVPN Data
    /// Channel Offload”) matches the foreign pattern by name — it is excluded, so KOALIX stops warning the user
    /// about its own tunnel (the field log showed exactly that false alarm on every OpenVPN connect).</summary>
    private void CheckForeignVpn()
    {
        var ownOpenVpn = _cm.Active?.Profile.Engine == EngineKind.OpenVpn;
        var now = ConflictDetector.ForeignVpnInterfaces(ownOpenVpn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = now.Except(_knownForeign).ToList();
        _knownForeign = now;
        if (added.Count == 0 || !IsConnected && State != ConnectionState.Connecting) return;
        var names = string.Join(", ", added);
        Conflicts.Add(L["Conflict.ForeignVpnAdapter"] + " — " + names);
        Toasts.Show(L["Toast.ForeignVpn.Title"], L.Format("Toast.ForeignVpn.Text", names), ToastKind.Warning);
        AppHost.Log(LogCategory.Application, "Foreign VPN interface appeared while connected: " + names, Serilog.Events.LogEventLevel.Warning);
    }

    /// <summary>
    /// v0.4.18 — the user-requested “close every filter-breaker KOALIX detects” option (Settings → Connection):
    /// before a tunnel starts, every running foreign VPN/proxy client from the known list (VeePN, v2rayN, Clash,
    /// Psiphon, WARP, Windscribe, Lantern …) is killed. Routes and DNS can no longer be hijacked mid-session by a
    /// second client. KOALIX's own engines are never touched — their PIDs are excluded explicitly and their paths
    /// live under the app folder. Failures are logged, never fatal.
    /// </summary>
    private void KillForeignVpnClients()
    {
        if (_settings.Get("vpn.killForeign") != "1") return;
        try
        {
            var ownPids = _cm.Active?.Engine.ProcessIds ?? [];
            var targets = ConflictDetector.ForeignVpnProcesses().Where(t => !ownPids.Contains(t.Pid)).ToList();
            if (targets.Count == 0) return;
            foreach (var (name, pid) in targets)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    AppHost.Log(LogCategory.Application, $"Kill-foreign: closed {name} (PID {pid}) before connecting", Serilog.Events.LogEventLevel.Warning);
                }
                catch (Exception ex) { AppHost.Log(LogCategory.Application, $"Kill-foreign: could not close {name} (PID {pid}): {Redactor.Redact(ex.Message)}"); }
            }
            var names = string.Join(", ", targets.Select(t => t.Name).Distinct());
            Toasts.Show(L["Toast.KillForeign.Title"], L.Format("Toast.KillForeign.Text", names), ToastKind.Info);
        }
        catch (Exception ex) { AppHost.Log(LogCategory.Application, "Kill-foreign failed: " + Redactor.Redact(ex.Message)); }
    }

    private void UpdateHero()
    {
        var profile = _cm.Active?.Profile.Name
            ?? (SelectedProfile?.IsAuto == true && AutoPickName.Length > 0 ? AutoPickName : SelectedProfile?.Name)
            ?? "";
        (OrbLabel, OrbHint, HeroTitle, HeroText) = State switch
        {
            ConnectionState.Connected => (L["Orb.Connected"], L["Orb.ClickToDisconnect"], L["Hero.Connected"], L.Format("Hero.ConnectedText", profile, _cm.Active?.Profile.Engine.ToString() ?? "")),
            ConnectionState.Degraded => (L["Orb.Connected"], L["Orb.ClickToDisconnect"], L["Hero.Degraded"], L["Hero.DegradedText"]),
            ConnectionState.Connecting => (L["Orb.Connecting"], L["Orb.Wait"], L["Hero.Connecting"], profile),
            ConnectionState.Reconnecting => (L["Orb.Connecting"], L["Orb.Wait"], L["Hero.Reconnecting"], profile),
            ConnectionState.Disconnecting => (L["Orb.Disconnecting"], L["Orb.Wait"], L["Hero.Disconnecting"], ""),
            ConnectionState.Error => (L["Orb.Retry"], L["Orb.ClickToConnect"], L["Hero.Error"], StatusMessage ?? ""),
            _ => (L["Orb.Connect"], L["Orb.ClickToConnect"], L["Hero.Disconnected"], L["Hero.DisconnectedText"]),
        };
    }

    partial void OnSelectedProfileChanged(ProfileRow? value)
    {
        foreach (var r in Profiles) r.IsSelected = r == value;
        UpdateHero();
    }

    public override void OnActivated()
    {
        var sel = SelectedProfile?.Profile?.Id;
        var autoWanted = _settings.Get("conn.auto") == "1";
        // v0.4.19: the chosen sort mode persists ("dash.sort"); Ping stays the shipped default.
        var savedSort = _settings.Get("dash.sort");
        if (savedSort is not null && SortOptions.Contains(savedSort)) SortMode = savedSort;
        Profiles.Clear();
        _autoRow = new ProfileRow(null);
        // v0.4.27 — the virtual row gets its localized title IMMEDIATELY: it is selected below (auto mode / restored
        // selection) before RefreshAutoTexts() runs at the end, and UpdateHero reads .Name on that selection — the
        // gap used to surface as the occasional NullReferenceException toast on the dashboard.
        _autoRow.SetAutoTexts(L["Auto.Title"], L["Auto.Sub"], "");
        Profiles.Add(_autoRow);
        _allRows = new List<ProfileRow>();
        foreach (var p in _profiles.All().Where(p => p.Enabled))
        {
            var row = new ProfileRow(p);
            if (p.LastLatencyMs is { } ms) row.SetResult(ms, false, null);
            _allRows.Add(row);
        }
        RebuildGroups();
        SortProfiles(sel);
        BuildCountryMap(); // v0.4.23 (suggestion #6): the "server map" card refreshes with the list
        // Restore the auto selection (v0.4.6); otherwise never leave the virtual row selected by accident.
        if (autoWanted && _autoRow is not null) SelectedProfile = _autoRow;
        else if (SelectedProfile is null || SelectedProfile.IsAuto)
            SelectedProfile = Profiles.FirstOrDefault(r => !r.IsAuto && r.Profile?.LastLatencyMs is not null) ?? Profiles.FirstOrDefault(r => !r.IsAuto);
        RefreshAutoTexts();
        var policy = CurrentPolicy();
        var (t, d, b, def) = policy.Preview();
        SplitSummary = L.Format("Dash.SplitSummary", L["Split.Mode." + policy.Mode], t.Count, d.Count, b.Count);
        _ = LoadUsageCardAsync(); // the usage card is independent of being connected (v0.4.15)
    }

    /// <summary>Localized texts of the virtual auto row; called on activation and on every language switch (v0.4.6).</summary>
    private void RefreshAutoTexts()
    {
        if (_autoRow is null) return;
        var pick = BestProfile()?.Name ?? "";
        _autoRow.SetAutoTexts(L["Auto.Title"], pick.Length > 0 ? L.Format("Auto.SubPick", pick) : L["Auto.Sub"], pick);
    }

    /// <summary>
    /// v0.4.23 (suggestion #6, user: «نقشهٔ جغرافیایی سرورها — پرچم‌ها که داریم؛ یک نگاه کلی روی موقعیت‌ها»):
    /// groups the server cards by country (from the flag emoji / ISO token in their names) — one glance shows
    /// where the fleet lives and which country is fastest. Best ping = the last measured value, honest and
    /// stale-safe: countries with no measurement just show the count.
    /// </summary>
    private void BuildCountryMap()
    {
        var rows = _allRows
            .Select(r => (Row: r, Code: Koalix.Infrastructure.ServerCountry.FromName(r.Name)))
            .Where(x => x.Code is not null)
            .GroupBy(x => x.Code!)
            .Select(g =>
            {
                var best = g.Select(x => x.Row)
                    .Where(r => double.TryParse(r.Ping, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    .Select(r => double.Parse(r.Ping, System.Globalization.CultureInfo.InvariantCulture))
                    .DefaultIfEmpty(double.NaN).Min();
                var bestText = double.IsNaN(best) ? "" : L.Format("Geo.BestFmt", best.ToString("0"));
                var title = L.Language == "fa" && Services.GeoNames.Fa.TryGetValue(g.Key, out var fa) ? fa : g.Key;
                return new CountryRow { Code = g.Key, Title = title, Count = g.Count(), CountText = L.Format("Geo.ServersFmt", g.Count()), Best = bestText,
                    BestMs = double.IsNaN(best) ? null : best, Servers = ByPing(g.Select(x => x.Row)) };
            })
            .OrderByDescending(r => r.Count)
            .ToList();
        var unknown = _allRows.Count - rows.Sum(r => r.Count);
        if (unknown > 0)
        {
            var rest = _allRows.Where(r => Koalix.Infrastructure.ServerCountry.FromName(r.Name) is null);
            rows.Add(new CountryRow { Code = "", Title = L["Geo.Unknown"], Count = unknown, CountText = L.Format("Geo.ServersFmt", unknown), Servers = ByPing(rest) });
        }
        var open = PickedCountry?.Code;
        CountryRows.Clear();
        foreach (var r in rows) CountryRows.Add(r);
        HasCountryRows = CountryRows.Count > 0 && _allRows.Count > 1;
        // keep an open picker alive across a refresh (e.g. a ping sweep finishing while it is open)
        if (open is not null) { PickedCountry = CountryRows.FirstOrDefault(r => r.Code == open); if (PickedCountry is null) CountryPickerOpen = false; }
    }

    private static List<ProfileRow> ByPing(IEnumerable<ProfileRow> rows) =>
        rows.Select(r => (Row: r, Ms: double.TryParse(r.Ping, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null))
            .OrderBy(x => x.Ms is null ? 1 : 0).ThenBy(x => x.Ms ?? 0).ThenBy(x => x.Row.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Row).ToList();

    /// <summary>v0.4.40 — the country whose servers the picker shows (map pin or flag chip click).</summary>
    [ObservableProperty] private CountryRow? _pickedCountry;
    [ObservableProperty] private bool _countryPickerOpen;

    [RelayCommand]
    private void OpenCountry(CountryRow? row)
    {
        if (row is null || row.Servers.Count == 0) return;
        PickedCountry = row;
        CountryPickerOpen = false; // re-open so the popup re-anchors at the new mouse point
        CountryPickerOpen = true;
    }

    [RelayCommand] private void CloseCountryPicker() => CountryPickerOpen = false;

    /// <summary>Row click in the picker: select that server (same as clicking its card).</summary>
    [RelayCommand]
    private void PickCountryServer(ProfileRow? row)
    {
        if (row is null) return;
        var target = Profiles.FirstOrDefault(r => r.Profile?.Id == row.Profile?.Id) ?? row;
        SelectProfile(target);
        CountryPickerOpen = false;
    }

    /// <summary>Connect button in the picker: select and connect right away.</summary>
    [RelayCommand]
    private async Task ConnectCountryServerAsync(ProfileRow? row)
    {
        if (row?.Profile is null) return;
        CountryPickerOpen = false;
        await ConnectToAsync(row.Profile);
    }

    public ObservableCollection<CountryRow> CountryRows { get; } = [];
    [ObservableProperty] private bool _hasCountryRows;

    /// <summary>Fastest measured profile (never the auto row); null when there are no profiles at all (v0.4.6).</summary>
    private TunnelProfile? BestProfile(IEnumerable<ProfileRow>? pool = null) =>
        (pool ?? Profiles).Where(r => !r.IsAuto && r.Profile?.LastLatencyMs is not null).OrderBy(r => r.Profile!.LastLatencyMs).Select(r => r.Profile).FirstOrDefault()
        ?? (pool ?? Profiles).Where(r => !r.IsAuto).Select(r => r.Profile).FirstOrDefault();

    partial void OnSortModeChanged(string value)
    {
        try { _settings.Set("dash.sort", value); } catch { }
        SortProfiles(SelectedProfile?.Profile?.Id);
    }

    /// <summary>Server cards in the user's chosen order (v0.4.19): Ping = fastest measured first (the long-standing
    /// default), Custom = the manual drag-and-drop order from the Tunnels page, Name/Engine/Protocol = categorized.
    /// The virtual auto row always stays pinned first (v0.4.6). v0.4.20: the selected group chip filters first.</summary>
    private void SortProfiles(long? keepId = null)
    {
        var auto = Profiles.FirstOrDefault(r => r.IsAuto);
        IEnumerable<ProfileRow> pool = _allRows;
        if (SelectedGroup?.Tag is { Length: > 0 } tag)
            pool = pool.Where(r => r.Profile?.Tag is { } t && string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase));
        var ordered = (SortMode switch
        {
            "Custom" => pool,                                        // already in repo (drag) order
            "Name" => pool.OrderBy(r => r.Name, StringComparer.CurrentCulture),
            "Engine" => pool.OrderBy(r => r.Engine, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.CurrentCulture),
            "Protocol" => pool.OrderBy(r => r.Source, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.CurrentCulture),
            _ => pool.OrderBy(r => r.Profile?.LastLatencyMs is null ? 1 : 0)   // Ping (default)
                          .ThenBy(r => r.Profile?.LastLatencyMs ?? double.MaxValue)
                          .ThenBy(r => r.Name, StringComparer.CurrentCulture),
        }).ToList();
        var bestId = ordered.Where(r => r.Profile?.LastLatencyMs is not null).Select(r => r.Profile!.Id).FirstOrDefault();
        var autoWasSelected = SelectedProfile?.IsAuto == true;
        Profiles.Clear();
        if (auto is not null) Profiles.Add(auto);
        foreach (var r in ordered)
        {
            r.IsBest = r.Profile?.LastLatencyMs is not null && r.Profile.Id == bestId;
            Profiles.Add(r);
        }
        SelectedProfile = Profiles.FirstOrDefault(r => !r.IsAuto && r.Profile?.Id == keepId)
            ?? (autoWasSelected && auto is not null ? auto : Profiles.FirstOrDefault(r => !r.IsAuto));
    }

    /// <summary>Rebuilds the group chips (v0.4.20): "All" + every tag, with live counts and the subscription marker.
    /// A tag whose profiles carry a SubscriptionId becomes a subscription chip (dot marker) — its click opens the
    /// account info strip; plain tags only filter the list.</summary>
    private void RebuildGroups()
    {
        var keep = SelectedGroup?.Tag;
        var byTag = _allRows.GroupBy(r => r.Profile?.Tag?.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key)).ToList();
        var subTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in byTag)
            if (g.Any(r => r.Profile?.SubscriptionId is not null)) subTags.Add(g.Key!);
        Groups.Clear();
        Groups.Add(new GroupChip { Tag = "", IsAll = true, Count = _allRows.Count });
        foreach (var g in byTag)
            Groups.Add(new GroupChip { Tag = g.Key!, Count = g.Count(), IsSub = subTags.Contains(g.Key!) });
        SelectedGroup = Groups.FirstOrDefault(c => string.Equals(c.Tag, keep, StringComparison.OrdinalIgnoreCase)) ?? Groups[0];
        foreach (var c in Groups) c.IsSelectedChip = ReferenceEquals(c, SelectedGroup);
        if (SelectedGroup.Tag.Length == 0) UpdateGroupInfo(null);
    }

    /// <summary>The chip click (v0.4.20): filters the server cards; a subscription chip also opens its account info.</summary>
    [RelayCommand] private void PickGroup(GroupChip? chip)
    {
        if (chip is null) return;
        SelectedGroup = chip;
        foreach (var c in Groups) c.IsSelectedChip = ReferenceEquals(c, chip);
        SortProfiles(SelectedProfile?.Profile?.Id);
        UpdateGroupInfo(chip.IsSub ? chip.Tag : null);
    }

    /// <summary>Fills the subscription info strip (v0.4.20) — aggregates every subscription whose profiles carry this tag.</summary>
    private void UpdateGroupInfo(string? tag)
    {
        if (tag is null)
        {
            GroupInfoVisible = false;
            return;
        }
        var subIds = _profiles.All().Where(p => p.Tag is not null && string.Equals(p.Tag.Trim(), tag, StringComparison.OrdinalIgnoreCase) && p.SubscriptionId is not null)
            .Select(p => p.SubscriptionId!.Value).Distinct().ToList();
        var subs = _subsRepo.All().Where(s => subIds.Contains(s.Id)).ToList();
        if (subs.Count == 0) { GroupInfoVisible = false; return; }

        var used = subs.Sum(s => (s.UploadBytes ?? 0) + (s.DownloadBytes ?? 0));
        var total = subs.Sum(s => s.TotalBytes ?? 0);
        if (total > 0 || used > 0)
        {
            GroupVolumeText = total > 0 ? Fmt(used) + " / " + Fmt(total) : Fmt(used);
            GroupVolumePct = total > 0 ? Math.Clamp(used * 100.0 / total, 0, 100) : 100;
            GroupVolumeLevel = total > 0 && used >= total ? 3 : GroupVolumePct >= 80 ? 2 : 1;
        }
        else { GroupVolumeText = ""; GroupVolumePct = 0; GroupVolumeLevel = 0; }

        var expires = subs.Where(s => s.ExpireUnix is not null).Select(s => DateTimeOffset.FromUnixTimeSeconds(s.ExpireUnix!.Value).ToLocalTime()).ToList();
        if (expires.Count > 0)
        {
            var at = expires.Min();
            var left = at - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero) { GroupExpireText = L["Sub.Expired"]; GroupExpireLevel = 3; }
            else if (left <= TimeSpan.FromDays(7)) { GroupExpireText = L.Format("Sub.DaysLeft", Math.Max(1, (int)Math.Ceiling(left.TotalDays))); GroupExpireLevel = 2; }
            else { GroupExpireText = L.Format("Sub.ExpireOn", at.ToString("yyyy-MM-dd")); GroupExpireLevel = 1; }
        }
        else { GroupExpireText = ""; GroupExpireLevel = 0; }

        var engines = subs.Select(s => s.Engine?.ToString()).Where(e => e is not null).Distinct().ToList();
        GroupEngineText = string.Join(" · ", engines);
        GroupIsDead = GroupVolumeLevel == 3 || GroupExpireLevel == 3;
        GroupInfoVisible = true;
        OnPropertyChanged(nameof(GroupVolumeWarn)); OnPropertyChanged(nameof(GroupVolumeDead));
        OnPropertyChanged(nameof(GroupExpireWarn)); OnPropertyChanged(nameof(GroupExpireDead));
    }

    public SplitPolicy CurrentPolicy()
    {
        var mode = Enum.TryParse<SplitMode>(_settings.Get("split.mode"), out var m) ? m : SplitMode.FullTunnel;
        return new SplitPolicy(mode, _rules.All(), _settings.Get("split.tunnelDns") != "0", _settings.Get("split.blockIpv6") == "1",
            _settings.Get("route.iranDirect") == "1")
        {
            ProxyDomains = SplitPolicy.ParseDomains(_settings.Get("route.proxyDomains")),
            DirectDomains = SplitPolicy.ParseDomains(_settings.Get("route.directDomains")),
            BlockQuic = _settings.Get("route.blockQuic") != "0",
        };
    }

    private void OnState(StateChange c)
    {
        var wasUp = State is ConnectionState.Connected or ConnectionState.Degraded;
        State = c.To;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(AutoOn));
        OnPropertyChanged(nameof(CanRescue));
        ConnectCommand.NotifyCanExecuteChanged();
        RescueCommand.NotifyCanExecuteChanged();
        var tray = AppHost.Get<TrayService>();
        tray.SetStatus("KOALIX — " + L["State." + c.To]);
        tray.SetMenuState(L["State." + c.To], IsConnected); // live header + dot in the tray menu (v0.4.12)
        tray.SetConnected(IsConnected);
        if (c.To == ConnectionState.Connected && !wasUp)
        {
            DownHistory.Clear(); UpHistory.Clear(); SessionTraffic = "";
            _sessionDown = _sessionUp = 0;
            StartTraffic(); _ = RefreshIpsAsync(); _uptimeTimer.Start();
            _ = LoadUsageAsync();
            _knownForeign = ConflictDetector.ForeignVpnInterfaces().ToHashSet(StringComparer.OrdinalIgnoreCase);
            Toasts.Show(L["Toast.Connected.Title"], L.Format("Toast.Connected.Text", _cm.Active?.Profile.Name ?? ""), ToastKind.Success);
            _autoStableSince = DateTimeOffset.Now;
        }
        if (c.To == ConnectionState.Degraded && !wasUp) { _sessionDown = _sessionUp = 0; StartTraffic(); _ = RefreshIpsAsync(); _uptimeTimer.Start(); }
        if (c.To == ConnectionState.Degraded && c.From == ConnectionState.Connected) Toasts.Show(L["Hero.Degraded"], L["Hero.DegradedText"], ToastKind.Warning);
        if (c.To == ConnectionState.Reconnecting) Toasts.Show(L["Hero.Reconnecting"], null, ToastKind.Warning);
        if (c.To is ConnectionState.Disconnected or ConnectionState.Error)
        {
            _trafficCts?.Cancel(); _uptimeTimer.Stop(); Uptime = "00:00:00";
            _spike.Reset(); PingSpiking = false;
            Upload = Download = "0 B/s"; Latency = "—"; TunnelIp = "—"; EgressVerdict = "Unknown"; EgressVerified = false;
            TunnelGeo = null;
            DownHistory.Clear(); UpHistory.Clear(); SessionTraffic = "";
            PersistPerApp(); // final flush of the session's per-app usage (v0.4.15)
            ResetPerApp(); // per-app usage belongs to the session (v0.4.12)
            _syncQuick = true; QuickSystemProxy = false; _syncQuick = false;
            if (_historyId is { } id)
            {
                _history.End(id, c.To.ToString(), _cm.LastLatencyMs, _sessionUp, _sessionDown);
                _historyId = null;
                _sessionDown = _sessionUp = 0;
                _ = LoadUsageAsync();
                _ = LoadUsageCardAsync(); // totals grew by this session (v0.4.15)
            }
        }
        // Auto smart-connect watchdog (v0.4.6): degraded → wait one interval, still bad? switch to the next best server.
        if (c.To == ConnectionState.Degraded && AutoActive())
        {
            _autoStableSince = DateTimeOffset.Now;
            if (!_autoBusy) _failoverTimer.Start();
        }
        else if (c.To == ConnectionState.Connected) _failoverTimer.Stop();
        if (c.To == ConnectionState.Error && AutoActive() && !_autoBusy) _ = AutoFailoverAsync(true);
        if (c.Reason is not null && c.To == ConnectionState.Error)
        {
            StatusMessage = string.Join("\n", c.Reason.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => L[x]));
            Toasts.Show(L["Toast.Error.Title"], StatusMessage, ToastKind.Error);
        }
        else if (c.To == ConnectionState.Disconnected && (wasUp || c.From == ConnectionState.Disconnecting))
            Toasts.Show(L["Toast.Disconnected.Title"], L["Toast.Disconnected.Text"], ToastKind.Info);
        UpdateHero();
    }

    private bool CanConnect() => State is ConnectionState.Disconnected or ConnectionState.Error or ConnectionState.Connected or ConnectionState.Degraded;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected) { await _cm.DisconnectAsync(CancellationToken.None); return; }
        if (SelectedProfile is { IsAuto: true }) { await ConnectBestAsync(); return; }
        if (SelectedProfile?.Profile is not { } profile) { StatusMessage = L["Dash.NoProfile"]; return; }
        await StartConnectAsync(profile);
    }

    /// <summary>Auto mode (v0.4.6): connect the fastest measured server; when nothing was measured yet, ping-test first, then connect.
    /// Selection stays on the auto card — the mode is what is selected, the card's subtitle shows the current pick.</summary>
    private async Task ConnectBestAsync()
    {
        if (BestProfile() is not { } best)
        {
            StatusMessage = L["Auto.Picking"];
            await RunPingTestsAsync(Profiles.Where(r => !r.IsAuto).ToList());
            best = BestProfile();
            if (best is null) { StatusMessage = L["Dash.NoProfile"]; return; }
        }
        await StartConnectAsync(best);
    }

    /* ─────────── Auto smart-connect watchdog (v0.4.6) ─────────── */

    /// <summary>
    /// The heart of the "Auto — best server" card. fromError: the connect attempt just failed → retry with the next
    /// candidate after a short pause. Otherwise (timer): the tunnel has been Degraded for a full interval → switch.
    /// A switch budget (4) plus a dead-list (5 min) make the loop honest instead of flapping forever.
    /// </summary>
    private async Task AutoFailoverAsync(bool fromError)
    {
        if (!AutoActive() || _autoBusy || State is ConnectionState.Disconnecting or ConnectionState.Connecting or ConnectionState.Reconnecting) return;
        if (!fromError)
        {
            if (_cm.StateMachine.State != ConnectionState.Degraded || !IsConnected) return; // recovered meanwhile
            if (_autoSwitches >= 4)
            {
                _failoverTimer.Stop();
                Toasts.Show(L["Auto.Title"], L["Auto.GaveUp"], ToastKind.Warning);
                return;
            }
        }
        var failing = _cm.Active?.Profile.Id ?? _lastTriedId;
        if (failing is long failingId) { _autoDead.RemoveAll(d => DateTimeOffset.Now - d.At > TimeSpan.FromMinutes(5)); _autoDead.Add((failingId, DateTimeOffset.Now)); }
        // v0.4.16: never auto-pick an OpenVPN profile while the policy is per-app — OpenVPN cannot honor it and
        // the connect would only fail (a confirm dialog mid-auto would be worse).
        var openVpnAllowed = CurrentPolicy().Mode == SplitMode.FullTunnel;
        var pool = Profiles.Where(r => !r.IsAuto && r.Profile is not null && _autoDead.All(d => d.Id != r.Profile!.Id)
            && (openVpnAllowed || r.Profile.Engine != EngineKind.OpenVpn)).ToList();
        var candidate = BestProfile(pool);
        if (candidate is null) { _autoDead.Clear(); candidate = BestProfile(); } // everyone blocked → start over, honest retry
        if (candidate is null || candidate.Id == failing) { _failoverTimer.Stop(); return; }
        _autoBusy = true;
        _failoverTimer.Stop();
        try
        {
            _autoSwitches++;
            AppHost.Log(LogCategory.Application, $"Auto: switching away from '{failing}' → '{candidate.Name}' ({candidate.LastLatencyMs?.ToString("0") ?? "?"} ms)");
            Toasts.Show(L["Auto.Title"], L.Format("Auto.Switching", candidate.Name), ToastKind.Info);
            SoundService.Play(SoundService.SoundEvent.AutoSwitch);
            if (_cm.Active is not null) await _cm.DisconnectAsync(CancellationToken.None);
            await Task.Delay(fromError ? 4000 : 1200, CancellationToken.None);
            // The user may have taken over during the pause (manual disconnect/selection) — never fight them.
            if (!AutoActive() || (fromError && State != ConnectionState.Error) || State is ConnectionState.Connecting or ConnectionState.Disconnecting) return;
            await StartConnectAsync(candidate);
            RefreshAutoTexts(); // subtitle now points at the new server
        }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
        finally { _autoBusy = false; }
    }

    /// <summary>Connect (or disconnect, when already active) a specific profile — used by the Tunnels page (v0.4.0), where each server row now has its own Connect button.</summary>
    public async Task ConnectToAsync(TunnelProfile profile)
    {
        if (_cm.Active?.Profile.Id == profile.Id) { await _cm.DisconnectAsync(CancellationToken.None); return; }
        SelectedProfile = Profiles.FirstOrDefault(r => r.Profile?.Id == profile.Id);
        _settings.Set("conn.auto", "0"); // an explicit server pick is a manual decision (v0.4.6)
        await StartConnectAsync(profile);
    }

    /// <summary>v0.4.22 (user: «یک گزینه بزار که بزنن روش خود اپ بسته شه و حالت ادمین باز شه») — under the admin
    /// warning: relaunch elevated. The single-instance mutex is released BEFORE the new process starts, so the
    /// elevated instance boots cleanly; if the UAC prompt is declined nothing happens and we say so.</summary>
    [RelayCommand]
    private void RestartAdmin()
    {
        if (ProcessRunner.IsElevated) return;
        if (ProcessRunner.RestartElevated()) { App.Current.ReleaseSingleInstance(); App.Current.Shutdown(); }
        else Toasts.Show(L["Admin.Title"], L["Admin.RestartDeclined"], ToastKind.Warning);
    }

    private async Task StartConnectAsync(TunnelProfile profile)
    {
        if (!ProcessRunner.IsElevated)
        {
            if (Services.Dialogs.Confirm(L["Admin.Title"], L["Admin.TunNeedsAdmin"])) { if (ProcessRunner.RestartElevated()) { App.Current.ReleaseSingleInstance(); App.Current.Shutdown(); } }
            return;
        }
        var policy = CurrentPolicy();
        var errors = policy.Validate();
        if (errors.Count > 0) { StatusMessage = string.Join("\n", errors.Select(e => L[e])); return; }
        // v0.4.18 — optional “close detected filter-breakers”: runs before the engine starts so the killed clients
        // cannot re-grab the route while KOALIX connects. KOALIX's own engine PIDs are excluded.
        KillForeignVpnClients();
        // v0.4.16 — OpenVPN cannot do per-app routing (the official client owns the whole adapter). Instead of the
        // old dead-end refusal (the user's per-app policy made EVERY OpenVPN connect fail instantly), offer the
        // full tunnel for this run. The saved split mode is untouched — the next connect decides again.
        if (profile.Engine == EngineKind.OpenVpn && policy.Mode != SplitMode.FullTunnel)
        {
            if (!Services.Dialogs.Confirm(L["Ovpn.FullTunnel.Title"], L["Ovpn.FullTunnel.Text"], dangerous: false))
            { StatusMessage = L["Ovpn.FullTunnel.Cancelled"]; return; }
            policy = policy with { Mode = SplitMode.FullTunnel };
            var (t2, d2, b2, _) = policy.Preview();
            SplitSummary = L.Format("Dash.SplitSummary", L["Split.Mode." + policy.Mode], t2.Count, d2.Count, b2.Count);
        }
        _cm.Options = _cm.Options with
        {
            AutoReconnect = _settings.Get("conn.autoReconnect") != "0",
            KillSwitch = _settings.Get("conn.killSwitch") == "1",
        };
        _lastTriedId = profile.Id;
        _autoStableSince = DateTimeOffset.Now;
        _historyId = _history.Start(profile.Name, profile.Engine);
        StatusMessage = null;
        await _cm.ConnectAsync(profile, policy, CancellationToken.None);
    }

    [RelayCommand] private void RestartAsAdmin() { if (ProcessRunner.RestartElevated()) App.Current.Shutdown(); }

    /// <summary>Escape hatch for the error/degraded state (v0.4.3): one click retries with the fastest known profile instead of leaving the user in a dead end.</summary>
    [RelayCommand(CanExecute = nameof(CanRescue))]
    private async Task RescueAsync()
    {
        var best = Profiles.Where(r => r.Profile?.LastLatencyMs is not null).OrderBy(r => r.Profile!.LastLatencyMs).FirstOrDefault() ?? Profiles.FirstOrDefault(r => !r.IsAuto);
        if (best?.Profile is not { } profile) { StatusMessage = L["Dash.NoProfile"]; return; }
        if (_cm.Active is not null) await _cm.DisconnectAsync(CancellationToken.None);
        await StartConnectAsync(profile);
    }

    /// <summary>The split-tunnel summary pill became a button (v0.4.3): the traffic path is the user's most important decision, one click away.</summary>
    [RelayCommand] private void OpenSplit()
    {
        var main = AppHost.Get<MainViewModel>();
        main.Selected = main.Nav.First(n => n.Key == "Nav.Split");
    }

    /// <summary>Server card click (v0.4.3) — the ComboBox became cards, selection is a click. The auto card stores its state (v0.4.6).</summary>
    [RelayCommand] private void SelectProfile(ProfileRow? row)
    {
        if (row is null) return;
        var wasAuto = AutoOn;
        SelectedProfile = row;
        _settings.Set("conn.auto", row.IsAuto ? "1" : "0");
        if (row.IsAuto && !wasAuto) Toasts.Show(L["Auto.Title"], L["Auto.EnabledTip"], ToastKind.Success);
        if (!row.IsAuto) _failoverTimer.Stop();
    }

    /// <summary>Palette entry (v0.4.6): flip between auto and manual selection.</summary>
    public void ToggleAutoFromPalette()
    {
        if (AutoOn)
        {
            var first = Profiles.FirstOrDefault(r => !r.IsAuto);
            if (first is not null) SelectProfileCommand.Execute(first);
        }
        else if (_autoRow is not null) SelectProfileCommand.Execute(_autoRow);
    }

    /// <summary>Real-delay test for every profile right from the dashboard (same tester as the Tunnels page, v0.4.3).</summary>
    [RelayCommand]
    private Task TestPingAsync() => RunPingTestsAsync(Profiles.ToList());

    /// <summary>v0.4.20 (user: «تو داشبورد هم یه چیز بزار سرور انتخاب میکنیم تست بتونیم بگیریم ازش»): test ONLY the
    /// selected server — one card, one honest measurement, no full sweep.</summary>
    [RelayCommand]
    private Task TestSelectedPingAsync() => SelectedProfile is { IsAuto: false } row ? RunPingTestsAsync([row]) : Task.CompletedTask;

    /// <summary>Public wrapper for the command palette (v0.4.3).</summary>
    public Task PingAllAsync() => RunPingTestsAsync(Profiles.ToList());

    /// <summary>Public wrapper for the command palette (v0.4.3).</summary>
    public Task RefreshEgressAsync() => RefreshIpsAsync();

    /// <summary>Public wrapper for the command palette (v0.4.3): connect/disconnect the selected profile, same as the orb.</summary>
    public Task ConnectSelectedAsync() => ConnectAsync();

    private async Task RunPingTestsAsync(List<ProfileRow> rows)
    {
        rows = rows.Where(r => !r.IsAuto).ToList();
        if (IsPinging) { _pingCts?.Cancel(); return; }
        if (rows.Count == 0) return;
        IsPinging = true;
        _pingCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var r in rows) r.SetTesting();
        var byId = rows.ToDictionary(r => r.Profile.Id);
        var progress = new Progress<LatencyResult>(res =>
        {
            if (!byId.TryGetValue(res.ProfileId, out var row)) return;
            row.Profile = row.Profile with { LastLatencyMs = res.Ms };
            row.SetResult(res.Ms, res.Method == LatencyMethod.TcpPing, res.Error);
            try { _profiles.SetLatency(res.ProfileId, res.Ms); } catch { }
        });
        try
        {
            var results = await Task.Run(() => new ProfileLatencyTester(AppHost.Get<EnginePaths>()).TestAsync(rows.Select(r => r.Profile).ToList(), progress, _pingCts.Token));
            var ok = results.Count(r => r.Ms is not null);
            StatusMessage = L.Format("Profiles.TestReport", ok, results.Count);
            SortProfiles(SelectedProfile?.Profile.Id);
            BuildCountryMap(); // v0.4.40: the map pins recolour with the fresh best pings
            RefreshAutoTexts(); // the auto card's "fastest: …" line follows new measurements (v0.4.6)
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L["Common.Cancelled"];
            foreach (var r in rows.Where(r => r.PingLevel == 5)) r.SetResult(null, false, null);
        }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
        finally { IsPinging = false; _pingCts?.Dispose(); _pingCts = null; }
    }

    [RelayCommand]
    private async Task RefreshIpsAsync()
    {
        DirectIp = await _health.PublicIpDirectAsync(null, CancellationToken.None) ?? L["Common.Unknown"];
        DirectGeo = await _geo.LookupAsync(IpOrNull(DirectIp), null, CancellationToken.None);
        if (_cm.Active?.Engine.HealthProxyPort is { } port)
        {
            var t = await _health.PublicIpViaProxyAsync(port, CancellationToken.None);
            TunnelIp = t ?? L["Common.Unknown"];
            // The exit country is looked up THROUGH the tunnel, so blocked geo APIs still answer (v0.4.6).
            TunnelGeo = await _geo.LookupAsync(IpOrNull(TunnelIp), port, CancellationToken.None);
            EgressVerdict = EgressVerifier.Compare(DirectIp == L["Common.Unknown"] ? null : DirectIp, t).Verdict;
            EgressVerified = EgressVerdict == "Verified";
        }
    }

    /// <summary>"—"/unknown text → null so the geo lookup treats it as "this connection"; otherwise the parsed IP.</summary>
    private static string? IpOrNull(string text) => System.Net.IPAddress.TryParse(text.Trim(), out _) ? text.Trim() : null;

    /* ─────────── Quick switches (v0.4.3) ─────────── */

    partial void OnQuickKillSwitchChanged(bool value)
    {
        if (_syncQuick) return;
        _settings.Set("conn.killSwitch", value ? "1" : "0");
        if (IsConnected) Toasts.Show(L["Quick.KillSwitch"], L["Quick.AppliedNextConnect"], ToastKind.Info);
    }

    partial void OnQuickIranDirectChanged(bool value)
    {
        if (_syncQuick) return;
        _settings.Set("route.iranDirect", value ? "1" : "0");
        if (IsConnected) Toasts.Show(L["Quick.IranDirect"], L["Quick.AppliedNextConnect"], ToastKind.Info);
    }

    partial void OnQuickGameModeChanged(bool value)
    {
        if (_syncQuick) return;
        _ = ToggleGameModeAsync(value);
    }

    private async Task ToggleGameModeAsync(bool on)
    {
        try
        {
            if (on) await _gaming.ApplyAsync(GamingTweak.HighPerformancePower | GamingTweak.GameMode | GamingTweak.MultimediaNetworkThrottling, CancellationToken.None);
            else await _gaming.RevertAsync(CancellationToken.None);
            Toasts.Show(L["Quick.GameMode"], L[on ? "Quick.GameMode.On" : "Quick.GameMode.Off"], on ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex)
        {
            _syncQuick = true; QuickGameMode = !on; _syncQuick = false;
            StatusMessage = Redactor.Redact(ex.Message);
        }
    }

    partial void OnQuickSystemProxyChanged(bool value)
    {
        if (_syncQuick) return;
        ToggleSystemProxy(value);
    }

    /// <summary>System proxy via the tunnel's local health port, journaled so the Repair page can restore it (same flow as the Proxy page, v0.4.3).</summary>
    private void ToggleSystemProxy(bool on)
    {
        try
        {
            if (on)
            {
                if (_cm.Active?.Engine.HealthProxyPort is not { } port)
                {
                    _syncQuick = true; QuickSystemProxy = false; _syncQuick = false;
                    StatusMessage = L["Quick.NeedConnect"];
                    return;
                }
                var before = _proxy.Read();
                _proxy.EnableLocal(port);
                _journal.Record(JournalKind.SystemProxy, "wininet", NetworkStateJournal.Serialize(before), NetworkStateJournal.Serialize(_proxy.Read()));
                AppHost.Log(LogCategory.WindowsNetwork, "system proxy enabled on 127.0.0.1:" + port);
            }
            else
            {
                var entries = _journal.Pending().Where(e => e.Kind == JournalKind.SystemProxy).OrderBy(e => e.At).ToList();
                if (entries.Count > 0)
                {
                    var original = NetworkStateJournal.Deserialize<SystemProxyState>(entries[0].Before)!;
                    var expected = NetworkStateJournal.Deserialize<SystemProxyState>(entries[^1].Applied)!;
                    if (!_proxy.RestoreIfUnchanged(original, expected) && !Services.Dialogs.Confirm(L["Proxy.Restore"], L["Proxy.ChangedByUser"]))
                    {
                        _syncQuick = true; QuickSystemProxy = true; _syncQuick = false;
                        return;
                    }
                    _proxy.Apply(original);
                    foreach (var e in entries) _journal.Complete(e.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _syncQuick = true; QuickSystemProxy = !on; _syncQuick = false;
            StatusMessage = Redactor.Redact(ex.Message);
        }
    }

    private void StartTraffic()
    {
        _trafficCts?.Cancel();
        var cts = _trafficCts = new CancellationTokenSource();
        if (_cm.Active?.Engine.StatsApi is { } api)
        {
            var client = new ClashApiClient(AppHost.Get<HttpClient>(), api.Url, api.Secret);
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var s in client.StreamTrafficAsync(cts.Token))
                        Ui(() => ApplyTraffic(s.UpBytesPerSec, s.DownBytesPerSec, s.TotalUp, s.TotalDown));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AppHost.Log(LogCategory.SingBox, "traffic stream: " + ex.Message); }
            });
            // v0.4.12: per-app usage lives on the same Clash API — /connections carries per-process byte counters.
            _ = StartPerAppUsageAsync(api, cts.Token);
        }
        else if (_cm.Active?.Engine is OpenVpnEngine ovp)
        {
            // Official OpenVPN (v0.4.15): the management interface pushes cumulative byte counters every second.
            // No per-process data — the honest note covers the per-app card for this engine.
            ResetPerApp();
            HasAppUsage = true;
            AppUsageNote = L["Usage.NotAvailable"];
            _ovpnLastUp = _ovpnLastDown = 0;
            ovp.ByteCounts += (up, down) =>
            {
                var dUp = Math.Max(0, up - Interlocked.Exchange(ref _ovpnLastUp, up));
                var dDown = Math.Max(0, down - Interlocked.Exchange(ref _ovpnLastDown, down));
                Ui(() => ApplyTraffic(dUp, dDown, up, down));
            };
        }
        else if (_cm.Active is { } a && a.Profile.Engine == EngineKind.WireGuard)
        {
            // Per-app usage is a sing-box-only capability: WireSock captures in its driver and never exposes
            // per-process counters — the card says so honestly instead of showing an empty list.
            ResetPerApp();
            HasAppUsage = true;
            AppUsageNote = L["Usage.NotAvailable"];
            // WireSock / official-service mode (v0.4.11): traffic never touches sing-box, so the Clash stream would
            // stay frozen at zero. Layered sources — UAPI pipe → wg.exe → adapter counters (see WireGuardTraffic) —
            // with the log naming the live one. WireSock and the official service also expose no local health port,
            // so a 5-second endpoint ping feeds the latency card and chart instead of the old permanent "—".
            var engine = a.Engine as WireGuardEngine;
            var conf = a.Profile.Content;
            var noHealthPort = a.Engine.HealthProxyPort is null;
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var s in WireGuardTraffic.WatchAsync(engine, conf, (cat, msg) => AppHost.Log(cat, msg), cts.Token))
                        Ui(() => ApplyTraffic(s.UpBytesPerSec, s.DownBytesPerSec, s.TotalUp, s.TotalDown));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AppHost.Log(LogCategory.WireGuard, "traffic stats: " + ex.Message); }
            });
            if (noHealthPort)
                _ = Task.Run(async () =>
                {
                    try { await WireGuardTraffic.RunLatencyAsync(conf, ms => _cm.UpdateLatency(ms), cts.Token); }
                    catch (OperationCanceledException) { }
                    catch { /* endpoint ICMP filtered — the card honestly shows "—" */ }
                });
        }
    }

    /// <summary>Today/this-week usage from the recorded sessions (v0.4.11) — shown next to the session totals.</summary>
    private async Task LoadUsageAsync()
    {
        try
        {
            var rows = await Task.Run(() => _history.UsageSince(DateTimeOffset.Now.AddDays(-7)));
            var today = DateTimeOffset.Now.Date;
            var tD = rows.Where(r => r.Started >= today).Sum(r => r.Down);
            var tU = rows.Where(r => r.Started >= today).Sum(r => r.Up);
            UsageSummary = L.Format("Dash.UsageFmt", Fmt(tD), Fmt(tU), Fmt(rows.Sum(r => r.Down)), Fmt(rows.Sum(r => r.Up)));
        }
        catch { UsageSummary = ""; }
    }

    /* ─────────── Persistent usage card (v0.4.15): today / 3 days / week / month / all time ─────────── */

    partial void OnUsagePeriodChanged(string value) => _ = LoadUsageCardAsync();

    /// <summary>Range start for the selected period. "All" = MinValue (the SQL string comparison still holds).</summary>
    private static DateTimeOffset PeriodSince(string period) => period switch
    {
        "Days3" => DateTimeOffset.Now.AddDays(-3),
        "Week" => DateTimeOffset.Now.AddDays(-7),
        "Month" => DateTimeOffset.Now.AddDays(-31),
        "All" => DateTimeOffset.MinValue,
        _ => DateTimeOffset.Now.Date,
    };

    /// <summary>Loads the usage card for the selected period: session-record totals + persisted per-app top list.</summary>
    private async Task LoadUsageCardAsync()
    {
        try
        {
            var period = UsagePeriod;
            var since = PeriodSince(period);
            var rows = await Task.Run(() => _history.UsageSince(since));
            var up = rows.Sum(r => r.Up); var down = rows.Sum(r => r.Down);
            var perApp = await Task.Run(() => _usage.AppUsageSince(since));
            if (period != UsagePeriod) return; // the user switched the period while we were loading
            UsageTotals = L.Format("Usage.TotalFmt", Fmt(down), Fmt(up), rows.Count);
            HistoryAppUsage.Clear();
            var max = perApp.Count > 0 ? perApp.Max(r => r.Down + r.Up) : 0;
            foreach (var r in perApp)
                HistoryAppUsage.Add(new AppUsageRow(r.App)
                {
                    Total = Fmt(r.Down + r.Up),
                    Detail = "↓ " + Fmt(r.Down) + "  ·  ↑ " + Fmt(r.Up),
                    BarWidth = max > 0 ? Math.Max(0.04, (r.Down + r.Up) / (double)max) : 0,
                });
            UsageCardNote = perApp.Count == 0 ? L["Usage.NoPerApp"] : "";
        }
        catch { /* the card stays as-is — usage statistics must never break the dashboard */ }
    }

    /* ─────────── Per-app usage (v0.4.12) ─────────── */

    /// <summary>Polls Clash /connections every 2 s and accumulates byte deltas per application. Cumulative per-connection
    /// counters mean closed connections keep their counted totals — nothing is undercounted.</summary>
    private async Task StartPerAppUsageAsync((Uri Url, string Secret) api, CancellationToken ct)
    {
        ResetPerApp();
        HasAppUsage = true;
        AppUsageNote = "";
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _perAppCts?.Dispose();
        _perAppCts = linked;
        var client = new ClashApiClient(AppHost.Get<HttpClient>(), api.Url, api.Secret);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    var snap = await client.GetConnectionsAsync(linked.Token);
                    if (snap is not null) Ui(() => ApplyPerApp(snap));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* engine busy — try again next tick */ }
                await Task.Delay(TimeSpan.FromSeconds(2), linked.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyPerApp(ClashConnectionsSnapshot snap)
    {
        var seen = new HashSet<string>();
        foreach (var c in snap.Connections ?? [])
        {
            seen.Add(c.Id);
            var (lastUp, lastDown) = _lastConnBytes.TryGetValue(c.Id, out var prev) ? prev : (0L, 0L);
            var dUp = Math.Max(0, c.Upload - lastUp);
            var dDown = Math.Max(0, c.Download - lastDown);
            if (dUp == 0 && dDown == 0 && prev != (0L, 0L)) continue;
            _lastConnBytes[c.Id] = (c.Upload, c.Download);
            if (dUp == 0 && dDown == 0) continue;
            var app = AppNameOf(c);
            var (u, d) = _perAppTotals.TryGetValue(app, out var t) ? t : (0L, 0L);
            _perAppTotals[app] = (u + dUp, d + dDown);
        }
        _lastConnBytes.Keys.Where(id => !seen.Contains(id)).ToList().ForEach(id => _lastConnBytes.Remove(id));
        PersistPerApp(); // session per-app totals survive app restarts (v0.4.15)
        RenderPerApp();
    }

    /// <summary>Writes the per-session per-app DELTA into the app_usage table (only what was not persisted yet).
    /// Deltas keep this cheap: one small UPSERT per changed app per poll, on a worker thread.</summary>
    private void PersistPerApp()
    {
        foreach (var (app, (up, down)) in _perAppTotals)
        {
            var (pu, pd) = _persistedPerApp.TryGetValue(app, out var p) ? p : (0L, 0L);
            var dUp = up - pu; var dDown = down - pd;
            if (dUp <= 0 && dDown <= 0) continue;
            _persistedPerApp[app] = (up, down);
            Task.Run(() => { try { _usage.AddBytes(app, dUp, dDown); } catch { /* stats must never break traffic */ } });
        }
    }

    private static string AppNameOf(ClashConnection c)
    {
        var path = c.Metadata.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
            try { return System.IO.Path.GetFileNameWithoutExtension(path.Trim()); } catch { }
        if (!string.IsNullOrWhiteSpace(c.Metadata.Host)) return c.Metadata.Host;
        if (!string.IsNullOrWhiteSpace(c.Metadata.DestinationIP)) return c.Metadata.DestinationIP;
        return "?";
    }

    private void RenderPerApp()
    {
        var top = _perAppTotals.Where(kv => kv.Value.Up + kv.Value.Down > 0)
            .OrderByDescending(kv => kv.Value.Down + kv.Value.Up).Take(8).ToList();
        var max = top.Count > 0 ? top.Max(kv => kv.Value.Down + kv.Value.Up) : 0;

        // v0.4.24 (audit fix: «نوارهای مصرف هر برنامه … نوارها نرم بزرگ و کوچک نمی‌شوند»): rows are UPDATED
        // IN PLACE. The v0.4.23 Clear()+re-Add rebuilt the entire list every 2 s — every bar was a NEW element
        // starting from zero, so nothing ever animated. Existing rows now keep their identity and their
        // ProgressBar glides to the new width (c:Fx.AnimateProgress in DashboardView); only genuinely new
        // apps insert and gone apps remove.
        var alive = new HashSet<string>();
        for (var i = 0; i < top.Count; i++)
        {
            var (name, (up, down)) = top[i];
            alive.Add(name);
            var total = Fmt(down + up);
            var detail = "↓ " + Fmt(down) + "  ·  ↑ " + Fmt(up);
            var bar = max > 0 ? Math.Max(0.04, (down + up) / (double)max) : 0;
            if (!_perAppRows.TryGetValue(name, out var row))
            {
                row = new AppUsageRow(name) { Total = total, Detail = detail, BarWidth = bar };
                _perAppRows[name] = row;
                AppUsage.Add(row);
            }
            else
            {
                row.Total = total;
                row.Detail = detail;
                row.BarWidth = bar; // observable → the bar animates instead of restarting
            }
        }
        foreach (var gone in _perAppRows.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _perAppRows.Remove(gone);
            for (var j = AppUsage.Count - 1; j >= 0; j--)
                if (AppUsage[j].Name == gone) { AppUsage.RemoveAt(j); break; }
        }
        // enforce the rank order (≤8 rows — Move keeps each row's element alive, no rebuild flicker)
        for (var i = 0; i < top.Count; i++)
        {
            if (!_perAppRows.TryGetValue(top[i].Key, out var want)) continue;
            var at = AppUsage.IndexOf(want);
            if (at >= 0 && at != i) AppUsage.Move(at, i);
        }
    }

    /// <summary>v0.4.24 — live rows by app name, so the per-app card updates in place instead of rebuilding.</summary>
    private readonly Dictionary<string, AppUsageRow> _perAppRows = [];

    private void ResetPerApp()
    {
        _perAppCts?.Cancel();
        _lastConnBytes.Clear();
        _perAppTotals.Clear();
        _persistedPerApp.Clear();
        _perAppRows.Clear();
        AppUsage.Clear();
        HasAppUsage = false;
        AppUsageNote = "";
    }

    /// <summary>One traffic sample per second → cards, session line and the speed chart (latency moved to the uptime timer, v0.4.11).</summary>
    private void ApplyTraffic(long upBps, long downBps, long totalUp, long totalDown)
    {
        Upload = Fmt(upBps) + "/s"; Download = Fmt(downBps) + "/s";
        ThemeService.ReportTraffic(downBps); // v0.4.38 reactive ARGB + live dashboard background
        SessionTraffic = L.Format("Dash.SessionFmt", Fmt(totalDown), Fmt(totalUp));
        _sessionDown = totalDown; _sessionUp = totalUp;
        DownHistory.Add(downBps);
        UpHistory.Add(upBps);
        while (DownHistory.Count > 120) DownHistory.RemoveAt(0);
        while (UpHistory.Count > 120) UpHistory.RemoveAt(0);
    }

    public static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B", < 1024 * 1024 => $"{b / 1024.0:0.0} KB", < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.0} MB", _ => $"{b / 1073741824.0:0.00} GB",
    };
}
