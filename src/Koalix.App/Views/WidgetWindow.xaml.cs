using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Koalix.App.Localization;
using Koalix.App.Services;
using Koalix.App.ViewModels;

namespace Koalix.App.Views;

/// <summary>
/// v0.4.38 — the floating mini widget window. A tool window (no taskbar button, no Alt+Tab entry, never steals
/// focus), topmost, dragged by any point, closed with the hover × or the right-click menu. Optional click-through
/// lets it sit over a game without catching the mouse — it is then turned back via Settings or the tray panel.
/// All state lives in <see cref="WidgetService"/>; this class only renders and forwards the user's choices.
/// </summary>
public partial class WidgetWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    private readonly WidgetService _svc;
    private bool _clickThrough;

    public WidgetWindow(WidgetService svc, WidgetViewModel vm)
    {
        _svc = svc;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => ApplyExStyle();
        Root.MouseEnter += (_, _) => FadeClose(1);
        Root.MouseLeave += (_, _) => FadeClose(0);
        MouseRightButtonUp += (_, e) => { e.Handled = true; OpenMenu(); };
        LocationChanged += (_, _) => { if (IsLoaded) _svc.RememberPosition(Left, Top); };
    }

    private void FadeClose(double to) =>
        CloseX.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.15)));

    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ApplyExStyle();
    }

    private void ApplyExStyle()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        var ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        ex = _clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    /// <summary>Games and other topmost windows can bury us; re-assert the z-order without activating.</summary>
    public void KeepOnTop()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>The OSD mode is a bare translucent black plate (Afterburner look); the others are the themed glass card.</summary>
    public void ApplyMode(string mode)
    {
        if (mode == "osd")
        {
            Card.Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x05, 0x06, 0x0A));
            Card.BorderThickness = new Thickness(0);
            Card.CornerRadius = new CornerRadius(6);
            Card.Padding = new Thickness(9, 6, 24, 6);
            Card.Effect = null;
        }
        else
        {
            Card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
            Card.BorderThickness = new Thickness(1);
            Card.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
            Card.Padding = mode == "compact" ? new Thickness(11, 7, 28, 7) : new Thickness(11, 9, 11, 9);
            var shadow = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = 0.35 };
            if (TryFindResource("ShadowColor") is Color sc) shadow.Color = sc;
            Card.Effect = shadow;
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (_svc.Locked || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => _svc.SetEnabled(false, fromWidget: true);

    private void OpenMenu()
    {
        var L = Loc.I;
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        MenuItem Item(string text, Action act, bool check = false)
        {
            var mi = new MenuItem { Header = (check ? "✓  " : "     ") + text };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        foreach (var m in WidgetViewModel.Modes)
        {
            var mode = m;
            Item(L["Widget.Mode." + mode], () => _svc.SetMode(mode), _svc.Mode == mode);
        }
        menu.Items.Add(new Separator { Margin = new Thickness(6, 3, 6, 3), Opacity = 0.4 });
        Item(L.Format("Widget.OpacityItem", $"{_svc.Opacity * 100:0}%"), _svc.CycleOpacity);
        Item(L["Widget.Lock"], () => _svc.SetLocked(!_svc.Locked), _svc.Locked);
        Item(L["Widget.ClickThrough"], () => _svc.SetClickThrough(true));
        menu.Items.Add(new Separator { Margin = new Thickness(6, 3, 6, 3), Opacity = 0.4 });
        Item(L["Widget.OpenApp"], _svc.RequestOpenApp);
        Item(L["Widget.Close"], () => _svc.SetEnabled(false, fromWidget: true));
        menu.IsOpen = true;
    }
}
