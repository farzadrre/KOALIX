using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SingBox;
using Koalix.Tunneling;
using Koalix.WireGuard;

namespace Koalix.App.ViewModels;

/// <summary>List row: profile + live ping state. Since v0.4.6 it can also represent the virtual "Auto — best server" row (Profile = null).</summary>
public sealed partial class ProfileRow : ObservableObject
{
    /// <summary>Null on the virtual auto row (v0.4.6) — every consumer already null-checks or filters by IsAuto.</summary>
    private TunnelProfile? _profile;
    public TunnelProfile? Profile
    {
        get => _profile;
        set { _profile = value; OnPropertyChanged(nameof(Tag)); OnPropertyChanged(nameof(CountryCode)); OnPropertyChanged(nameof(HasCountry)); } // group chip follows profile updates (v0.4.12)
    }
    private string? _titleOverride;
    public ProfileRow(TunnelProfile? profile, string? titleOverride = null) { Profile = profile; _titleOverride = titleOverride; }
    /// <summary>True on the virtual "Auto — best server" row (v0.4.6).</summary>
    public bool IsAuto => Profile is null;
    /// <summary>v0.4.27 — NEVER throws: the dashboard rebuilds the virtual auto row (Profile = null, title not yet
    /// set by RefreshAutoTexts) and selects it in the same breath; UpdateHero then read .Name mid-rebuild and the
    /// old <c>Profile!.Name</c> crashed with NullReferenceException («این ارور میاد بعضی وقتا» — the raw-type-name
    /// error toast). A row without a title now reports "" until its localized texts arrive one frame later.</summary>
    public string Name => _titleOverride ?? Profile?.Name ?? "";
    /// <summary>Group tag chip text (v0.4.12) — empty on the auto row.</summary>
    public string? Tag => Profile?.Tag;
    public string Engine => Profile?.Engine.ToString() ?? "auto";
    /// <summary>v0.4.38 — ISO-2 country guessed from the server name (flag emoji or "DE"/"us" token); "" when unknown.</summary>
    public string CountryCode => Profile is null ? "" : ServerCountry.FromName(Profile.Name) ?? "";
    public bool HasCountry => CountryCode.Length == 2;
    public string Source => Profile is null ? "AUTO" : Profile.Source switch { ProfileSource.ShareLink => Protocol(), ProfileSource.WireGuardConf => "WireGuard", ProfileSource.OpenVpnConf => ".ovpn", ProfileSource.XrayJson => "Xray JSON", _ => "sing-box JSON" };
    public bool CanSwitchEngine => Profile?.Source == ProfileSource.ShareLink;
    /// <summary>Sub line of the auto card (v0.4.6).</summary>
    public string AutoSub { get; private set; } = "";
    /// <summary>Server name the auto row currently points at (best measured); "" when unknown (v0.4.6).</summary>
    public string AutoPick { get; private set; } = "";
    /// <summary>Virtual auto row: localized title/sub/current pick (v0.4.6).</summary>
    public void SetAutoTexts(string title, string sub, string pick)
    {
        _titleOverride = title; AutoSub = sub;
        if (AutoPick != pick) { AutoPick = pick; OnPropertyChanged(nameof(AutoPick)); }
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(AutoSub));
    }
    /// <summary>"" idle, "…" testing, "123" ms, "×" failed.</summary>
    [ObservableProperty] private string _ping = "";
    /// <summary>0 = none, 1 good (&lt;300), 2 ok (&lt;800), 3 slow, 4 failed, 5 testing.</summary>
    [ObservableProperty] private int _pingLevel;
    [ObservableProperty] private string? _pingNote;
    [ObservableProperty] private bool _isTcp;
    /// <summary>Gaming verdict badge (v0.4.0): 0 = not tested, 1 good, 2 ok, 3 bad. Text/note are localized by the view model.</summary>
    [ObservableProperty] private string _gamingText = "";
    [ObservableProperty] private int _gamingLevel;
    [ObservableProperty] private string? _gamingNote;
    /// <summary>Dashboard server-card selection highlight (v0.4.3); unused on the Tunnels page.</summary>
    [ObservableProperty] private bool _isSelected;
    /// <summary>Multi-select tick (v0.4.20) — bulk group/delete/engine-switch actions on the Tunnels page.</summary>
    [ObservableProperty] private bool _isChecked;
    /// <summary>True for the fastest measured profile — drawn as a star on the dashboard cards (v0.4.3).</summary>
    [ObservableProperty] private bool _isBest;

    public void SetGaming(GamingVerdict verdict, string text, string? note)
    {
        GamingLevel = verdict switch { GamingVerdict.Good => 1, GamingVerdict.Ok => 2, GamingVerdict.Bad => 3, _ => 0 };
        GamingText = text;
        GamingNote = note;
    }
    public void SetGamingTesting(string text) { GamingLevel = 0; GamingText = text; GamingNote = null; }

    private string Protocol()
    {
        var i = Profile.Content.IndexOf("://", StringComparison.Ordinal);
        return i > 0 ? Profile.Content[..i].ToUpperInvariant() switch { "SS" => "Shadowsocks", "HY2" or "HYSTERIA2" => "Hysteria2", var x => x } : "Link";
    }

    public void SetResult(double? ms, bool tcp, string? error)
    {
        IsTcp = tcp;
        if (ms is { } v) { Ping = Math.Round(v).ToString("0"); PingLevel = v < 300 ? 1 : v < 800 ? 2 : 3; PingNote = null; }
        else { Ping = "×"; PingLevel = 4; PingNote = error; }
    }
    public void SetTesting() { Ping = "…"; PingLevel = 5; PingNote = null; }
}

/// <summary>A saved subscription (v0.4.11): the link is stored, its servers carry the id — one button refreshes them all.</summary>
public sealed partial class SubRow : ObservableObject
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    [ObservableProperty] private string _name = "";
    /// <summary>"{count} servers • last update {time}" — localized, built by the view model.</summary>
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private bool _isUpdating;
    // ── v0.4.20: the panel's subscription-userinfo (volume + expiry) and the sub's preferred engine ──
    /// <summary>"12.3 GB / 50 GB" — empty when the panel reports nothing.</summary>
    [ObservableProperty] private string _volumeText = "";
    /// <summary>0..100 — used/total, drives the row's progress bar.</summary>
    [ObservableProperty] private double _volumePct;
    /// <summary>0 = not reported, 1 = fine, 2 = ≥80% used, 3 = exhausted.</summary>
    [ObservableProperty] private int _volumeLevel;
    /// <summary>"23 روز مانده" / the expiry date — empty when not reported.</summary>
    [ObservableProperty] private string _expireText = "";
    /// <summary>0 = not reported, 1 = fine, 2 = ≤7 days, 3 = expired.</summary>
    [ObservableProperty] private int _expireLevel;
    /// <summary>"sing-box" / "Xray" — the forced engine, empty = auto.</summary>
    [ObservableProperty] private string _engineText = "";
    public bool HasVolume => VolumeLevel > 0;
    public bool HasExpiry => ExpireLevel > 0;
    public bool IsDead => VolumeLevel == 3 || ExpireLevel == 3;
}

