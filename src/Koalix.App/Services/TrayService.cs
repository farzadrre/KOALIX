using System.Drawing;
using System.Windows;
using Koalix.App.Views;
using Koalix.Infrastructure;
using Koalix.Tunneling;
using WinForms = System.Windows.Forms;

namespace Koalix.App.Services;

/// <summary>
/// v0.4.24 — the tray, redesigned per the approved list («پنل مدرن Tray به‌جای منوی فعلی: شیشه‌ای، با فونت
/// IRANSansX، دکمهٔ اتصال، فهرست سرورها با پینگ رنگی و پرچم، و انیمیشن باز شدن»): left- OR right-click on
/// the notification icon now opens <see cref="TrayPanelWindow"/> — a glassy animated WPF panel with the live
/// state, a big connect button and the fastest servers with flags + colour-coded pings. Double-click still
/// opens the main window; balloon notifications are unchanged. (v0.4.12–v0.4.13 had a themed WinForms
/// ContextMenuStrip — functional, but it could never feel like part of the app.)
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TunnelProfileRepository _profiles;
    private WinForms.NotifyIcon? _icon;
    private TrayPanelWindow? _panel;
    public event Action? ShowRequested, ToggleConnectionRequested, ExitRequested;
    /// <summary>Raised when a profile is picked from the panel's server list (id from the repository).</summary>
    public event Action<long>? ConnectProfileRequested;

    // Live state (kept current by DashboardViewModel on every connection-state change).
    private volatile string _stateText = "KOALIX";
    private volatile bool _connected;

    public TrayService(TunnelProfileRepository profiles) => _profiles = profiles;

    public void Initialize()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/KOALIX;component/Assets/koalix.ico"))?.Stream;
        // v0.4.26 CRITICAL FIX: the 0.4.24 tray redesign dropped the _baseIcon assignment (the compiler even
        // warned CS0649 «never assigned») — the first Connected-state icon swap then called Overlay(null) →
        // new Icon(null, 32, 32) threw ArgumentNullException('original') INSIDE the state-machine event chain,
        // the connect flow caught it and tore the healthy tunnel down → «دیگه هیچ کانفیگی وصل نمیشه».
        // The base icon is now stored once and reused by every later swap.
        _baseIcon ??= iconStream is null ? SystemIcons.Application : new Icon(iconStream);
        _icon = new WinForms.NotifyIcon
        {
            Icon = _baseIcon,
            Text = "KOALIX", Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        // v0.4.24: both buttons open the modern panel (the old right-button WinForms strip is gone).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button is WinForms.MouseButtons.Left or WinForms.MouseButtons.Right) OpenPanel();
        };
        _icon.Visible = true;
    }

    /// <summary>Builds fresh panel data and shows the panel at the click point.</summary>
    private void OpenPanel()
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _panel ??= new TrayPanelWindow
                    {
                        ShowInTaskbar = false,
                        // no Owner: the panel must open even while the main window is hidden in the tray
                    };
                    // wire the panel's actions through the same events the old menu used
                    _panel.ToggleConnectionRequested -= OnToggle;
                    _panel.ConnectProfileRequested -= OnConnectProfile;
                    _panel.ShowRequested -= OnShow;
                    _panel.ExitRequested -= OnExit;
                    _panel.WidgetToggleRequested -= OnWidget;
                    _panel.WidgetToggleRequested += OnWidget;
                    _panel.ToggleConnectionRequested += OnToggle;
                    _panel.ConnectProfileRequested += OnConnectProfile;
                    _panel.ShowRequested += OnShow;
                    _panel.ExitRequested += OnExit;

                    var cursor = WinForms.Cursor.Position; // the click happened on the icon, right here
                    _panel.OpenAt(cursor, "KOALIX — " + _stateText, _connected, BuildRows());
                }
                catch (Exception ex) { StartupLog.Trace("tray panel: " + ex.Message); }
            });
        }
        catch (Exception ex) { StartupLog.Trace("tray panel dispatch: " + ex.Message); }
    }

    private void OnToggle() => ToggleConnectionRequested?.Invoke();
    private void OnShow() => ShowRequested?.Invoke();
    private void OnExit() => ExitRequested?.Invoke();
    private void OnWidget() => WidgetToggleRequested?.Invoke();
    /// <summary>v0.4.38 — the panel's widget button: show / hide the floating mini widget.</summary>
    public event Action? WidgetToggleRequested;
    private void OnConnectProfile(long id) => ConnectProfileRequested?.Invoke(id);

    /// <summary>The fastest enabled profiles as panel rows: flag (from the server-name country hints),
    /// colour-coded ping, engine chip, active highlight.</summary>
    private List<TrayPanelWindow.TrayServerRow> BuildRows()
    {
        var rows = new List<TrayPanelWindow.TrayServerRow>();
        try
        {
            var activeId = TryGetActiveProfileId();
            foreach (var p in _profiles.All()
                         .Where(p => p.Enabled)
                         .OrderBy(p => p.LastLatencyMs ?? double.MaxValue)
                         .Take(8))
            {
                var ping = p.LastLatencyMs;
                var pingText = ping is null ? "—" : Math.Round(ping.Value) + " ms";
                var pingLevel = ping is null ? 2 : ping.Value < 150 ? 0 : ping.Value < 400 ? 1 : 2;
                rows.Add(new TrayPanelWindow.TrayServerRow
                {
                    Id = p.Id,
                    Name = p.Name,
                    PingText = pingText,
                    PingLevel = pingLevel,
                    FlagCode = Koalix.Infrastructure.ServerCountry.FromName(p.Name) ?? "",
                    EngineText = p.Engine.ToString(),
                    IsActive = p.Id == activeId,
                });
            }
        }
        catch { /* the panel must open even if the repository hiccups */ }
        return rows;
    }

    /// <summary>Resolves the active profile id through the app's connection manager (avoids a DI cycle — may be null early).</summary>
    private static long? TryGetActiveProfileId()
    {
        try { return AppHost.Get<ConnectionManager>().Active?.Profile.Id; }
        catch { return null; }
    }

    /// <summary>Called by the dashboard on every connection-state change so the panel header stays live.</summary>
    public void SetMenuState(string stateText, bool connected)
    {
        _stateText = stateText;
        _connected = connected;
    }

    private string _statusText = "KOALIX";
    private int _pingLevel = -1;
    private string _pingSuffix = "";

    public void SetStatus(string text)
    {
        _statusText = text;
        ApplyTooltip();
    }

    private void ApplyTooltip()
    {
        if (_icon is null) return;
        var t = _statusText + _pingSuffix;
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    /// <summary>v0.4.36 — while connected, the tray dot follows the live ping (green &lt;150 ms, amber &lt;400 ms,
    /// red above or no answer) and the tooltip shows the number. Cosmetic: never throws.</summary>
    public void SetPing(double? ms)
    {
        try
        {
            if (_icon is null || !_connected) return;
            var level = ms is null ? 2 : ms.Value < 150 ? 0 : ms.Value < 400 ? 1 : 2;
            _pingSuffix = ms is null ? " · — ms" : $" · {Math.Round(ms.Value)} ms";
            ApplyTooltip();
            if (level == _pingLevel) return;
            _pingLevel = level;
            _baseIcon ??= SystemIcons.Application;
            _levelIcons[level] ??= Overlay(_baseIcon, LevelColor(level));
            _icon.Icon = _levelIcons[level];
        }
        catch (Exception ex) { try { StartupLog.Trace("tray ping: " + ex.Message); } catch { } }
    }

    private readonly System.Drawing.Icon?[] _levelIcons = new System.Drawing.Icon?[3];
    private static Color LevelColor(int level) => level switch
    {
        0 => Color.FromArgb(0x2E, 0xE0, 0x8A),
        1 => Color.FromArgb(0xF5, 0xB0, 0x2E),
        _ => Color.FromArgb(0xEF, 0x44, 0x44),
    };
    public void Notify(string title, string text, ToastKind kind = ToastKind.Info) =>
        _icon?.ShowBalloonTip(3500, title, string.IsNullOrEmpty(text) ? " " : text,
            kind switch { ToastKind.Error => WinForms.ToolTipIcon.Error, ToastKind.Warning => WinForms.ToolTipIcon.Warning, _ => WinForms.ToolTipIcon.Info });

    /// <summary>Swap the tray icon tint by state (connected icon gets a green dot overlay).
    /// v0.4.26 — pure cosmetics must NEVER throw into the connection flow: this runs inside the
    /// state-machine event chain, and one exception here tears down a healthy tunnel (the 0.4.25
    /// «هیچ کانفیگی وصل نمیشه» incident). Any failure is logged and the previous icon stays.</summary>
    public void SetConnected(bool connected)
    {
        try
        {
            if (_icon is null) return;
            _baseIcon ??= SystemIcons.Application; // belt for the suspenders — never hand a null Icon to NotifyIcon
            if (connected)
            {
                if (_pingLevel < 0)
                {
                    _connectedIcon ??= Overlay(_baseIcon, LevelColor(0));
                    _icon.Icon = _connectedIcon;
                }
            }
            else
            {
                _icon.Icon = _baseIcon; // (the old code assigned null here — the tray icon silently vanished)
                _pingLevel = -1; _pingSuffix = ""; ApplyTooltip();
            }
        }
        catch (Exception ex)
        {
            try { StartupLog.Trace("tray icon swap: " + ex.Message); } catch { }
        }
        SetMenuState(_stateText, connected);
    }

    private System.Drawing.Icon? _baseIcon, _connectedIcon;
    private static System.Drawing.Icon Overlay(System.Drawing.Icon baseIcon, Color dotColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var src = new Icon(baseIcon, 32, 32);
            g.DrawIcon(src, new Rectangle(0, 0, 32, 32));
            using var ring = new SolidBrush(Color.White);
            using var dot = new SolidBrush(dotColor);
            using var ringPath = RoundedPath(new Rectangle(17, 17, 15, 15), 4);
            using var dotPath = RoundedPath(new Rectangle(19, 19, 11, 11), 3);
            g.FillPath(ring, ringPath);
            g.FillPath(dot, dotPath);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        try { _panel?.Close(); } catch { }
        _panel = null;
        if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
