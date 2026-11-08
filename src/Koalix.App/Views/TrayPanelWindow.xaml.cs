using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Koalix.App.Services;

namespace Koalix.App.Views;

/// <summary>
/// v0.4.24 — the modern tray panel («پنل مدرن Tray»: شیشه‌ای، با فونت IRANSansX، دکمهٔ اتصال، فهرست سرورها با
/// پینگ رنگی و پرچم، و انیمیشن باز شدن). A borderless WPF window that opens at the tray icon: glass card
/// (acrylic via DWM on Windows 11, translucent layered card elsewhere), one big connect button, the live server
/// list with flags and colour-coded pings, and open/close slide+fade animations. Any action — or a click
/// anywhere else (Deactivated), or Esc — dismisses it.
/// </summary>
public partial class TrayPanelWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>Raised for the big button — same semantics as the old menu's «اتصال / قطع».</summary>
    public event Action? ToggleConnectionRequested;
    /// <summary>Raised when a server row is clicked (profile id).</summary>
    public event Action<long>? ConnectProfileRequested;
    public event Action? ShowRequested;
    public event Action? ExitRequested;
    /// <summary>v0.4.38 — show / hide the floating widget.</summary>
    public event Action? WidgetToggleRequested;

    /// <summary>One server row of the panel list (built by <see cref="TrayService"/> on every open).</summary>
    public sealed class TrayServerRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string PingText { get; init; } = "—";
        /// <summary>0 = fast (green), 1 = okay (amber), 2 = slow/unmeasured (subtle).</summary>
        public int PingLevel { get; init; }
        public string FlagCode { get; init; } = "";
        public string EngineText { get; init; } = "";
        public bool IsActive { get; init; }
    }

    public static readonly DependencyProperty ConnectLabelProperty =
        DependencyProperty.Register(nameof(ConnectLabel), typeof(string), typeof(TrayPanelWindow), new PropertyMetadata(""));
    /// <summary>Text of the big button — «اتصال» / «قطع».</summary>
    public string ConnectLabel { get => (string)GetValue(ConnectLabelProperty); set => SetValue(ConnectLabelProperty, value); }

    public static readonly DependencyProperty ConnectGlyphProperty =
        DependencyProperty.Register(nameof(ConnectGlyph), typeof(string), typeof(TrayPanelWindow), new PropertyMetadata("play"));
    /// <summary>"play" or "stop" — flips the big button's icon.</summary>
    public string ConnectGlyph { get => (string)GetValue(ConnectGlyphProperty); set => SetValue(ConnectGlyphProperty, value); }

    private bool _closing;
    private readonly TranslateTransform _slide = new();

    public TrayPanelWindow()
    {
        InitializeComponent();
        // v0.4.24 — the OPEN ANIMATION: the card rises ~10px into place while fading in (0.16 s).
        Card.RenderTransform = _slide;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _closing) return;
            Opacity = 0;
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _slide.Y = 10;
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromSeconds(0.16)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.14)));
        };
    }

    /// <summary>Shows the panel near the tray click point with fresh state + servers, clamped to the work area.</summary>
    public void OpenAt(System.Drawing.Point trayPoint, string stateText, bool connected, IReadOnlyList<TrayServerRow> servers)
    {
        StateText.Text = stateText;
        StateDot.SetResourceReference(Border.BackgroundProperty, connected ? "SuccessBrush" : "SubtleTextBrush");
        ConnectLabel = Localization.Loc.I[connected ? "Tray.PanelDisconnect" : "Tray.PanelConnect"];
        ConnectGlyph = connected ? "stop" : "play";

        ServerList.ItemsSource = servers;
        var hasRows = servers.Count > 0;
        ServersTitle.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        ServerList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        NoServers.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;

        _closing = false;
        Opacity = 0;
        Show();
        UpdateLayout(); // force SizeToContent to produce the real height before we position
        PositionNear(trayPoint);
        try { ApplyGlass(); } catch { /* older Windows: the solid themed card is already fine */ }
        Activate(); // focus enables the Deactivated-driven dismissal
    }

    /// <summary>Places the panel beside the tray point (left of the icon, above the taskbar — flipped at the
    /// screen edges), clamped to the monitor work area. The WinForms point is in physical pixels; the window
    /// works in DIPs, so it is converted with the primary-screen DPI.</summary>
    private void PositionNear(System.Drawing.Point trayPointPx)
    {
        double dip = 96;
        try { using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero); dip = g.DpiX; } catch { }
        var px = trayPointPx.X * 96.0 / dip;
        var py = trayPointPx.Y * 96.0 / dip;

        var work = SystemParameters.WorkArea;
        var h = ActualHeight < 60 ? 340 : ActualHeight; // first open hasn't measured yet — assume a typical height
        double x = px - Width - 14;                    // default: panel to the LEFT of the tray icon
        if (x < work.Left) x = px + 14;                // icon near the screen's left edge → panel to the RIGHT
        double y = py - h - 14;                        // default: grow upward from a bottom taskbar
        if (y < work.Top) y = py + 16;                 // taskbar at the top → open downward
        if (x + Width > work.Right) x = work.Right - Width - 8;
        if (y + h > work.Bottom) y = work.Bottom - h - 8;
        if (x < work.Left) x = work.Left + 8;
        if (y < work.Top) y = work.Top + 8;
        Left = x;
        Top = y;
    }

    /// <summary>Windows 11: native rounded corners + dark title attribute + acrylic backdrop with a translucent
    /// card tint on top (real glass). Everywhere else the layered translucent card from the XAML stands as-is.</summary>
    private void ApplyGlass()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = AppHost.Get<ThemeService>().Mode == AppTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            int type = 3; // DWMSBT_TRANSIENTWINDOW (acrylic)
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4) == 0 &&
                Application.Current.TryFindResource("WindowColor") is Color baseC)
            {
                var tint = new SolidColorBrush(Color.FromArgb(0xC2, baseC.R, baseC.G, baseC.B));
                tint.Freeze();
                Card.Background = tint; // a tint, not a wall — the acrylic shows through
            }
        }
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        ToggleConnectionRequested?.Invoke();
        Dismiss();
    }

    private void OnServerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrayServerRow row)
        {
            ConnectProfileRequested?.Invoke(row.Id);
            Dismiss();
        }
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        ShowRequested?.Invoke();
        Dismiss();
    }

    private void OnWidget(object sender, RoutedEventArgs e)
    {
        WidgetToggleRequested?.Invoke();
        Dismiss();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
        Dismiss();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
    }

    private void OnDeactivated(object sender, EventArgs e) => Dismiss();

    /// <summary>Fade-out (0.12 s) then hide — the panel is reused, never destroyed.</summary>
    public void Dismiss()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.12));
        fade.Completed += (_, _) =>
        {
            // only hide if we're still closing — the user may have re-opened the panel mid-fade
            if (!_closing) return;
            _closing = false;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