public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly TunnelProfileRepository _repo;
    private readonly SubscriptionRepository _subsRepo;
    private readonly SettingsRepository _settings;
    private readonly EnginePaths _paths;
    private readonly ConnectionManager _cm;
    public ObservableCollection<TunnelProfile> Items { get; } = [];
    public ObservableCollection<ProfileRow> Rows { get; } = [];
    [ObservableProperty] private ProfileRow? _selectedRow;
    [ObservableProperty] private bool _isTesting;
    private CancellationTokenSource? _testCts;
    public IReadOnlyList<string> Filters { get; } = ["All", nameof(EngineKind.SingBox), nameof(EngineKind.Xray), nameof(EngineKind.WireGuard)];
    [ObservableProperty] private string _filter = "All";
    /// <summary>Group/tag filter (v0.4.12): "All" + every distinct tag in use.</summary>
    public ObservableCollection<string> TagFilters { get; } = ["All"];
    [ObservableProperty] private string _tagFilter = "All";
    [ObservableProperty] private TunnelProfile? _selected;
    [ObservableProperty] private string _importText = "";
    [ObservableProperty] private string _importName = "";
    /// <summary>Optional group label applied to everything imported in this batch (v0.4.12) — merges several
    /// subscriptions under one tag, e.g. "work", "gaming".</summary>
    [ObservableProperty] private string _importTag = "";
    [ObservableProperty] private string _preview = "";
    /// <summary>Saved subscriptions (v0.4.11) — each refreshable/removable with one click.</summary>
    public ObservableCollection<SubRow> Subs { get; } = [];

    // ── v0.4.20: engine choice on import, multi-select, auto-clean ──
    /// <summary>Import-time engine choice (v0.4.20): 0 = auto per link, 1 = force sing-box, 2 = force Xray.
    /// Stored on the subscription too, so future refreshes keep the chosen engine — the fix for
    /// «لینک ساب زدم و بازم سینگ باکس تشخیصشون میده» (vless+ws+tls IS sing-box native, so auto always picked it).</summary>
    public IReadOnlyList<string> ImportEngineChoices { get; } = ["Auto", "SingBox", "Xray"];
    [ObservableProperty] private int _importEngine;
    /// <summary>Delete profiles whose latency test FAILED (v0.4.20 — big subscriptions stay clean automatically).</summary>
    [ObservableProperty] private bool _autoClean;
    /// <summary>How many rows are ticked (v0.4.20 multi-select).</summary>
    [ObservableProperty] private int _selectedCount;
    /// <summary>True when every visible row is ticked — drives the select-all toggle text.</summary>
    public bool AllSelected => SelectedCount > 0 && Rows.Count > 0 && Rows.All(r => r.IsChecked);
    public bool HasSelection => SelectedCount > 0;

    public ProfilesViewModel(TunnelProfileRepository repo, SubscriptionRepository subs, SettingsRepository settings, EnginePaths paths, ConnectionManager cm)
    {
        _repo = repo; _subsRepo = subs; _settings = settings; _paths = paths; _cm = cm;
        AutoClean = _settings.Get("tunnels.autoClean") == "1";
    }

    public override void OnActivated() { Reload(); LoadSubs(); }
    partial void OnFilterChanged(string value) => Reload();
    /// <summary>Guards Reload against WPF feedback loops: clearing/rebuilding the TagFilters ItemsSource makes the
    /// group ComboBox push null (or a stale value) back into TagFilter through its TwoWay SelectedItem binding —
    /// which used to call Reload again inside Reload and froze the app (v0.4.16).</summary>
    private bool _reloading;
    private bool _suppressTagEvents;
    private string? _lastTagSig;
    partial void OnTagFilterChanged(string value)
    {
        if (_reloading || _suppressTagEvents) return;
        if (value is null) { _suppressTagEvents = true; TagFilter = "All"; _suppressTagEvents = false; return; }
        Reload();
    }
    /// <summary>A hand-pasted text has no file folder — drop any stale import folder so OpenVPN file-inlining only
    /// ever reads siblings of the .ovpn the user actually picked (v0.4.15).</summary>
    partial void OnImportTextChanged(string value) => _importFileDir = null;
    partial void OnSelectedChanged(TunnelProfile? value) =>
        Preview = value is null ? "" : value.Source switch
        {
            ProfileSource.WireGuardConf => WireGuardConfig.Parse(value.Content, out _).ToRedactedString(),
            ProfileSource.OpenVpnConf => OpenVpnConfig.ToRedacted(value.Content),
            _ => Redactor.Redact(value.Content),
        };

    partial void OnSelectedRowChanged(ProfileRow? value) => Selected = value?.Profile;

    partial void OnAutoCleanChanged(bool value) => _settings.Set("tunnels.autoClean", value ? "1" : "0");

    /// <summary>Recounts the ticked rows (v0.4.20) — every row checkbox routes its change here.</summary>
    public void RowCheckedChanged()
    {
        var n = Rows.Count(r => r.IsChecked);
        if (n != SelectedCount) { SelectedCount = n; OnPropertyChanged(nameof(AllSelected)); OnPropertyChanged(nameof(HasSelection)); }
    }

    /// <summary>Command side of the row checkbox (v0.4.20) — the tick already bound, this just refreshes the counter.</summary>
    [RelayCommand] private void RowTicked() => RowCheckedChanged();

    private void Reload()
    {
        if (_reloading) return; // v0.4.16 — a ComboBox pushing into TagFilter while we rebuild it used to recurse forever (UI freeze)
        _reloading = true;
        try
        {
            var keep = SelectedRow?.Profile.Id;
            var all = _repo.All();
            Items.Clear(); Rows.Clear();
            foreach (var p in all.Where(p => Filter == "All" || p.Engine.ToString() == Filter)
                         .Where(p => TagFilter == "All" || (p.Tag is { } t && string.Equals(t, TagFilter, StringComparison.OrdinalIgnoreCase))))
            {
                Items.Add(p);
                var row = new ProfileRow(p);
                if (p.LastLatencyMs is { } ms) row.SetResult(ms, false, null);
                Rows.Add(row);
            }
            SelectedRow = Rows.FirstOrDefault(r => r.Profile.Id == keep);
            var tags = all.Select(p => p.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.CurrentCulture).ToList();
            var keepTag = TagFilter;
            // v0.4.16 — rebuild the group ComboBox items ONLY when the tag set really changed: clearing the
            // ItemsSource of a bound ComboBox wipes its selection and pushes null back into TagFilter, which
            // re-entered Reload (infinite loop → app hang). A signature check keeps the common path push-free.
            var sig = string.Join("\u0001", tags);
            if (sig != _lastTagSig)
            {
                _lastTagSig = sig;
                TagFilters.Clear(); TagFilters.Add("All");
                foreach (var t in tags) TagFilters.Add(t);
                // Re-push the current source value into the ComboBox — the rebuild wiped its visual selection
                // and, since the source itself did not change, no automatic target update would happen.
                OnPropertyChanged(nameof(TagFilter));
            }
            if (keepTag is null || !TagFilters.Contains(keepTag))
            {
                _suppressTagEvents = true;
                TagFilter = "All";
                _suppressTagEvents = false;
            }
            if (_repo.Unreadable > 0) StatusMessage = L.Format("Profiles.Unreadable", _repo.Unreadable);
        }
        finally { _reloading = false; }
    }

    /// <summary>Persists the manual drag-and-drop order of the current (filtered) list (v0.4.19). Profiles
    /// hidden by the active filters keep their stored position; the visible order becomes 10, 20, 30 …</summary>
    public void PersistOrder()
    {
        try { _repo.Reorder(Rows.Where(r => !r.IsAuto).Select(r => r.Profile!.Id)); }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
    }

    /// <summary>Real-delay test (HTTP 204 through each config via a temporary sing-box), TCP ping as fallback.</summary>
    [RelayCommand] private Task TestAllAsync() => RunTestsAsync(Rows.ToList());
    [RelayCommand] private Task TestSelectedAsync() => SelectedRow is null ? Task.CompletedTask : RunTestsAsync([SelectedRow]);

    private async Task RunTestsAsync(List<ProfileRow> rows)
    {
        if (IsTesting) { _testCts?.Cancel(); return; }
        if (rows.Count == 0) return;
        IsTesting = true;
        _testCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var r in rows) r.SetTesting();
        var byId = rows.ToDictionary(r => r.Profile.Id);
        var progress = new Progress<LatencyResult>(res =>
        {
            if (!byId.TryGetValue(res.ProfileId, out var row)) return;
            row.SetResult(res.Ms, res.Method == LatencyMethod.TcpPing, res.Error);
            try { _repo.SetLatency(res.ProfileId, res.Ms); } catch { }
        });
        try
        {
            var results = await Task.Run(() => new ProfileLatencyTester(_paths).TestAsync(rows.Select(r => r.Profile).ToList(), progress, _testCts.Token));
            var ok = results.Count(r => r.Ms is not null);
            StatusMessage = L.Format("Profiles.TestReport", ok, results.Count);
            if (!File.Exists(_paths.SingBoxExe)) StatusMessage += "\n" + L["Profiles.TestTcpOnly"];
            // v0.4.20 — auto-clean: profiles whose test FAILED are removed right away (big subs stay tidy).
            if (AutoClean) CleanDead(results, auto: true);
        }
        catch (OperationCanceledException) { StatusMessage = L["Common.Cancelled"]; foreach (var r in rows.Where(r => r.PingLevel == 5)) r.SetResult(null, false, null); }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
        finally { IsTesting = false; _testCts?.Dispose(); _testCts = null; }
    }

    /// <summary>Deletes the profiles that failed their latency test (v0.4.20). Never touches the connected one.</summary>
    private void CleanDead(IReadOnlyList<LatencyResult> results, bool auto)
    {
        var activeId = _cm.Active?.Profile.Id;
        var dead = results.Where(r => r.Ms is null && r.ProfileId != activeId).Select(r => r.ProfileId).ToList();
        if (dead.Count == 0) return;
        if (!auto && !Dialogs.Confirm(L["Profiles.RemoveDead"], L.Format("Profiles.RemoveDeadConfirm", dead.Count))) return;
        _repo.DeleteMany(dead);
        StatusMessage = L.Format("Profiles.RemoveDeadDone", dead.Count) + (auto ? " (" + L["Profiles.AutoClean"] + ")" : "");
        Reload();
    }

    /// <summary>Manual "remove everything that failed the last test" (v0.4.20) — acts on the visible rows.</summary>
    [RelayCommand] private void RemoveDead()
    {
        var activeId = _cm.Active?.Profile.Id;
        var dead = Rows.Where(r => r.PingLevel == 4 && r.Profile.Id != activeId).Select(r => r.Profile.Id).ToList();
        if (dead.Count == 0) { StatusMessage = L["Profiles.NoDead"]; return; }
        if (!Dialogs.Confirm(L["Profiles.RemoveDead"], L.Format("Profiles.RemoveDeadConfirm", dead.Count))) return;
        _repo.DeleteMany(dead);
        StatusMessage = L.Format("Profiles.RemoveDeadDone", dead.Count);
        Reload();
    }

    /// <summary>Connect/disconnect the selected server right here on the Tunnels page (v0.4.0) — same flow as the dashboard orb.</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedRow?.Profile is not { } p) return;
        await AppHost.Get<DashboardViewModel>().ConnectToAsync(p);
        StatusMessage = _cm.Active?.Profile.Id == p.Id ? L.Format("Profiles.ConnectedTo", p.Name) : L["Profiles.Disconnected"];
    }

    [ObservableProperty] private bool _isGamingTesting;
    private CancellationTokenSource? _gamingCts;

    /// <summary>Gaming test for the selected config: static analysis (UDP? Cloudflare worker?) + live jitter/UDP probes.</summary>
    [RelayCommand] private Task TestGamingSelectedAsync() => SelectedRow is null ? Task.CompletedTask : RunGamingTestsAsync([SelectedRow]);
    [RelayCommand] private Task TestGamingAllAsync() => RunGamingTestsAsync(Rows.ToList());

    private async Task RunGamingTestsAsync(List<ProfileRow> rows)
    {
        if (IsGamingTesting) { _gamingCts?.Cancel(); return; }
        if (rows.Count == 0) return;
        IsGamingTesting = true;
        _gamingCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        foreach (var r in rows) r.SetGamingTesting("…");
        var byId = rows.ToDictionary(r => r.Profile.Id);
        var progress = new Progress<GamingResult>(res =>
        {
            if (!byId.TryGetValue(res.ProfileId, out var row)) return;
            var metrics = new List<string>();
            if (res.MedianMs is { } m) metrics.Add($"{Math.Round(m)} ms");
            if (res.JitterMs is { } j) metrics.Add("jitter " + Math.Round(j) + " ms");
            if (res.UdpOk is { } u) metrics.Add("UDP: " + (u ? "✓" : "✗"));
            var note = string.Join("؛ ", res.ReasonCodes.Select(c => L[c]).Concat(metrics));
            row.SetGaming(res.Verdict, L["Gaming.Verdict." + res.Verdict], note);
        });
        try
        {
            var results = await Task.Run(() => new ProfileLatencyTester(_paths).GamingTestAsync(rows.Select(r => r.Profile).ToList(), progress, _gamingCts.Token));
            var good = results.Count(r => r.Verdict == GamingVerdict.Good);
            StatusMessage = L.Format("Profiles.GamingReport", good, results.Count);
            if (!File.Exists(_paths.SingBoxExe)) StatusMessage += "\n" + L["Profiles.TestTcpOnly"];
        }
        catch (OperationCanceledException) { StatusMessage = L["Common.Cancelled"]; }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
        finally { IsGamingTesting = false; _gamingCts?.Dispose(); _gamingCts = null; }
    }

    /// <summary>Share links can run on either engine; some servers behave better on Xray.</summary>
    [RelayCommand] private void SwitchEngine()
    {
        if (SelectedRow is not { CanSwitchEngine: true } row) return;
        if (_cm.Active?.Profile.Id == row.Profile.Id) { StatusMessage = L["Profiles.InUse"]; return; }
        var target = row.Profile.Engine == EngineKind.Xray ? EngineKind.SingBox : EngineKind.Xray;
        if (target == EngineKind.SingBox && !SingBoxConfigBuilder.IsNativelySupported(ShareLinkParser.Parse(row.Profile.Content), out var why))
        { StatusMessage = why ?? ""; return; }
        _repo.SetEngine(row.Profile.Id, target);
        StatusMessage = L.Format("Profiles.EngineSwitched", row.Name, target);
        Reload();
    }

    /// <summary>Detects the input type: subscription URL(s — one per line, v0.4.12), share links, sing-box JSON,
    /// Xray JSON or WireGuard .conf. The optional name names a single subscription; the optional group tag
    /// labels everything imported in this batch (merging several subs under one group); the engine choice
    /// (v0.4.20) forces every imported share-link node onto sing-box or Xray instead of the per-link auto-detect.</summary>
    [RelayCommand] private async Task ImportAsync() => await ImportTextCoreAsync(ImportText, ImportName, ImportTag, true);

    /// <summary>Import pipeline shared by the button, the file picker and dropping links onto the window (v0.4.20).</summary>
    private async Task ImportTextCoreAsync(string text, string name, string tag, bool clearFields)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var names = name.Trim();
        tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        IsBusy = true;
        try
        {
            var added = 0; var errors = new List<string>();
            var urlLines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
            if (urlLines.Count > 0)
            {
                // v0.4.12 — one or many subscription links: every link is saved with its own name and its servers
                // get the batch tag. Merging multiple subscriptions = paste several links + one group name.
                var single = urlLines.Count == 1;
                var preferred = ImportEngine switch { 1 => EngineKind.SingBox, 2 => EngineKind.Xray, _ => (EngineKind?)null };
                for (var i = 0; i < urlLines.Count; i++)
                {
                    var url = urlLines[i];
                    if (!SubscriptionClient.IsValidSubscriptionUrl(url, out var err)) { errors.Add(L[err ?? "Profiles.Error.Url"]); continue; }
                    var (r, error) = await FetchSubscriptionAsync(url);
                    if (r is null) { errors.Add(L["Sub.Error.Blocked"] + " " + url + "\n" + (error ?? "")); continue; }
                    // New subs: the custom name (single link) or the host name (multi-link batch).
                    // Existing subs keep their name unless a custom name was typed for a single link.
                    var subName = single && names.Length > 0 ? names : new Uri(url).Host;
                    var existing = _subsRepo.FindByUrl(url);
                    var subId = existing ?? _subsRepo.Add(subName, url, preferred);
                    if (existing is not null && single && names.Length > 0) _subsRepo.Rename(subId, subName);
                    if (preferred is not null) _subsRepo.SetEngine(subId, preferred); // v0.4.20: the choice sticks for future refreshes
                    // v0.4.16: -1 = the feed gave no servers (or the sub vanished) — old servers kept, honest error
                    // v0.4.23: the combo choice rides along — an explicit SingBox here is honoured (the
                    // SingBox→Xray migration inside ReplaceSubscription only catches the old auto-detect leftovers).
                    var n = ReplaceSubscription(subId, r, tag, preferred);
                    if (n >= 0) added += n; else errors.Add(L["Sub.Error.EmptyFeed"]);
                    errors.AddRange(r.Links.Errors);
                }
            }
            else if (text.StartsWith('{'))
            {
                var json = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("JSON");
                var isXray = json["outbounds"] is JsonArray outs && outs.OfType<JsonObject>().Any(o => o["protocol"] is not null);
                var pname = string.IsNullOrWhiteSpace(names) ? (isXray ? "Xray config" : "sing-box config") : names;
                _repo.Add(new TunnelProfile { Name = pname, Engine = isXray ? EngineKind.Xray : EngineKind.SingBox, Source = isXray ? ProfileSource.XrayJson : ProfileSource.SingBoxJson, Content = text, Tag = tag });
                added++;
            }
            else if (text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = WireGuardConfig.Parse(text, out var wgErrors);
                if (wgErrors.Count > 0) { StatusMessage = string.Join("\n", wgErrors.Select(e => L[e])); return; }
                _repo.Add(new TunnelProfile { Name = string.IsNullOrWhiteSpace(names) ? "WireGuard" : names, Engine = EngineKind.WireGuard, Source = ProfileSource.WireGuardConf, Content = text, Tag = tag });
                added++;
            }
            else if (OpenVpnConfig.LooksLikeOpenVpn(text))
            {
                // OpenVPN (v0.4.15, detection hardened in v0.4.16): any .ovpn-shaped text is handled HERE, so a config
                // with reportable problems (missing remote, external file refs, credentials…) gets the honest
                // OpenVPN error instead of a confusing "unsupported link scheme" from the share-link fallback.
                // External files are already inlined; auth-user-pass without credentials is completed with ONE
                // dialog (stored encrypted).
                var content = OpenVpnConfig.InlineExternalFiles(text, _importFileDir ?? ".");
                var ovpn = OpenVpnConfig.Parse(content, out var ovpnErrors);
                if (ovpnErrors.Count > 0) { StatusMessage = string.Join("\n", ovpnErrors.Select(e => L[e])); return; }
                if (ovpn.HasAuthUserPass && !ovpn.HasInlineCredentials)
                {
                    var user = Dialogs.Prompt(L["Ovpn.Creds.Title"], L["Ovpn.Creds.User"]);
                    if (string.IsNullOrWhiteSpace(user)) return;
                    var pass = Dialogs.Prompt(L["Ovpn.Creds.Title"], L["Ovpn.Creds.Pass"], password: true);
                    if (pass is null) return;
                    content = InjectAuthUserPass(content, user, pass);
                }
                var pname = string.IsNullOrWhiteSpace(names) ? "OpenVPN · " + ovpn.DisplayEndpoint : names;
                _repo.Add(new TunnelProfile { Name = pname, Engine = EngineKind.OpenVpn, Source = ProfileSource.OpenVpnConf, Content = content, Tag = tag });
                added++;
            }
            else
            {
                var r = ShareLinkParser.ParseMany(text);
                added += AddNodes(r.Nodes, tag: tag, preferred: ImportEngine switch { 1 => EngineKind.SingBox, 2 => EngineKind.Xray, _ => null }); errors.AddRange(r.Errors);
                // v0.4.22: the panel's ad/volume placeholder rows were skipped — say so, honestly and quietly.
                if (r.Notices > 0) errors.Add(L.Format("Profiles.NoticesSkipped", r.Notices));
            }
            if (clearFields) { ImportText = ""; ImportName = ""; ImportTag = ""; }
            StatusMessage = L.Format("Profiles.ImportReport", added, errors.Count) + (errors.Count > 0 ? "\n" + string.Join("\n", errors.Take(10).Select(e => Redactor.Redact(e))) : "");
            Reload(); LoadSubs();
        }
        catch (Exception ex) { StatusMessage = L["Profiles.Error.Import"] + ": " + Redactor.Redact(ex.Message); }
        finally { IsBusy = false; }
    }

    /// <summary>Dropped text/files on the main window (v0.4.20): share links, subscription URLs or a whole config —
    /// the same pipeline as the Import button. Returns how many configs were added (for the toast).</summary>
    public async Task<int> ImportDroppedAsync(string text)
    {
        var before = _repo.All().Count;
        await ImportTextCoreAsync(text, "", "", false);
        return _repo.All().Count - before;
    }

    private int AddNodes(IEnumerable<ProxyNode> nodes, long? subId = null, string? tag = null, EngineKind? preferred = null)
    {
        var n = 0;
        foreach (var node in nodes)
        {
            if (node.Validate().Count > 0) continue;
            // v0.4.23 (user: «میخوام لینک ساب و ویتوری که اضافه میکنیم همون اول خودش کلا موتورشو xray تشخیص بده،
            // فقط کانفیگای جیسون و سینگ باکس تشخیص بده»): share links land on XRAY by default — the old
            // "sing-box when natively supported" auto-detect kept flipping vless/vmess/ws onto sing-box and that
            // core was exactly the one that refused the ZEUS nodes. JSON configs (sing-box / Xray) keep their own
            // detection in the import pipeline above; WireGuard/OpenVPN carry their engine in the format itself.
            // A per-subscription or per-import choice (ImportEngine) still wins when set.
            var engine = preferred ?? EngineKind.Xray;
            _repo.Add(new TunnelProfile { Name = node.Name, Engine = engine, Source = ProfileSource.ShareLink, Content = node.RawLink ?? "", SubscriptionId = subId, Tag = tag });
            n++;
        }
        return n;
    }

    /// <summary>Turns a bare "auth-user-pass" directive into the inline two-line credential form (v0.4.15).</summary>
    private static string InjectAuthUserPass(string content, string user, string password)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var idx = lines.FindIndex(l => l.Trim().Equals("auth-user-pass", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = lines.FindIndex(l => l.Trim().StartsWith("auth-user-pass ", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return content;
        lines[idx] = "auth-user-pass";
        lines.InsertRange(idx + 1, [user.Trim(), password]);
        return string.Join("\n", lines);
    }

    /* ─────────── Saved subscriptions (v0.4.11) ─────────── */

    /// <summary>Subscription download with the v0.4.10 DPI workaround: through the active tunnel when it exposes a local
    /// proxy (panel hosts like *.pages.dev are TLS-reset by DPI on Iranian ISPs), direct otherwise with the system proxy
    /// ignored so a stale WinINet entry can never break the import. Null Result = failed (Error redacted).</summary>
    private async Task<(SubFetchResult? Result, string? Error)> FetchSubscriptionAsync(string url)
    {
        if (_cm.Active?.Engine.HealthProxyPort is { } p)
            try { return (await SubscriptionClient.FetchAsync(url, p, CancellationToken.None), null); }
            catch { }
        try { return (await SubscriptionClient.FetchAsync(url, null, CancellationToken.None), null); }
        catch (Exception ex) { return (null, Redactor.Redact(ex.Message)); }
    }

    /// <summary>
    /// Replaces exactly this subscription's servers (auto-removal of its previous nodes) and stamps the update time.
    /// v0.4.16 hardening: a fetch that parses ZERO nodes (panel hiccup, Cloudflare challenge page, feed format change)
    /// no longer deletes the existing servers — that silent wipe was why server lists could empty themselves between
    /// sessions (-1 return). And when the subscription was deleted while the download was running, nothing is
    /// re-added (that race resurrected deleted configs under an orphaned subscription id).
    /// v0.4.20: the panel's subscription-userinfo (volume/expiry) is stored on the sub, and the sub's saved
    /// engine choice is reused so a refresh never silently flips servers back to auto-detect.
    /// </summary>
    private int ReplaceSubscription(long subId, SubFetchResult r, string? tag = null, EngineKind? preferredEngine = null)
    {
        if (!_subsRepo.Exists(subId)) return -1; // deleted while we were downloading — never resurrect its servers
        if (r.Links.Nodes.Count == 0) return -1;  // empty/failed feed — keep the old servers instead of wiping them
        // v0.4.40 (user: «گروهی که میسازم … حذف میشه ولی کانفیگاش هنوز هست تو قسمت همه») — a refresh deletes and
        // re-adds the sub's servers, and the re-added rows came back WITHOUT the group the user had put them in
        // (auto-refresh on app open does this silently). Remember each old server's group by its link/name.
        var oldNodes = _repo.BySubscription(subId);
        var tagByLink = new Dictionary<string, string>(StringComparer.Ordinal);
        var tagByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in oldNodes.Where(o => !string.IsNullOrWhiteSpace(o.Tag)))
        {
            tagByLink.TryAdd(o.Content, o.Tag!);
            tagByName.TryAdd(o.Name, o.Tag!);
        }
        var oldTags = oldNodes.Select(o => o.Tag?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wholeSubTag = oldTags.Count == 1 && !string.IsNullOrWhiteSpace(oldTags[0]) ? oldTags[0] : null; // whole sub was one group
        foreach (var old in oldNodes) _repo.Delete(old.Id);
        var engine = preferredEngine ?? _subsRepo.All().FirstOrDefault(s => s.Id == subId)?.Engine; // saved choice sticks
        // v0.4.23 migration: subscriptions saved under the OLD auto-detect carry SingBox for plain share links;
        // per the new rule (links → Xray, only JSON → sing-box) one refresh moves them over, so existing subs get
        // the same treatment as fresh imports without the user touching anything.
        if (engine is null or EngineKind.SingBox)
        {
            engine = EngineKind.Xray;
            _subsRepo.SetEngine(subId, EngineKind.Xray);
        }
        var n = AddNodes(r.Links.Nodes, subId, tag, engine);
        if (string.IsNullOrWhiteSpace(tag) && (tagByLink.Count > 0 || wholeSubTag is not null))
            foreach (var p in _repo.BySubscription(subId))
            {
                var keep = tagByLink.GetValueOrDefault(p.Content) ?? tagByName.GetValueOrDefault(p.Name) ?? wholeSubTag;
                if (keep is not null) _repo.SetTag(p.Id, keep);
            }
        if (r.Info is { } info) _subsRepo.SetInfo(subId, info);
        _subsRepo.SetLastUpdate(subId);
        // v0.4.22: skipped placeholder rows (0.0.0.0:1 ads/volume reminders) are reported, not silently dropped.
        if (r.Links.Notices > 0) StatusMessage = L.Format("Profiles.NoticesSkipped", r.Links.Notices);
        return n;
    }

    private void LoadSubs()
    {
        Subs.Clear();
        foreach (var s in _subsRepo.All())
        {
            var row = new SubRow
            {
                Id = s.Id, Url = s.Url, Name = s.Name,
                Meta = L.Format("Sub.MetaFmt", s.ProfileCount, s.LastUpdate is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—"),
                EngineText = s.Engine is { } e ? e.ToString() : "",
            };
            // v0.4.20 — the panel-reported volume bar + expiry chip
            if (s.TotalBytes is > 0 or null && (s.TotalBytes is not null || s.UploadBytes is not null || s.DownloadBytes is not null))
            {
                var used = (s.UploadBytes ?? 0) + (s.DownloadBytes ?? 0);
                if (s.TotalBytes is { } total && total > 0)
                {
                    var pct = Math.Clamp(used * 100.0 / total, 0, 100);
                    row.VolumeText = DashboardViewModel.Fmt(used) + " / " + DashboardViewModel.Fmt(total);
                    row.VolumePct = pct;
                    row.VolumeLevel = used >= total ? 3 : pct >= 80 ? 2 : 1;
                }
                else if (used > 0)
                {
                    // panel reports usage but no cap — show what was consumed
                    row.VolumeText = DashboardViewModel.Fmt(used);
                    row.VolumePct = 100;
                    row.VolumeLevel = 1;
                }
            }
            if (s.ExpireUnix is { } exp)
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime();
                var left = at - DateTimeOffset.Now;
                if (left <= TimeSpan.Zero) { row.ExpireText = L["Sub.Expired"]; row.ExpireLevel = 3; }
                else if (left <= TimeSpan.FromDays(7)) { row.ExpireText = L.Format("Sub.DaysLeft", Math.Max(1, (int)Math.Ceiling(left.TotalDays))); row.ExpireLevel = 2; }
                else { row.ExpireText = L.Format("Sub.ExpireOn", at.ToString("yyyy-MM-dd")); row.ExpireLevel = 1; }
            }
            Subs.Add(row);
        }
    }

    /// <summary>One-click refresh: re-downloads the sub (through the tunnel when connected) and replaces its servers.</summary>
    [RelayCommand]
    private async Task UpdateSubAsync(SubRow? row)
    {
        if (row is null || row.IsUpdating) return;
        if (_cm.Active?.Profile.SubscriptionId == row.Id) { StatusMessage = L["Sub.InUse"]; return; }
        row.IsUpdating = true;
        try
        {
            var (r, error) = await FetchSubscriptionAsync(row.Url);
            if (r is null) { StatusMessage = L["Sub.Error.Blocked"] + "\n" + (error ?? ""); return; }
            var n = ReplaceSubscription(row.Id, r);
            if (n < 0)
            {
                Reload(); LoadSubs(); // the row may be gone (deleted while downloading) — refresh the section
                if (_subsRepo.Exists(row.Id)) { StatusMessage = L["Sub.Error.EmptyFeed"]; Toasts.Show(L["Sub.UpdatedTitle"], L["Sub.Error.EmptyFeed"], ToastKind.Warning); }
                return;
            }
            StatusMessage = L.Format("Sub.Updated", row.Name, n);
            Toasts.Show(L["Sub.UpdatedTitle"], L.Format("Sub.Updated", row.Name, n), ToastKind.Success);
            Reload(); LoadSubs();
        }
        finally { row.IsUpdating = false; }
    }

    /// <summary>Removes the subscription together with its servers (refused while one of them is connected).</summary>
    [RelayCommand]
    private void RemoveSub(SubRow? row)
    {
        if (row is null) return;
        if (_cm.Active?.Profile.SubscriptionId == row.Id) { StatusMessage = L["Sub.InUse"]; return; }
        if (!Dialogs.Confirm(L["Sub.Remove"], L.Format("Sub.ConfirmRemove", row.Name))) return;
        _subsRepo.Delete(row.Id);
        Reload(); LoadSubs();
    }

    /// <summary>Renames a saved subscription in place (v0.4.12 — user-chosen names for profiles).</summary>
    [RelayCommand]
    private void RenameSub(SubRow? row)
    {
        if (row is null) return;
        var name = Dialogs.Prompt(L["Sub.Rename"], L["Sub.NamePrompt"], row.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == row.Name) return;
        _subsRepo.Rename(row.Id, name.Trim());
        LoadSubs();
        StatusMessage = L.Format("Sub.Renamed", name.Trim());
    }

    /// <summary>Group tag of the selected profile (v0.4.12): set via prompt, empty input clears it.</summary>
    [RelayCommand]
    private void SetTag()
    {
        if (SelectedRow?.Profile is not { } p) return;
        var tag = Dialogs.Prompt(L["Profiles.SetTag"], L["Profiles.SetTagPrompt"], p.Tag ?? "");
        if (tag is null) return; // cancelled
        _repo.SetTag(p.Id, tag);
        StatusMessage = string.IsNullOrWhiteSpace(tag) ? L.Format("Profiles.TagCleared", p.Name) : L.Format("Profiles.TagSet", p.Name, tag.Trim());
        Reload();
    }

    /// <summary>Moves every server of a subscription into a group (v0.4.12 — bulk tagging).</summary>
    [RelayCommand]
    private void TagSubServers(SubRow? row)
    {
        if (row is null) return;
        var ids = _repo.BySubscription(row.Id).Select(p => p.Id).ToList();
        if (ids.Count == 0) { StatusMessage = L["Tray.NoServers"]; return; }
        var tag = Dialogs.Prompt(L["Sub.TagServers"], L.Format("Sub.TagServersPrompt", row.Name, ids.Count));
        if (tag is null) return;
        _repo.SetTagMany(ids, tag);
        StatusMessage = string.IsNullOrWhiteSpace(tag) ? L.Format("Profiles.TagCleared", row.Name) : L.Format("Sub.TagServersDone", ids.Count, tag.Trim());
        Reload();
    }

    /// <summary>App-open refresh (v0.4.11, setting "subs.auto", default on): subscriptions older than 12 h are
    /// updated in the background so dead panels never leave a stale server list.</summary>
    public async Task AutoRefreshAsync()
    {
        try
        {
            if (_settings.Get("subs.auto") == "0") return;
            var stale = _subsRepo.All().Where(s => s.LastUpdate is null || DateTimeOffset.Now - s.LastUpdate > TimeSpan.FromHours(12)).ToList();
            if (stale.Count == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(12)); // let the UI and any tunnel settle first
            var updated = 0;
            foreach (var s in stale)
            {
                if (_cm.Active?.Profile.SubscriptionId == s.Id) continue; // never swap servers under a live connection
                var (r, _) = await FetchSubscriptionAsync(s.Url);
                if (r is null) continue; // blocked — the manual button shows the full story
                if (ReplaceSubscription(s.Id, r) > 0) updated++; // v0.4.16: empty feeds and deleted subs change nothing
            }
            if (updated > 0)
            {
                Toasts.Show(L["Sub.UpdatedTitle"], L.Format("Sub.AutoUpdated", updated), ToastKind.Success);
                Ui(() => { Reload(); LoadSubs(); });
            }
        }
        catch (Exception ex) { AppHost.Log(LogCategory.Application, "sub auto-refresh: " + ex.Message); }
    }

    /* ─────────── Share a config as QR + link (v0.4.23, suggestion #5: «خروجی QR/لینک برای فرستادن به گوشی») ─────────── */

    /// <summary>True when the share popup is on screen.</summary>
    [ObservableProperty] private bool _shareOpen;
    /// <summary>What the QR encodes: the raw share link for link-type configs, the sub URL for subscriptions.</summary>
    [ObservableProperty] private string _shareText = "";
    /// <summary>False when the shared payload is a full config TEXT (JSON/.ovpn/.conf) — QR of 4 KB would be unreadable;
    /// the dialog then offers copying the text instead of a scannable square.</summary>
    [ObservableProperty] private bool _shareIsLink;
    [ObservableProperty] private string _shareTitle = "";
    /// <summary>Rendered QR as PNG bytes (white on transparent is bad on dark glass — black on white card scans best).</summary>
    [ObservableProperty] private System.Windows.Media.ImageSource? _shareQrImage;
    public bool HasQr => ShareQrImage is not null;
    partial void OnShareQrImageChanged(System.Windows.Media.ImageSource? value) => OnPropertyChanged(nameof(HasQr));
    /// <summary>The copy button's label follows the payload: a link for phones vs the full config text.</summary>
    public string ShareIsLinkText => ShareIsLink ? L["Share.Copy"] : L["Share.CopyText"];
    partial void OnShareIsLinkChanged(bool value) => OnPropertyChanged(nameof(ShareIsLinkText));

    /// <summary>Opens the share dialog for a server row (toolbar uses the selected row) or a subscription card.</summary>
    [RelayCommand]
    private void Share(object? item)
    {
        switch (item)
        {
            case SubRow sub:
                ShareTitle = sub.Name;
                OpenShare(sub.Url, isLink: true);
                return;
            case ProfileRow { Profile: { } p }:
                ShareTitle = p.Name;
                var isLink = p.Source == ProfileSource.ShareLink;
                OpenShare(p.Content, isLink);
                return;
            default:
                StatusMessage = L["Profiles.SharePick"];
                return;
        }
    }

    private void OpenShare(string text, bool isLink)
    {
        ShareText = text;
        ShareIsLink = isLink;
        ShareQrImage = null;
        ShareOpen = true;
        if (!isLink || text.Length > 800) return; // no QR for full JSON/WG/OVPN blobs or very long links
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule: 7);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            using var ms = new MemoryStream(png);
            img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit();
            img.Freeze();
            ShareQrImage = img;
        }
        catch (Exception ex) { StartupLog.Trace("qr: " + ex.Message); StatusMessage = L["Share.QrFail"]; }
    }

    [RelayCommand] private void CloseShare() => ShareOpen = false;

    [RelayCommand]
    private void CopyShareText()
    {
        try { System.Windows.Clipboard.SetText(ShareText); StatusMessage = L["Share.Copied"]; Toasts.Show(L["Share.Title"], L["Share.Copied"], ToastKind.Success); }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
    }

    [RelayCommand]
    private void SaveShareQr()
    {
        if (ShareQrImage is not System.Windows.Media.Imaging.BitmapSource bmp) return;
        var file = Dialogs.SaveFile("PNG (*.png)|*.png", $"koalix-qr-{DateTime.Now:yyyyMMdd-HHmm}.png");
        if (file is null) return;
        try
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(file);
            enc.Save(fs);
            Toasts.Show(L["Share.Title"], L.Format("Logs.Exported", file), ToastKind.Success);
        }
        catch (Exception ex) { StatusMessage = Redactor.Redact(ex.Message); }
    }

    [RelayCommand] private void ImportFile()
    {
        var f = Dialogs.OpenFile("Config (*.json;*.conf;*.txt;*.ovpn)|*.json;*.conf;*.txt;*.ovpn|OpenVPN (*.ovpn)|*.ovpn|All|*.*");
        if (f is null) return;
        if (new FileInfo(f).Length > 5_000_000) { StatusMessage = L["Dns.FileTooLarge"]; return; }
        ImportText = File.ReadAllText(f);
        ImportName = Path.GetFileNameWithoutExtension(f);
        // v0.4.15: .ovpn files often reference sibling files (ca.crt, ta.key …). Remember the folder so the
        // import can inline them; a hand-pasted config without a folder just keeps its external references.
        _importFileDir = Path.GetDirectoryName(f);
    }

    private string? _importFileDir;

    [RelayCommand] private async Task ValidateAsync()
    {
        if (Selected is null) return;
        await using var engine = Selected.Engine switch
        {
            EngineKind.Xray => (ITunnelEngine)new Koalix.Xray.XrayEngine(_paths),
            EngineKind.WireGuard => new WireGuardEngine(_paths, AppHost.Get<IProcessRunner>()),
            EngineKind.OpenVpn => new OpenVpnEngine(_paths),
            _ => new SingBoxEngine(_paths),
        };
        var v = await engine.ValidateAsync(Selected, AppHost.Get<DashboardViewModel>().CurrentPolicy(), CancellationToken.None);
        StatusMessage = v.Ok ? L["Profiles.Valid"] : string.Join("\n", v.Errors.Select(e => L[e]));
    }

    [RelayCommand] private void Delete()
    {
        if (Selected is null || !Dialogs.Confirm(L["Common.Delete"], L.Format("Dns.ConfirmDelete", Selected.Name))) return;
        if (_cm.Active?.Profile.Id == Selected.Id) { StatusMessage = L["Profiles.InUse"]; return; }
        _repo.Delete(Selected.Id); Reload();
    }

    /* ─────────── Multi-select bulk actions (v0.4.20) ─────────── */

    /// <summary>Ticks/unticks every visible row.</summary>
    [RelayCommand] private void ToggleSelectAll()
    {
        var target = !AllSelected;
        foreach (var r in Rows) r.IsChecked = target;
        RowCheckedChanged();
    }

    /// <summary>Moves every ticked profile into one group (prompt; empty input clears the tag).</summary>
    [RelayCommand] private void MoveSelectedToGroup()
    {
        var ids = Rows.Where(r => r.IsChecked && r.Profile is not null).Select(r => r.Profile.Id).ToList();
        if (ids.Count == 0) return;
        var tag = Dialogs.Prompt(L["Profiles.MoveGroup"], L.Format("Profiles.MoveGroupPrompt", ids.Count), TagFilter == "All" ? "" : TagFilter);
        if (tag is null) return; // cancelled — empty input clears on purpose
        _repo.SetTagMany(ids, tag);
        StatusMessage = string.IsNullOrWhiteSpace(tag) ? L.Format("Profiles.TagClearedMany", ids.Count) : L.Format("Profiles.MoveGroupDone", ids.Count, tag.Trim());
        foreach (var r in Rows) r.IsChecked = false;
        RowCheckedChanged();
        Reload();
    }

    /// <summary>Deletes every ticked profile (one confirm; refuses to remove the connected one).</summary>
    [RelayCommand] private void DeleteSelected()
    {
        var activeId = _cm.Active?.Profile.Id;
        var ids = Rows.Where(r => r.IsChecked && r.Profile is not null && r.Profile.Id != activeId).Select(r => r.Profile.Id).ToList();
        if (ids.Count == 0) { StatusMessage = L["Profiles.NothingSelected"]; return; }
        if (!Dialogs.Confirm(L["Profiles.DeleteSel"], L.Format("Profiles.DeleteSelConfirm", ids.Count))) return;
        _repo.DeleteMany(ids);
        StatusMessage = L.Format("Profiles.DeleteSelDone", ids.Count);
        RowCheckedChanged();
        Reload();
    }

    /// <summary>
    /// Engine switch for every ticked row (v0.4.20). Honest rules: only share-link profiles can switch —
    /// OpenVPN/WireGuard/JSON configs carry the engine in their format (their servers ARE their config), and an
    /// Xray-forced share link runs the Xray core behind sing-box's TUN exactly like the single-row switch does.
    /// </summary>
    [RelayCommand] private void SwitchEngineSelected()
    {
        var activeId = _cm.Active?.Profile.Id;
        var ticked = Rows.Where(r => r.IsChecked && r.Profile is not null).ToList();
        if (ticked.Count == 0) { StatusMessage = L["Profiles.NothingSelected"]; return; }
        var switchable = ticked.Where(r => r.CanSwitchEngine && r.Profile.Id != activeId).ToList();
        var skipped = ticked.Count - switchable.Count;
        if (switchable.Count == 0) { StatusMessage = L["Profiles.EngineSelNone"]; return; }
        var target = switchable[0].Profile.Engine == EngineKind.Xray ? EngineKind.SingBox : EngineKind.Xray;
        // sing-box only when every picked node is natively supported (same rule as the single-row switch)
        if (target == EngineKind.SingBox)
        {
            var blocked = 0;
            foreach (var r in switchable)
                if (!SingBoxConfigBuilder.IsNativelySupported(ShareLinkParser.Parse(r.Profile.Content), out _)) blocked++;
            if (blocked > 0) { StatusMessage = L.Format("Profiles.EngineSelBlocked", blocked); return; }
        }
        _repo.SetEngineMany(switchable.Select(r => r.Profile.Id), target);
        StatusMessage = L.Format("Profiles.EngineSelDone", switchable.Count, target) + (skipped > 0 ? "\n" + L.Format("Profiles.EngineSelSkipped", skipped) : "");
        foreach (var r in Rows) r.IsChecked = false;
        RowCheckedChanged();
        Reload();
    }
}
