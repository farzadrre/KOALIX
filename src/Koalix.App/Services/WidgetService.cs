using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using Koalix.App.Localization;
using Koalix.App.ViewModels;
using Koalix.App.Views;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;
using Koalix.Tunneling;

namespace Koalix.App.Services;

/// <summary>
/// v0.4.38 — owns the floating mini widget: its settings (widget.enabled / mode / opacity / clickThrough / locked /
/// x / y), its window, and the 1 s sampler. Hardware sensors are read only in the modes that show them, off the UI
/// thread; network speed comes from the physical adapters; ping is the tunnel's own health latency while connected
/// and a light ICMP probe to 1.1.1.1 (every 3 s) otherwise. Off = no window, no timer, no sensor handles.
/// </summary>
public sealed class WidgetService
{
    private readonly SettingsRepository _settings;
    private readonly ConnectionManager _cm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NetThroughput _net = new();
    private HardwareMonitor? _hw;
    private WidgetWindow? _win;
    private bool _busy;
    private int _ticks;
    private double? _directPing;
    private DateTime _lastPingAt;

    public WidgetViewModel Vm { get; } = new();
    /// <summary>Raised when the widget is turned on/off or its options change (Settings page, tray panel).</summary>
    public event Action? Changed;
    public event Action? OpenAppRequested;

    public static readonly double[] OpacitySteps = [1.0, 0.85, 0.7, 0.55];

    public WidgetService(SettingsRepository settings, ConnectionManager cm)
    {
        _settings = settings;
        _cm = cm;
        _timer.Tick += async (_, _) => await TickAsync();
        Vm.Mode = Normalize(_settings.Get("widget.mode"));
    }

    public bool Enabled => _settings.Get("widget.enabled") == "1";
    public bool IsShown => _win is { IsVisible: true };
    public string Mode => Vm.Mode;
    public double Opacity => double.TryParse(_settings.Get("widget.opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? Math.Clamp(o, 0.3, 1) : 0.92;
    public bool ClickThrough => _settings.Get("widget.clickThrough") == "1";
    public bool Locked => _settings.Get("widget.locked") == "1";

    private static string Normalize(string? m) => m is not null && WidgetViewModel.Modes.Contains(m) ? m : "full";

    public void Start()
    {
        if (Enabled) Open();
    }

    public void SetEnabled(bool on, bool fromWidget = false)
    {
        _settings.Set("widget.enabled", on ? "1" : "0");
        if (on) Open(); else CloseWindow();
        if (!on && fromWidget) Toasts.Show(Loc.I["Widget.ClosedTitle"], Loc.I["Widget.ClosedText"], ToastKind.Info);
        Changed?.Invoke();
    }

    public void Toggle() => SetEnabled(!Enabled);

    public void SetMode(string mode)
    {
        mode = Normalize(mode);
        _settings.Set("widget.mode", mode);
        Vm.Mode = mode;
        _win?.ApplyMode(mode);
        if (!Vm.NeedsHardware) Vm.Footer = "";
        _ = TickAsync();
        Changed?.Invoke();
    }

    public void SetOpacity(double o)
    {
        o = Math.Clamp(o, 0.3, 1);
        _settings.Set("widget.opacity", o.ToString("0.##", CultureInfo.InvariantCulture));
        if (_win is not null) _win.Opacity = o;
        Changed?.Invoke();
    }

    public void CycleOpacity()
    {
        var cur = Opacity;
        var i = Array.FindIndex(OpacitySteps, s => Math.Abs(s - cur) < 0.04);
        SetOpacity(OpacitySteps[(i + 1) % OpacitySteps.Length]);
    }

    public void SetClickThrough(bool on)
    {
        _settings.Set("widget.clickThrough", on ? "1" : "0");
        _win?.SetClickThrough(on);
        if (on) Toasts.Show(Loc.I["Widget.ClickThroughTitle"], Loc.I["Widget.ClickThroughText"], ToastKind.Info);
        Changed?.Invoke();
    }

    public void SetLocked(bool on)
    {
        _settings.Set("widget.locked", on ? "1" : "0");
        Changed?.Invoke();
    }

    public void RememberPosition(double x, double y)
    {
        _settings.Set("widget.x", x.ToString("0", CultureInfo.InvariantCulture));
        _settings.Set("widget.y", y.ToString("0", CultureInfo.InvariantCulture));
    }

    public void ResetPosition()
    {
        _settings.Set("widget.x", "");
        _settings.Set("widget.y", "");
        if (_win is not null) PlaceDefault(_win);
    }

    public void RequestOpenApp() => OpenAppRequested?.Invoke();

    private void Open()
    {
        try
        {
            if (_win is null)
            {
                _win = new WidgetWindow(this, Vm) { Opacity = Opacity };
                _win.SetClickThrough(ClickThrough);
                _win.ApplyMode(Vm.Mode);
                _win.Closed += (_, _) => { _win = null; _timer.Stop(); };
                _win.Left = -10000; _win.Top = -10000;
                _win.Show();
                _win.UpdateLayout();
                if (!RestorePosition(_win)) PlaceDefault(_win);
            }
            else if (!_win.IsVisible) _win.Show();
            _timer.Start();
            _ = TickAsync();
        }
        catch (Exception ex) { StartupLog.Trace("widget open: " + ex.Message); }
    }

    private void CloseWindow()
    {
        _timer.Stop();
        var w = _win;
        _win = null;
        try { w?.Close(); } catch { }
        _hw?.Dispose();
        _hw = null;
    }

    private bool RestorePosition(Window w)
    {
        if (!double.TryParse(_settings.Get("widget.x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(_settings.Get("widget.y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
        // a monitor that is gone (laptop undocked) must not strand the widget off-screen
        var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
        if (x < vl - 20 || y < vt - 20 || x > vl + SystemParameters.VirtualScreenWidth - 40 || y > vt + SystemParameters.VirtualScreenHeight - 30) return false;
        w.Left = x; w.Top = y;
        return true;
    }

    private static void PlaceDefault(Window w)
    {
        var work = SystemParameters.WorkArea;
        var width = w.ActualWidth > 10 ? w.ActualWidth : 260;
        w.Left = work.Right - width - 16;
        w.Top = work.Top + 16;
    }

    private async Task TickAsync()
    {
        if (_busy || _win is null) return;
        _busy = true;
        try
        {
            var needHw = Vm.NeedsHardware;
            if (needHw) _hw ??= new HardwareMonitor();
            var hw = needHw ? _hw : null;
            var (net, snap) = await Task.Run(() => (_net.Sample(), hw?.Sample()));

            var state = _cm.StateMachine.State;
            var connected = state is ConnectionState.Connected or ConnectionState.Degraded;
            if (!connected && (DateTime.UtcNow - _lastPingAt).TotalSeconds >= 3)
            {
                _lastPingAt = DateTime.UtcNow;
                _ = ProbeDirectAsync();
            }
            var ping = connected ? _cm.LastLatencyMs : _directPing;
            Vm.SetNetwork(state, _cm.Active?.Profile.Name, ping, connected, net.DownBps, net.UpBps);
            if (snap is not null) Vm.SetHardware(snap);
            else Vm.Footer = "";
            if (++_ticks % 5 == 0) _win?.KeepOnTop();
        }
        catch (Exception ex) { StartupLog.Trace("widget tick: " + ex.Message); }
        finally { _busy = false; }
    }

    private async Task ProbeDirectAsync()
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync("1.1.1.1", 1500);
            _directPing = r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { _directPing = null; }
    }
}
