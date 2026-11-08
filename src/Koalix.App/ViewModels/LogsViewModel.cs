using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;

namespace Koalix.App.ViewModels;

public sealed partial class LogsViewModel : PageViewModel
{
    private readonly RingSink _ring;
    private readonly AppPaths _paths;
    public ObservableCollection<LogLine> Lines { get; } = [];
    public IReadOnlyList<string> Categories { get; } = ["All", .. Enum.GetNames<LogCategory>()];
    [ObservableProperty] private string _category = "All";
    [ObservableProperty] private string _search = "";
    private bool _active;

    public LogsViewModel(RingSink ring, AppPaths paths)
    {
        _ring = ring; _paths = paths;
        ring.Emitted += l => { if (_active) Ui(() => { if (Match(l)) { Lines.Add(l); while (Lines.Count > 3000) Lines.RemoveAt(0); } }); };
    }
    public override void OnActivated() { _active = true; Reload(); }
    public override void OnDeactivated() => _active = false;
    partial void OnCategoryChanged(string value) => Reload();
    partial void OnSearchChanged(string value) => Reload();

    private bool Match(LogLine l) => (Category == "All" || l.Category == Category) && (Search.Length == 0 || l.Message.Contains(Search, StringComparison.OrdinalIgnoreCase));
    [RelayCommand] private void Reload() { Lines.Clear(); foreach (var l in _ring.Snapshot().Where(Match).TakeLast(3000)) Lines.Add(l); }
    [RelayCommand] private void OpenFolder() => System.Diagnostics.Process.Start("explorer.exe", _paths.LogsDir);
    [RelayCommand] private void ExportDiagnostics()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var file = DiagnosticReport.Create(_paths, _ring.Snapshot().Select(l => $"{l.At:O} [{l.Level}] [{l.Category}] {l.Message}"), dir);
        StatusMessage = L.Format("Logs.Exported", file);
    }
}
