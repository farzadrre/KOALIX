using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koalix.App.Services;
using Koalix.Infrastructure;
using Koalix.Tunneling;

namespace Koalix.App.ViewModels;

/// <summary>
/// v0.4.38 — the About page (user: «یه قسمت هم میتونی بزاری برای اپ اسم و لوگوش … وقتی میایم روش سلکت میکنیم
/// متحرک باشه مث بقیه بخش ها»): the app name + animated logo, version, what is installed (engines) and a few
/// handy shortcuts (data folder, replay the tour). Engine detection runs every time the page opens.
/// </summary>
public sealed partial class AboutViewModel : PageViewModel
{
    private readonly AppPaths _paths;
    private readonly EnginePaths _engines;

    public string Version => "v" + UpdateService.CurrentVersion;
    public string Runtime => $".NET {Environment.Version.Major}.{Environment.Version.Minor} · {(Environment.Is64BitProcess ? "x64" : "x86")} · Windows {Environment.OSVersion.Version.Build}";
    public string DataFolder => _paths.Root;

    [ObservableProperty] private bool _hasSingBox;
    [ObservableProperty] private bool _hasXray;
    [ObservableProperty] private bool _hasWireGuard;
    [ObservableProperty] private bool _hasWireSock;
    [ObservableProperty] private bool _isAdmin;

    public AboutViewModel(AppPaths paths, EnginePaths engines)
    {
        _paths = paths; _engines = engines;
        Refresh();
    }

    public override void OnActivated() => Refresh();

    private void Refresh()
    {
        try
        {
            HasSingBox = File.Exists(_engines.SingBoxExe);
            HasXray = File.Exists(_engines.XrayExe);
            HasWireGuard = File.Exists(EnginePaths.WireGuardExe);
            HasWireSock = EnginePaths.WireSockExe is { } ws && File.Exists(ws);
            IsAdmin = Koalix.SystemIntegration.ProcessRunner.IsElevated;
        }
        catch { /* purely informational */ }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.Root}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void ReplayTour()
    {
        if (Application.Current.MainWindow is MainWindow mw) mw.ShowOnboarding();
    }

    [RelayCommand]
    private void CopyInfo()
    {
        try
        {
            Clipboard.SetText($"KOALIX {Version}\n{Runtime}\nsing-box: {HasSingBox} · xray: {HasXray} · WireGuard: {HasWireGuard} · WireSock: {HasWireSock} · admin: {IsAdmin}");
            Toasts.Show(L["About.Copied"], "", ToastKind.Success);
        }
        catch { }
    }
}
