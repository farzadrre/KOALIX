using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Dns;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;

namespace Koalix.App.ViewModels;

public sealed partial class DnsRow : ObservableObject
{
    public required DnsProfile Profile { get; init; }
    public string Name => Profile.Name;
    public string Servers => string.Join(", ", Profile.AllServers);
    [ObservableProperty] private string _median = "—";
    [ObservableProperty] private string _jitter = "—";
    [ObservableProperty] private string _loss = "—";
    [ObservableProperty] private string _dnsSuccess = "—";
    [ObservableProperty] private string _icmp = "—";
    [ObservableProperty] private double _score = double.NaN;
    [ObservableProperty] private int _rank;
    /// <summary>0–100 bar relative to the best tested profile (100 = best).</summary>
    [ObservableProperty] private double _quality;
    [ObservableProperty] private bool _isTested;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
}

public sealed partial class DnsViewModel : PageViewModel
{
    private readonly DnsProfileRepository _repo;
    private readonly DnsTester _tester;
    private readonly WindowsDnsConfigurator _cfg;
    private readonly NetworkStateJournal _journal;
    private CancellationTokenSource? _testCts;

    public ObservableCollection<DnsRow> Rows { get; } = [];
    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public IReadOnlyList<RankingStrategy> Strategies { get; } = Enum.GetValues<RankingStrategy>();
    [ObservableProperty] private RankingStrategy _strategy = RankingStrategy.BalancedGaming;
    [ObservableProperty] private bool _showAdd;
    [RelayCommand] private void ToggleAdd() => ShowAdd = !ShowAdd;
    [ObservableProperty] private DnsRow? _selected;
    [ObservableProperty] private AdapterInfo? _selectedAdapter;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newPrimary = "";
    [ObservableProperty] private string _newSecondary = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _importReport = "";

    public DnsViewModel(DnsProfileRepository repo, DnsTester tester, WindowsDnsConfigurator cfg, NetworkStateJournal journal)
    { _repo = repo; _tester = tester; _cfg = cfg; _journal = journal; }

    public override void OnActivated() { Reload(); RefreshAdapters(); }

    private void Reload()
    {
        Rows.Clear();
        foreach (var p in _repo.All()) Rows.Add(new DnsRow { Profile = p });
        if (Rows.Count == 0) SeedDefaults();
    }

    private void SeedDefaults()
    {
        var builtin = DnsTxtFormat.Parse("""
            Cloudflare
            1.1.1.1
            1.0.0.1
            Google
            8.8.8.8
            8.8.4.4
            Quad9
            9.9.9.9
            149.112.112.112
            AdGuard
            94.140.14.14
            94.140.15.15
            OpenDNS
            208.67.222.222
            208.67.220.220
            Shecan
            178.22.122.100
            185.51.200.2
            Electro
            78.157.42.100
            78.157.42.101
            Radar Game
            10.202.10.10
            10.202.10.11
            """);
        foreach (var p in builtin.Profiles) _repo.TryAdd(p, out _);
        foreach (var p in _repo.All()) Rows.Add(new DnsRow { Profile = p });
    }

    [RelayCommand] private void RefreshAdapters()
    {
        Adapters.Clear();
        foreach (var a in _cfg.GetAdapters().Where(a => !a.IsVirtualTunnel)) Adapters.Add(a);
        SelectedAdapter ??= Adapters.FirstOrDefault();
    }

