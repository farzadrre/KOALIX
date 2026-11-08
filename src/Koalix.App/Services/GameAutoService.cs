using System.Text.Json;
using System.Windows.Threading;
using Koalix.App.Localization;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;

namespace Koalix.App.Services;

/// <summary>
/// v0.4.37 — per-game auto-activation (user: «فعال شدن خودکار با اجرای بازی … اینم بساز»). While at least one
/// game profile has "auto" on, every 3 s the running processes are checked (only the handful whose image name
/// matches a game — full paths are resolved for those alone, off the UI thread). On a game's START: connect if the
/// tunnel is down, raise the game to High priority, toast. On its EXIT: toast, and disconnect only a tunnel KOALIX
/// opened for a game, once no other auto game is still running. The decisions live in <see cref="GameLaunchTracker"/>.
/// </summary>
public sealed class GameAutoService
{
    private readonly SettingsRepository _settings;
    private readonly GameLaunchTracker _tracker = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IReadOnlyList<GameProfile> _profiles = [];
    private bool _scanning;
    private DateTimeOffset _connectIssuedAt;

    public GameAutoService(SettingsRepository settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        try { SetProfiles(JsonSerializer.Deserialize<List<GameProfile>>(_settings.Get("gaming.profiles") ?? "[]") ?? []); }
        catch { SetProfiles([]); }
    }

    /// <summary>Called by the Gaming page whenever its list changes; the scan timer runs only while something is armed.</summary>
    public void SetProfiles(IEnumerable<GameProfile> profiles)
    {
        _profiles = profiles.Where(p => p.AutoActivate).ToList();
        if (_profiles.Count > 0) { if (!_timer.IsEnabled) _timer.Start(); }
        else
        {
            _timer.Stop();
            _ = ApplyAsync(_tracker.Update([], new HashSet<string>())); // games un-armed while running → treat as stopped
        }
    }

    private async Task TickAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var auto = _profiles;
            var names = GameLaunchTracker.CandidateNames(auto);
            var running = await Task.Run(() => GameLaunchTracker.ScanRunning(names));
            await ApplyAsync(_tracker.Update(auto, running));
        }
        catch { /* a failed scan just waits for the next tick */ }
        finally { _scanning = false; }
    }

    private static ViewModels.DashboardViewModel Dash => AppHost.Get<ViewModels.DashboardViewModel>();
    private static bool TunnelUp(ViewModels.DashboardViewModel d) => d.State is not (ConnectionState.Disconnected or ConnectionState.Error);

    private async Task ApplyAsync(GameLaunchDelta delta)
    {
        var dash = Dash;
        // The user disconnected by hand while a game ran → the tunnel is not ours to close any more.
        if (_tracker.ConnectedForGame && !TunnelUp(dash) && DateTimeOffset.Now - _connectIssuedAt > TimeSpan.FromSeconds(45))
            _tracker.ReleaseOwnership();
        if (delta.IsEmpty) return;
        var L = Loc.I;
        foreach (var g in delta.Started)
        {
            var connect = _tracker.ShouldConnect(TunnelUp(dash), autoConnectEnabled: true);
            Toasts.Show(L.Format("Gaming.Auto.Started", g.Name), connect ? L["Gaming.Auto.Connecting"] : L["Gaming.Auto.Boosted"], ToastKind.Success);
            if (connect)
            {
                _connectIssuedAt = DateTimeOffset.Now;
                try { if (dash.ConnectCommand.CanExecute(null)) dash.ConnectCommand.Execute(null); } catch { }
            }
            Boost(g.ExecutablePath);
            _ = BoostLaterAsync(g.ExecutablePath); // launchers often respawn the real game a few seconds later
        }
        foreach (var g in delta.Stopped)
        {
            var disconnect = _tracker.ShouldDisconnect(TunnelUp(dash));
            Toasts.Show(L.Format("Gaming.Auto.Stopped", g.Name), disconnect ? L["Gaming.Auto.Disconnecting"] : null, ToastKind.Info);
            if (disconnect)
                try { if (dash.IsConnected && dash.ConnectCommand.CanExecute(null)) dash.ConnectCommand.Execute(null); } catch { } // Connect toggles off when connected
        }
        await Task.CompletedTask;
    }

    private static void Boost(string exe) { try { GamingOptimizer.BoostProcesses(exe); } catch { } }

    private static async Task BoostLaterAsync(string exe)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        await Task.Run(() => Boost(exe));
    }
}
