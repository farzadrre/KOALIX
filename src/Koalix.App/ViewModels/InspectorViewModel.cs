using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SystemIntegration;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

/// <summary>Module 23 — Routing Inspector. Shows configured vs observed route per app/connection. Never guesses: unverifiable = Unknown.</summary>
public sealed partial class InspectorViewModel : PageViewModel
{
    private readonly ConnectionManager _cm;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, string?> _pidCache = [];
    private bool _refreshing;
    public ObservableCollection<InspectedConnection> Connections { get; } = [];
    public ObservableCollection<AppTrafficSummary> Apps { get; } = [];
    public ObservableCollection<DnsLookupRecord> DnsLookups { get; } = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _onlyMismatch;
    [ObservableProperty] private string _engineStatus = "";
    [ObservableProperty] private string _mismatchSummary = "";

    public InspectorViewModel(ConnectionManager cm, HttpClient http)
    {
        _cm = cm; _http = http;
        _timer.Tick += async (_, _) => await RefreshAsync();
        cm.Log += (cat, line) =>
        {
            if (DnsInspector.TryParse(line, DateTimeOffset.Now) is { } rec)
                Ui(() => { DnsLookups.Insert(0, rec); while (DnsLookups.Count > 300) DnsLookups.RemoveAt(DnsLookups.Count - 1); });
        };
    }

    public override void OnActivated() { _timer.Start(); _ = RefreshAsync(); }
    public override void OnDeactivated() => _timer.Stop();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ClashConnectionsSnapshot? snap = null;
            if (_cm.Active?.Engine.StatsApi is { } api)
            {
                try { snap = await new ClashApiClient(_http, api.Url, api.Secret).GetConnectionsAsync(CancellationToken.None); }
                catch (Exception ex) { AppHost.Log(LogCategory.Routing, "inspector: " + ex.Message); }
            }
            EngineStatus = snap is null ? L["Inspector.NoEngineData"] : L.Format("Inspector.EngineData", snap.Connections?.Length ?? 0);
            var sockets = await Task.Run(RoutingInspector.GetSockets);
            var tun = RoutingInspector.TunAddresses();
            var list = await Task.Run(() => RoutingInspector.Inspect(sockets, snap, tun, Path));
            var policy = _cm.Active?.Policy ?? AppHost.Get<DashboardViewModel>().CurrentPolicy();
            var apps = RoutingInspector.Summarize(list, p => ConfiguredRoute(policy, p));

            var f = FilterText.Trim();
            Connections.Clear();
            foreach (var c in list.Where(c => f.Length == 0 || (c.ProcessName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) || (c.Host?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) || (c.Remote?.Contains(f) ?? false)).Take(500))
                Connections.Add(c);
            Apps.Clear();
            var mismatches = 0;
            foreach (var a in apps)
            {
                var mismatch = IsMismatch(a);
                if (mismatch) mismatches++;
                if (!OnlyMismatch || mismatch) Apps.Add(a);
            }
            MismatchSummary = mismatches == 0 ? L["Inspector.NoMismatch"] : L.Format("Inspector.Mismatch", mismatches);
        }
        finally { _refreshing = false; }
    }

    private string? Path(int pid)
    {
        if (_pidCache.Count > 2000) _pidCache.Clear();
        if (!_pidCache.TryGetValue(pid, out var p)) _pidCache[pid] = p = ProcessScanner.GetProcessPath(pid);
        return p;
    }

    public static string ConfiguredRoute(SplitPolicy policy, string exePath)
    {
        var rule = policy.Rules.FirstOrDefault(r => !r.IsFolderRule && string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase))
            ?? policy.Rules.FirstOrDefault(r => r.Matches(exePath));
        if (rule?.Action == AppAction.Block) return "block";
        return policy.Mode switch
        {
            SplitMode.FullTunnel => "tunnel",
            SplitMode.Include => rule?.Action == AppAction.Tunnel ? "tunnel" : "direct",
            _ => rule?.Action == AppAction.Direct ? "direct" : "tunnel",
        };
    }

    public static bool IsMismatch(AppTrafficSummary a) => (a.ConfiguredRoute, a.ObservedRoute) switch
    {
        (_, ObservedRoute.Unknown) => false, // cannot verify ≠ mismatch
        ("tunnel", ObservedRoute.Tunnel) or ("direct", ObservedRoute.Direct) or ("direct", ObservedRoute.Bypass) or ("block", ObservedRoute.Blocked) => false,
        _ => true,
    };
}