    [RelayCommand] private void Add()
    {
        var p = new DnsProfile { Name = NewName.Trim(), PrimaryV4 = NullIfEmpty(NewPrimary), SecondaryV4 = NullIfEmpty(NewSecondary) };
        var errors = p.Validate();
        if (errors.Count > 0) { StatusMessage = string.Join("\n", errors.Select(e => L[e])); return; }
        StatusMessage = _repo.TryAdd(p, out _) ? L["Dns.Added"] : L["Dns.DuplicateMsg"];
        NewName = NewPrimary = NewSecondary = "";
        Reload();
    }
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand] private void Duplicate()
    {
        if (Selected is null) return;
        var copy = Selected.Profile.Duplicate(L["Dns.CopySuffix"]) with { SecondaryV4 = Selected.Profile.SecondaryV4 is null ? Selected.Profile.PrimaryV4 : null };
        StatusMessage = _repo.TryAdd(copy, out _) ? L["Dns.Added"] : L["Dns.DuplicateMsg"];
        Reload();
    }

    [RelayCommand] private void Delete()
    {
        if (Selected is null || !Dialogs.Confirm(L["Common.Delete"], L.Format("Dns.ConfirmDelete", Selected.Name))) return;
        _repo.Delete(Selected.Profile.Id); Reload();
    }

    [RelayCommand] private void Import()
    {
        var file = Dialogs.OpenFile("Text (*.txt)|*.txt|All|*.*");
        if (file is null) return;
        if (new FileInfo(file).Length > DnsTxtFormat.MaxFileBytes) { StatusMessage = L["Dns.FileTooLarge"]; return; }
        var r = DnsTxtFormat.Parse(File.ReadAllText(file));
        int added = 0, dup = 0;
        foreach (var p in r.Profiles) if (_repo.TryAdd(p, out _)) added++; else dup++;
        ImportReport = L.Format("Dns.ImportReport", added, dup, r.Issues.Count) +
            (r.Issues.Count > 0 ? "\n" + string.Join("\n", r.Issues.Take(30).Select(i => $"#{i.Line}: {L[i.Code]} — {i.Text}")) : "");
        Reload();
    }

    [RelayCommand] private void Export()
    {
        var file = Dialogs.SaveFile("Text (*.txt)|*.txt", "koalix-dns.txt");
        if (file is null) return;
        File.WriteAllText(file, DnsTxtFormat.Export(Rows.Select(r => r.Profile)));
        StatusMessage = L["Common.Saved"];
    }

    [RelayCommand] private async Task TestAllAsync()
    {
        if (IsBusy) { _testCts?.Cancel(); return; }
        IsBusy = true; Progress = 0;
        _testCts = new CancellationTokenSource();
        var total = Rows.Count; var done = 0;
        var byId = Rows.ToDictionary(r => r.Profile.Id);
        var progress = new Progress<DnsProfileTestResult>(res =>
        {
            done++; Progress = done * 100.0 / total;
            if (byId.TryGetValue(res.Profile.Id, out var row)) Fill(row, res);
        });
        try
        {
            var results = await _tester.TestAllAsync(Rows.Select(r => r.Profile).ToList(), new DnsTestOptions { Strategy = Strategy }, progress, _testCts.Token);
            var rank = 1;
            foreach (var r in results.OrderBy(r => r.Score))
                if (byId.TryGetValue(r.Profile.Id, out var row)) row.Rank = r.Score >= DnsScorer.Unusable ? 0 : rank++;
            var bestScore = results.Where(r => r.Score < DnsScorer.Unusable).Select(r => r.Score).DefaultIfEmpty(double.NaN).Min();
            foreach (var row in Rows)
                row.Quality = row.Rank == 0 || double.IsNaN(bestScore) || double.IsNaN(row.Score) ? 4 : Math.Clamp(100 * Math.Max(bestScore, 0.5) / Math.Max(row.Score, 0.5), 6, 100);
            var sorted = Rows.OrderBy(r => r.Rank == 0 ? int.MaxValue : r.Rank).ToList();
            Rows.Clear(); foreach (var r in sorted) Rows.Add(r);
            StatusMessage = sorted.FirstOrDefault(r => r.Rank == 1) is { } best ? L.Format("Dns.Best", best.Name) : L["Dns.NoneUsable"];
        }
        catch (OperationCanceledException) { StatusMessage = L["Common.Cancelled"]; }
        finally { IsBusy = false; }
    }

    private void Fill(DnsRow row, DnsProfileTestResult res)
    {
        var s = res.Servers.OrderBy(x => DnsScorer.ScoreServer(x, res.Strategy)).First();
        row.Median = s.DnsQuery.MedianMs is { } m ? $"{m:0} ms" : "—";
        row.Jitter = s.DnsQuery.JitterMs is { } j ? $"{j:0.0} ms" : "—";
        row.Loss = $"{s.Icmp.LossPercent:0}%";
        row.DnsSuccess = $"{s.DnsSuccessRate:0}%";
        row.Icmp = s.IcmpReachable ? (s.Icmp.MedianMs is { } im ? $"{im:0} ms" : "✓") : L["Dns.IcmpBlocked"];
        row.Score = res.Score;
        row.IsTested = true;
        _repo.SaveScore(res.Profile.Id, new LatencyStatsRow(s.DnsQuery.MedianMs, s.DnsQuery.JitterMs, s.Icmp.LossPercent, s.DnsSuccessRate, res.Score));
    }

    [RelayCommand] private async Task ApplyAsync()
    {
        if (Selected is null || SelectedAdapter is null) return;
        var a = SelectedAdapter;
        if (!Dialogs.Confirm(L["Dns.Apply"], L.Format("Dns.ConfirmApply", Selected.Name, a.Name))) return;
        var snap = _cfg.Snapshot(a);
        var entry = _journal.Record(JournalKind.DnsChange, a.Id, NetworkStateJournal.Serialize(snap), Selected.Servers);
        var v4 = Selected.Profile.V4Servers.ToList(); var v6 = Selected.Profile.V6Servers.ToList();
        var ok = true;
        if (v4.Count > 0) ok &= (await _cfg.ApplyAsync(a, IpFamily.IPv4, v4)).Success;
        if (v6.Count > 0) ok &= (await _cfg.ApplyAsync(a, IpFamily.IPv6, v6)).Success;
        // Verify: re-read adapter DNS and confirm the change actually happened.
        var after = _cfg.GetAdapters().FirstOrDefault(x => x.Id == a.Id);
        var verified = after is not null && v4.All(s => after.DnsV4.Contains(s));
        if (!ok || !verified) { await _cfg.RestoreAsync(snap); _journal.Complete(entry.Id); StatusMessage = L["Dns.ApplyFailed"]; }
        else StatusMessage = L.Format("Dns.Applied", Selected.Name, a.Name);
        AppHost.Log(LogCategory.Dns, $"apply {Selected.Name} to {a.Name}: ok={ok} verified={verified}");
        RefreshAdapters();
    }

    [RelayCommand] private async Task RestoreAsync()
    {
        if (SelectedAdapter is null) return;
        var pending = _journal.Pending().Where(e => e.Kind == JournalKind.DnsChange && e.Target == SelectedAdapter.Id).ToList();
        if (pending.Count == 0)
        {
            if (!Dialogs.Confirm(L["Dns.Restore"], L["Dns.ConfirmDhcp"])) return;
            await _cfg.SetDhcpAsync(SelectedAdapter, IpFamily.IPv4); await _cfg.SetDhcpAsync(SelectedAdapter, IpFamily.IPv6);
        }
        else
        {
            var first = pending.OrderBy(e => e.At).First(); // oldest = original state
            if (NetworkStateJournal.Deserialize<DnsSnapshot>(first.Before) is { } snap) await _cfg.RestoreAsync(snap);
            foreach (var e in pending) _journal.Complete(e.Id);
        }
        StatusMessage = L["Dns.Restored"];
        RefreshAdapters();
    }

    [RelayCommand] private async Task QuickTestAsync(string? server)
    {
        if (server is null || !IPAddress.TryParse(server, out _)) return;
        var r = await _tester.TestServerAsync(server, new DnsTestOptions(), CancellationToken.None);
        StatusMessage = $"{server}: DNS {r.DnsQuery.MedianMs:0} ms, {r.DnsSuccessRate:0}%";
    }
}
