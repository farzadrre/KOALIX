using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Koalix.App.Controls;
using Koalix.App.Services;
using Koalix.App.ViewModels;
using Koalix.Core;
using Koalix.Infrastructure;

namespace Koalix.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private Storyboard? _aurora;
    // v0.4.23 — theme cross-fade state (the old look snapshot fades out over the new one) + focus overlay.
    private AppTheme _themeMode = AppTheme.Dark;
    private bool _themeFading;
    /// <summary>Set when the normal visual layer failed once: no backdrop material, no animations.</summary>
    public static bool SafeMode { get; set; }

    /* ───── Global hotkey Ctrl+Alt+K (v0.4.11): connect/disconnect without opening the window ───── */
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const uint ModAltControl = 0x0001 | 0x0002; // MOD_ALT | MOD_CONTROL
    private const uint VkK = 0x4B;
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggleId = 0x4B01;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _hotkeyOn;

    public MainWindow()
    {
        InitializeComponent();
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/KOALIX;component/Assets/koalix.png")); }
        catch (Exception ex) { StartupLog.Trace("window icon: " + ex.Message); }
        SourceInitialized += (_, _) =>
        {
            try
            {
                var s = AppHost.Get<SettingsRepository>();
                AppHost.Get<ThemeService>().ApplyBackdrop(this, SafeMode ? Backdrop.None : Enum.TryParse<Backdrop>(s.Get("ui.backdrop"), out var b) ? b : Backdrop.Mica);
            }
            catch (Exception ex) { StartupLog.Trace("backdrop: " + ex.Message); }
        };
        SourceInitialized += (_, _) => RegisterGlobalHotkey();
        Closed += (_, _) => UnregisterGlobalHotkey();
        Loaded += (_, _) =>
        {
            StartupLog.Trace("main window loaded");
            try { StartAurora(); Fx.SlideIn(PageHost); } catch (Exception ex) { StartupLog.Trace("aurora: " + ex); Aurora.Visibility = Visibility.Collapsed; }
        };
        // v0.4.22 — the first-run starter tour: opens itself exactly once (ui.onboarded), unless we're in safe mode.
        Loaded += (_, _) =>
        {
            try
            {
                if (!SafeMode && AppHost.Get<SettingsRepository>().Get("ui.onboarded") is null) ShowOnboarding();
            }
            catch (Exception ex) { StartupLog.Trace("onboarding: " + ex.Message); }
        };
        Onboarding.Closed += CloseOnboarding;
        Onboarding.EnginesRequested += () =>
        {
            CloseOnboarding();
            if (DataContext is MainViewModel mvm) mvm.Selected = mvm.Nav.First(n => n.Key == "Nav.Settings");
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.State)) { UpdateState(vm.State); MaybeConnectHalo(vm.State); }
                if (e.PropertyName == nameof(MainViewModel.Selected)) Fx.SlideIn(PageHost);
            };
            try { vm.Selected.Page.OnActivated(); } catch (Exception ex) { StartupLog.Trace("page activation: " + ex); }
            UpdateState(vm.State);
        };
        Toasts.Raised += ShowToast;
        Toasts.HistoryChanged += UpdateNotifBadge;
        _toastTimer.Tick += (_, _) => HideToast();
        ThemeService.Changed += () => { if (DataContext is MainViewModel vm) UpdateState(vm.State); OnThemeSwapped(); };
        ThemeService.PreviewApply += OnThemePreviewApply;
        _themeMode = AppHost.Get<ThemeService>().Mode;
        UpdateThemeGlyph(animate: false);
        IsVisibleChanged += (_, _) => { if (IsVisible) _aurora?.Resume(); else _aurora?.Pause(); };

        // v0.4.20: drag-and-drop IMPORT — drop share links, subscription URLs or a config file anywhere on the
        // window and it goes through the same pipeline as the Tunnels page's Import button.
        AllowDrop = true;
        DragOver += OnImportDragOver;
        Drop += OnImportDrop;
    }

    /* ───── Onboarding tour (v0.4.22) — first-run guide, replayable from Settings ───── */

    /// <summary>Shows the starter tour overlay (used by the first-run check and Settings ← دیدن دوبارهٔ راهنمای شروع).</summary>
    public void ShowOnboarding()
    {
        OnboardingOverlay.Visibility = Visibility.Visible;
        OnboardingOverlay.Focus();
        try { if (Fx.Enabled) Fx.SlideIn(Onboarding, 18, 0.35); } catch { }
    }

    private void CloseOnboarding()
    {
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        try { AppHost.Get<SettingsRepository>().Set("ui.onboarded", "1"); } catch (Exception ex) { StartupLog.Trace("onboarding save: " + ex.Message); }
    }

    private void OnboardingOverlay_KeyDown(object sender, KeyEventArgs e) => Onboarding.HandleKey(e);

    /* ───── Theme circular wave (v0.4.24) — «تعویض تم تیره/روشن با موج دایره‌ای که از دکمهٔ خورشید پخش می‌شود» ───── */

    /// <summary>Root-coordinate centre of the sun button, captured on click BEFORE the theme flips —
    /// the circular wave grows from exactly there. Null = unknown origin (palette page change): plain cross-fade.</summary>
    private Point? _themeWaveOrigin;

    /// <summary>Click fires before the command, so the origin is safely stored before <c>Apply</c> snapshots.</summary>
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var p = ThemeButton.TransformToVisual(Root).Transform(new Point(ThemeButton.ActualWidth / 2, ThemeButton.ActualHeight / 2));
            _themeWaveOrigin = p;
        }
        catch { _themeWaveOrigin = null; }
    }

    /// <summary>v0.4.32: true while the last reveal was a light/dark flip (vs. an accent-only change).</summary>
    private bool _revealIsMode;
    private DateTime _lastReveal = DateTime.MinValue;
    private EllipseGeometry? _revealHole;

    /// <summary>Fires BEFORE the theme resources flip: snapshot the current look so the reveal can uncover the new one.
    /// v0.4.32: also for accent-colour changes (the reveal grows from wherever the user clicked the swatch).</summary>
    private void OnThemePreviewApply(bool modeFlip)
    {
        try
        {
            if (SafeMode || !Fx.Enabled) return;
            if (Root.ActualWidth < 60 || Root.ActualHeight < 60 || !IsVisible) return;
            // rapid accent changes (typing a hex, arrowing through swatches): let them apply live instead of
            // stacking snapshots — a reveal that is still running simply keeps going.
            if (!modeFlip && (DateTime.UtcNow - _lastReveal).TotalSeconds < 0.45) return;
            var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var w = (int)Math.Ceiling(Root.ActualWidth * dpi.M11);
            var h = (int)Math.Ceiling(Root.ActualHeight * dpi.M22);
            if (w <= 0 || h <= 0) return;
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.M11, 96 * dpi.M22, PixelFormats.Pbgra32);
            ThemeOverlay.Visibility = Visibility.Collapsed; // never photograph leftovers of a previous reveal
            rtb.Render(Root);
            rtb.Freeze();
            // v0.4.34 — the real mirror fix (0.4.33's Image FlowDirection alone did nothing): RenderTargetBitmap
            // renders an RTL Root in its LOGICAL space, i.e. the window's own mirror is not applied, so in Persian the
            // snapshot itself is mirrored (text backwards, sidebar on the wrong side). Flip it back horizontally —
            // verified with a screen-capture harness: snapshot over live UI is pixel-identical; the LTR Image + the
            // mirrored hole centre in OnThemeSwapped put the reveal circle exactly under the click.
            BitmapSource snap = rtb;
            if (Root.FlowDirection == FlowDirection.RightToLeft)
            {
                var flipped = new TransformedBitmap(rtb, new ScaleTransform(-1, 1));
                flipped.Freeze();
                snap = flipped;
            }
            ThemeSnapshot.Source = snap;
            ThemeSnapshot.Clip = null;
            ThemeSnapshot.BeginAnimation(OpacityProperty, null);
            ThemeSnapshot.Opacity = 1;
            ThemeOverlay.BeginAnimation(OpacityProperty, null);
            ThemeOverlay.Opacity = 1;
            ThemeRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThemeRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThemeRing.BeginAnimation(OpacityProperty, null);
            ThemeWave.Visibility = Visibility.Collapsed;
            ThemeRing.Visibility = Visibility.Collapsed;
            ThemeOverlay.Visibility = Visibility.Visible;
            // origin: the sun button for a mode flip; otherwise the mouse (= the swatch the user just clicked)
            if (_themeWaveOrigin is null)
            {
                var m = Mouse.GetPosition(Root);
                if (m.X >= 0 && m.Y >= 0 && m.X <= Root.ActualWidth && m.Y <= Root.ActualHeight) _themeWaveOrigin = m;
            }
            _revealIsMode = modeFlip;
            _themeFading = true;
        }
        catch (Exception ex) { StartupLog.Trace("theme snapshot: " + ex.Message); _themeFading = false; }
    }

    /// <summary>Fires AFTER the swap: a true circular reveal — the OLD look (snapshot) gets a growing hole cut into it,
    /// so the real, live NEW theme shows through from the click point outward, with a ring in the new accent riding
    /// the edge. Mode flips also spin the sun/moon glyph.</summary>
    private void OnThemeSwapped()
    {
        _themeMode = AppHost.Get<ThemeService>().Mode;
        UpdateThemeGlyph(animate: _themeFading && _revealIsMode);
        if (!_themeFading) return;
        _lastReveal = DateTime.UtcNow;
        var token = ++_revealToken;
        try
        {
            var W = Root.ActualWidth; var H = Root.ActualHeight;
            var origin = _themeWaveOrigin ?? new Point(W / 2, 24); // unknown origin → top-centre
            _themeWaveOrigin = null;
            var maxR = new[]
            {
                Math.Sqrt(Math.Pow(origin.X, 2) + Math.Pow(origin.Y, 2)),
                Math.Sqrt(Math.Pow(W - origin.X, 2) + Math.Pow(origin.Y, 2)),
                Math.Sqrt(Math.Pow(origin.X, 2) + Math.Pow(H - origin.Y, 2)),
                Math.Sqrt(Math.Pow(W - origin.X, 2) + Math.Pow(H - origin.Y, 2)),
            }.Max() * 1.02;

            var dur = TimeSpan.FromSeconds(_revealIsMode ? 0.7 : 0.55);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            // the snapshot Image is LeftToRight (see XAML) — in RTL its x axis runs opposite to Root's, so mirror the
            // hole's centre into image space; the ring lives in the overlay grid and keeps Root's coordinates.
            var holeCentre = Root.FlowDirection == FlowDirection.RightToLeft ? new Point(W - origin.X, origin.Y) : origin;
            _revealHole = new EllipseGeometry(holeCentre, 0, 0);
            ThemeSnapshot.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W + 2, H + 2)), _revealHole);
            var grow = new DoubleAnimation(0, maxR, dur) { EasingFunction = ease };
            grow.Completed += (_, _) =>
            {
                if (token != _revealToken) return; // a newer reveal took over
                ThemeOverlay.Visibility = Visibility.Collapsed;
                ThemeRing.Visibility = Visibility.Collapsed;
                ThemeSnapshot.Clip = null;
                ThemeSnapshot.Source = null; // release the bitmap
            };
            _revealHole.BeginAnimation(EllipseGeometry.RadiusXProperty, grow);
            _revealHole.BeginAnimation(EllipseGeometry.RadiusYProperty, grow);

            // the accent ring rides exactly on the reveal edge (same duration + easing), fading as it leaves
            ThemeRing.Width = ThemeRing.Height = maxR * 2;
            ThemeRing.Margin = new Thickness(origin.X - maxR, origin.Y - maxR, 0, 0);
            ThemeRing.HorizontalAlignment = HorizontalAlignment.Left;
            ThemeRing.VerticalAlignment = VerticalAlignment.Top;
            ThemeRing.Visibility = Visibility.Visible;
            var ringGrow = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            ThemeRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringGrow);
            ThemeRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringGrow);
            var ringFade = new DoubleAnimationUsingKeyFrames { Duration = dur };
            ringFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            ringFade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.12)));
            ringFade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            ThemeRing.BeginAnimation(OpacityProperty, ringFade);
        }
        catch (Exception ex)
        {
            StartupLog.Trace("theme reveal: " + ex.Message);
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3));
            fade.Completed += (_, _) => { if (token == _revealToken) ThemeOverlay.Visibility = Visibility.Collapsed; };
            ThemeSnapshot.BeginAnimation(OpacityProperty, fade);
        }
        finally { _themeFading = false; }
    }

    private int _revealToken;

    /* ───── Connect halo (v0.4.35) ───── */

    private bool _haloWasUp;
    private int _haloToken;

    /// <summary>Plays the halo once per real transition: down → Connected (outward, green/accent) and
    /// up → Disconnected/Error (inward, red). Reconnect blips inside a live session don't replay it.</summary>
    private void MaybeConnectHalo(ConnectionState s)
    {
        var up = s is ConnectionState.Connected or ConnectionState.Degraded;
        if (up && !_haloWasUp) { _haloWasUp = true; PlayConnectHalo(connected: true); }
        else if (!up && _haloWasUp && s is ConnectionState.Disconnected or ConnectionState.Error) { _haloWasUp = false; PlayConnectHalo(connected: false); }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindVisual<T>(c) is { } deep) return deep;
        }
        return null;
    }

    private static RadialGradientBrush Radial(params (double off, Color c, byte a)[] stops)
    {
        var b = new RadialGradientBrush();
        foreach (var (off, c, a) in stops) b.GradientStops.Add(new GradientStop(Color.FromArgb(a, c.R, c.G, c.B), off));
        b.Freeze();
        return b;
    }

    private void PlayConnectHalo(bool connected)
    {
        try
        {
            if (SafeMode || !Fx.Enabled || !IsVisible || WindowState == WindowState.Minimized) return;
            var W = Root.ActualWidth; var H = Root.ActualHeight;
            if (W < 60 || H < 60) return;
            // origin: the connect orb when the dashboard is showing, else the status chip, else the window centre
            FrameworkElement? anchor = FindVisual<ConnectOrb>(PageHost) is { IsVisible: true, ActualWidth: > 0 } orb ? orb
                                     : StateChip.IsVisible ? StateChip : null;
            var o = anchor is null ? new Point(W / 2, H / 2)
                                   : anchor.TransformToVisual(Root).Transform(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
            var R = new[] { double.Hypot(o.X, o.Y), double.Hypot(W - o.X, o.Y), double.Hypot(o.X, H - o.Y), double.Hypot(W - o.X, H - o.Y) }.Max() * 1.05;

            var main = (Color)FindResource(connected ? "SuccessColor" : "DangerColor");
            var second = connected && TryFindResource("AccentColor") is Color ac ? ac : main;
            ConnectFxWash.Fill = Radial((0, main, 0x00), (0.55, main, connected ? (byte)0x1C : (byte)0x14), (0.9, main, connected ? (byte)0x3A : (byte)0x2A), (1, main, 0x00));
            ConnectFxRing.Fill = Radial((0, main, 0), (0.86, main, 0), (0.95, main, 0xD0), (0.975, main, 0xF0), (1, main, 0));
            ConnectFxRing2.Fill = Radial((0, second, 0), (0.92, second, 0), (0.97, second, 0xB0), (1, second, 0));

            var token = ++_haloToken;
            var dur = TimeSpan.FromSeconds(connected ? 1.05 : 0.85);
            IEasingFunction ease = new CubicEase { EasingMode = connected ? EasingMode.EaseOut : EasingMode.EaseIn };
            var lag = TimeSpan.FromSeconds(0.14);
            ConnectFx.Visibility = Visibility.Visible;
            foreach (var (el, delay, size) in new (System.Windows.Shapes.Ellipse, TimeSpan, double)[] { (ConnectFxWash, TimeSpan.Zero, R), (ConnectFxRing, TimeSpan.Zero, R), (ConnectFxRing2, lag, R * 0.92) })
            {
                el.Width = el.Height = size * 2;
                Canvas.SetLeft(el, o.X - size);
                Canvas.SetTop(el, o.Y - size);
                var st = (ScaleTransform)el.RenderTransform;
                var grow = new DoubleAnimation(connected ? 0.02 : 1, connected ? 1 : 0.02, dur) { BeginTime = delay, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                // fade in fast, hold, fade out at the far end of the travel
                var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = dur };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(connected ? 0.1 : 0.2)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(connected ? 0.55 : 0.75)));
                fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                el.Opacity = 0;
                if (el == ConnectFxRing2)
                    fade.Completed += (_, _) => { if (token == _haloToken) ConnectFx.Visibility = Visibility.Collapsed; };
                el.BeginAnimation(OpacityProperty, fade);
            }
        }
        catch (Exception ex) { StartupLog.Trace("connect halo: " + ex.Message); ConnectFx.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Sun in dark mode (click → light), moon in light mode; a mode flip spins and pops the glyph.</summary>
    private void UpdateThemeGlyph(bool animate)
    {
        try
        {
            ThemeButton.Content = _themeMode == AppTheme.Dark ? "" : "";
            if (!animate || !Fx.Enabled) return;
            var rot = new RotateTransform();
            var pop = new ScaleTransform();
            ThemeButton.RenderTransformOrigin = new Point(0.5, 0.5);
            ThemeButton.RenderTransform = new TransformGroup { Children = { pop, rot } };
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-180, 0, TimeSpan.FromSeconds(0.6)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
            var p = new DoubleAnimation(0.6, 1, TimeSpan.FromSeconds(0.5)) { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 } };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, p);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, p);
        }
        catch (Exception ex) { StartupLog.Trace("theme glyph: " + ex.Message); }
    }

    /* ───── Gaming focus mode (v0.4.23, suggestion #7) ───── */

    /// <summary>Fullscreen minimal surface: status + live ping + disconnect. Nothing else. Esc exits.</summary>
    /// <summary>v0.4.35 («حالت فوکوس قشنگ‌تر، با انیمیشن و موشن خاص موقع زدن»): focus mode opens as a circular IRIS
    /// from the click point, the content cascades in (status, detail, the latency number zooming in with a spring,
    /// the pills, the button) and a sonar keeps pinging behind the ping number. Exit closes the iris again.</summary>
    public void ShowFocus()
    {
        var wasOpen = FocusOverlay.Visibility == Visibility.Visible;
        FocusOverlay.Visibility = Visibility.Visible;
        FocusOverlay.Focus();
        if (!wasOpen) SoundService.Play(SoundService.SoundEvent.FocusOn); // v0.4.36 (silent by default)
        if (DataContext is MainViewModel vm) UpdateState(vm.State);
        var token = ++_focusToken;
        try
        {
            FocusOverlay.UpdateLayout();
            var W = FocusOverlay.ActualWidth; var H = FocusOverlay.ActualHeight;
            var o = Mouse.GetPosition(FocusOverlay);
            if (o.X < 0 || o.Y < 0 || o.X > W || o.Y > H) o = new Point(W / 2, H / 2);
            _focusOrigin = o;
            SetSonar(Fx.Enabled && !SafeMode);
            if (!Fx.Enabled || SafeMode || wasOpen || W < 60) { FocusOverlay.Clip = null; FocusOverlay.Opacity = 1; return; }

            FocusOverlay.BeginAnimation(OpacityProperty, null);
            FocusOverlay.Opacity = 1;
            var R = Corners(o, W, H);
            var iris = new EllipseGeometry(o, 0, 0);
            FocusOverlay.Clip = iris;
            var open = new DoubleAnimation(0, R, TimeSpan.FromSeconds(0.62)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut } };
            open.Completed += (_, _) => { if (token == _focusToken) FocusOverlay.Clip = null; };
            iris.BeginAnimation(EllipseGeometry.RadiusXProperty, open);
            iris.BeginAnimation(EllipseGeometry.RadiusYProperty, open);

            // the cascade: each block rises + fades in, 70 ms apart, after the iris is a third open
            var i = 0;
            foreach (var el in new FrameworkElement[] { FocusHead, FocusDetail, FocusPing, FocusPills, FocusConnect })
            {
                var begin = TimeSpan.FromSeconds(0.2 + 0.07 * i++);
                var tt = new TranslateTransform(0, 26);
                var sc = new ScaleTransform(1, 1);
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                el.RenderTransform = new TransformGroup { Children = { sc, tt } };
                el.Opacity = 0;
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.36)) { BeginTime = begin });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(26, 0, TimeSpan.FromSeconds(0.55)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                if (el == FocusPing)
                {
                    var zoom = new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(0.8)) { BeginTime = begin, EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 } };
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
                }
            }
        }
        catch (Exception ex) { StartupLog.Trace("focus open: " + ex.Message); FocusOverlay.Clip = null; }
    }

    private int _focusToken;
    private Point _focusOrigin;

    private static double Corners(Point o, double W, double H) =>
        new[] { double.Hypot(o.X, o.Y), double.Hypot(W - o.X, o.Y), double.Hypot(o.X, H - o.Y), double.Hypot(W - o.X, H - o.Y) }.Max() * 1.02;

    /// <summary>The sonar behind the latency number: three rings pinging outward (staggered) + a breathing glow.</summary>
    private void SetSonar(bool on)
    {
        var rings = new[] { FocusRing1, FocusRing2, FocusRing3 };
        var glow = (ScaleTransform)FocusGlow.RenderTransform;
        if (!on)
        {
            foreach (var r in rings)
            {
                r.BeginAnimation(OpacityProperty, null); r.Opacity = 0;
                var s = (ScaleTransform)r.RenderTransform;
                s.BeginAnimation(ScaleTransform.ScaleXProperty, null); s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            glow.BeginAnimation(ScaleTransform.ScaleXProperty, null); glow.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var period = TimeSpan.FromSeconds(3);
        for (var i = 0; i < rings.Length; i++)
        {
            var begin = TimeSpan.FromSeconds(i * 1.0);
            var s = (ScaleTransform)rings[i].RenderTransform;
            var grow = new DoubleAnimation(0.4, 1.7, period) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            rings[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 0, period) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
        }
        var breathe = new DoubleAnimation(0.9, 1.12, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        glow.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        glow.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    /// <summary>Exit: the iris closes back onto where focus mode was opened, then the overlay collapses.</summary>
    public void CloseFocus()
    {
        if (FocusOverlay.Visibility != Visibility.Visible) return;
        var token = ++_focusToken;
        SetSonar(false);
        try
        {
            var W = FocusOverlay.ActualWidth; var H = FocusOverlay.ActualHeight;
            if (!Fx.Enabled || SafeMode || W < 60) { FocusOverlay.Visibility = Visibility.Collapsed; FocusOverlay.Clip = null; return; }
            var o = _focusOrigin.X > 0 || _focusOrigin.Y > 0 ? _focusOrigin : new Point(W / 2, H / 2);
            var iris = new EllipseGeometry(o, Corners(o, W, H), Corners(o, W, H));
            FocusOverlay.Clip = iris;
            var close = new DoubleAnimation(Corners(o, W, H), 0, TimeSpan.FromSeconds(0.45)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            close.Completed += (_, _) =>
            {
                if (token != _focusToken) return; // re-opened mid-close
                FocusOverlay.Visibility = Visibility.Collapsed;
                FocusOverlay.Clip = null;
            };
            iris.BeginAnimation(EllipseGeometry.RadiusXProperty, close);
            iris.BeginAnimation(EllipseGeometry.RadiusYProperty, close);
        }
        catch (Exception ex) { StartupLog.Trace("focus close: " + ex.Message); FocusOverlay.Visibility = Visibility.Collapsed; FocusOverlay.Clip = null; }
    }

    private void FocusOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseFocus(); e.Handled = true; }
    }

    private void FocusClose_Click(object sender, RoutedEventArgs e) => CloseFocus();

    /* ───── Drag-to-import (v0.4.20) ───── */

    private void OnImportDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnImportDrop(object sender, DragEventArgs e)
    {
        try
        {
            string? text = null;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                var f = files[0];
                if (new FileInfo(f).Length > 5_000_000) { Toasts.Show(Localization.Loc.I["Profiles.DropTitle"], Localization.Loc.I["Dns.FileTooLarge"], ToastKind.Error); return; }
                text = File.ReadAllText(f);
            }
            else text = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
            if (string.IsNullOrWhiteSpace(text)) return;
            var vm = AppHost.Get<ProfilesViewModel>();
            var added = await vm.ImportDroppedAsync(text);
            if (added > 0)
            {
                // jump to the Tunnels page so the result is visible, with a toast for what happened
                if (DataContext is MainViewModel mvm) mvm.Selected = mvm.Nav.First(n => n.Key == "Nav.Profiles");
                Toasts.Show(Localization.Loc.I["Profiles.DropTitle"], string.Format(Localization.Loc.I["Profiles.DropAdded"], added), ToastKind.Success);
            }
            else Toasts.Show(Localization.Loc.I["Profiles.DropTitle"], vm.StatusMessage ?? "", ToastKind.Warning);
        }
        catch (Exception ex) { Toasts.Show(Localization.Loc.I["Profiles.DropTitle"], Redactor.Redact(ex.Message), ToastKind.Error); }
    }

    /// <summary>Slow drifting background blobs. 24 fps is plenty for this and keeps GPU/CPU use negligible while gaming.</summary>
    public void StartAurora()
    {
        _aurora?.Stop();
        Aurora.Visibility = Fx.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!Fx.Enabled) return;
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        Timeline.SetDesiredFrameRate(sb, 24);
        void Drift(UIElement el, double x, double y, double seconds)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var ax = new DoubleAnimation(0, x, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease };
            var ay = new DoubleAnimation(0, y, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease };
            Storyboard.SetTarget(ax, el); Storyboard.SetTargetProperty(ax, new PropertyPath("RenderTransform.X"));
            Storyboard.SetTarget(ay, el); Storyboard.SetTargetProperty(ay, new PropertyPath("RenderTransform.Y"));
            sb.Children.Add(ax); sb.Children.Add(ay);
        }
        Drift(Blob1, 260, 160, 22); Drift(Blob2, -320, -200, 27); Drift(Blob3, -180, 260, 31);
        sb.Begin(this, true);
        _aurora = sb;
    }

    private Brush StateBrush(ConnectionState s) => (Brush)FindResource(s switch
    {
        ConnectionState.Connected => "SuccessBrush",
        ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnecting => "AccentBrush",
        ConnectionState.Degraded => "WarningBrush",
        ConnectionState.Error => "DangerBrush",
        _ => "SubtleTextBrush",
    });

    private void UpdateState(ConnectionState s)
    {
        var b = StateBrush(s);
        StatusDot.Background = StatusHalo.Background = ChipDot.Background = b;
        FocusDot.Background = b;
        var soft = s switch
        {
            ConnectionState.Connected => "SuccessSoftBrush",
            ConnectionState.Degraded => "WarningSoftBrush",
            ConnectionState.Error => "DangerSoftBrush",
            ConnectionState.Disconnected => "ControlBrush",
            _ => "AccentSoftBrush",
        };
        StatusCard.SetResourceReference(Border.BackgroundProperty, soft);
        StateChip.SetResourceReference(Border.BackgroundProperty, soft);
        StatusCard.BorderBrush = StateChip.BorderBrush = s == ConnectionState.Disconnected ? (Brush)FindResource("BorderBrush") : b;

        // Halo pulse around the dot while busy (fast) or connected (slow heartbeat).
        var st = (ScaleTransform)StatusHalo.RenderTransform;
        var period = s switch { ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnecting => 0.9, ConnectionState.Connected or ConnectionState.Degraded => 2.4, _ => 0 };
        if (period > 0 && Fx.Enabled)
        {
            var d = TimeSpan.FromSeconds(period);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 2.2, d) { RepeatBehavior = RepeatBehavior.Forever });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 2.2, d) { RepeatBehavior = RepeatBehavior.Forever });
            StatusHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, d) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            StatusHalo.BeginAnimation(OpacityProperty, null); StatusHalo.Opacity = 0;
        }
    }

    private void ShowToast(ToastMessage m)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            AppHost.Get<TrayService>().Notify(m.Title, m.Text ?? "", m.Kind);
            return;
        }
        ToastTitle.Text = m.Title;
        ToastText.Text = m.Text ?? "";
        ToastText.Visibility = string.IsNullOrEmpty(m.Text) ? Visibility.Collapsed : Visibility.Visible;
        ToastIcon.Text = m.Glyph;
        ToastIconBg.SetResourceReference(Border.BackgroundProperty, m.Kind switch
        {
            ToastKind.Success => "SuccessBrush", ToastKind.Warning => "WarningBrush", ToastKind.Error => "DangerBrush", _ => "AccentGradientBrush",
        });
        Toast.Visibility = Visibility.Visible;
        Fx.SlideIn(Toast, 24, 0.35);
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(m.Kind == ToastKind.Error ? 8 : 4.5);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var a = new DoubleAnimation(0, TimeSpan.FromSeconds(0.25));
        a.Completed += (_, _) => { if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, a);
    }

    private void Toast_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => HideToast();

    private void StatusCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Selected = vm.Nav[0];
    }

    /* ───── Global hotkey Ctrl+Alt+K (v0.4.11): connect/disconnect without opening the window ───── */
    private void RegisterGlobalHotkey()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
            if (_hotkeyOn) { UnregisterHotKey(_hwnd, HotkeyToggleId); _hotkeyOn = false; }
            if (AppHost.Get<SettingsRepository>().Get("ui.hotkey") == "0") return;
            if (_hwndSource is null)
            {
                _hwndSource = HwndSource.FromHwnd(_hwnd);
                _hwndSource?.AddHook(WndProc);
            }
            _hotkeyOn = RegisterHotKey(_hwnd, HotkeyToggleId, ModAltControl, VkK);
            if (_hotkeyOn) StartupLog.Trace("global hotkey Ctrl+Alt+K registered");
        }
        catch (Exception ex) { StartupLog.Trace("hotkey: " + ex.Message); }
    }

    private void UnregisterGlobalHotkey()
    {
        try { if (_hotkeyOn && _hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyToggleId); } catch { }
        _hotkeyOn = false;
    }

    /// <summary>Live on/off for the Settings toggle — no restart needed (v0.4.11).</summary>
    public void SetHotkey(bool enabled)
    {
        if (enabled) RegisterGlobalHotkey();
        else UnregisterGlobalHotkey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt64() == HotkeyToggleId)
        {
            handled = true;
            try { AppHost.Get<ViewModels.DashboardViewModel>().ConnectCommand.Execute(null); } catch { }
        }
        return IntPtr.Zero;
    }

    /* ───── Command palette (v0.4.3, Ctrl+K) ───── */

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenPalette();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void SearchChip_Click(object sender, RoutedEventArgs e) => OpenPalette();

    private void OpenPalette()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.PaletteQuery = "";
        PalettePopup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            PaletteBox.Focus();
            Keyboard.Focus(PaletteBox);
            if (vm.PaletteResults.Count > 0) PaletteList.SelectedIndex = 0;
        });
    }

    private void PaletteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                PalettePopup.IsOpen = false;
                break;
            case Key.Enter:
                e.Handled = true;
                if (vm.PaletteResults.Count == 0) { PalettePopup.IsOpen = false; break; }
                var idx = PaletteList.SelectedIndex < 0 ? 0 : PaletteList.SelectedIndex;
                PalettePopup.IsOpen = false;
                vm.PaletteResults[Math.Min(idx, vm.PaletteResults.Count - 1)].Run();
                break;
            case Key.Down:
                e.Handled = true;
                if (vm.PaletteResults.Count > 0)
                {
                    PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, vm.PaletteResults.Count - 1);
                    if (PaletteList.SelectedItem is { } item) PaletteList.ScrollIntoView(item);
                }
                break;
            case Key.Up:
                e.Handled = true;
                if (vm.PaletteResults.Count > 0)
                {
                    PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
                    if (PaletteList.SelectedItem is { } item2) PaletteList.ScrollIntoView(item2);
                }
                break;
        }
    }

    private void PaletteList_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement?)e.OriginalSource)?.DataContext is PaletteItem item)
        {
            PalettePopup.IsOpen = false;
            item.Run();
        }
    }

    /* ───── Notification center (v0.4.3) ───── */

    private void Bell_Click(object sender, RoutedEventArgs e)
    {
        NotifPopup.IsOpen = !NotifPopup.IsOpen;
        if (NotifPopup.IsOpen) Toasts.MarkAllRead();
        UpdateNotifBadge();
    }

    private void NotifClear_Click(object sender, RoutedEventArgs e)
    {
        Toasts.ClearHistory();
        UpdateNotifBadge();
    }

    private void NotifDiagnose_Click(object sender, RoutedEventArgs e)
    {
        NotifPopup.IsOpen = false;
        if (DataContext is MainViewModel vm) vm.ExportDiagnostics();
    }

    private void UpdateNotifBadge() => NotifBadge.Visibility = Toasts.Unread > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // WindowChrome: compensate the off-screen border when maximized.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AppHost.Get<SettingsRepository>().Get("ui.tray") != "0")
        {
            e.Cancel = true; Hide();
            if (AppHost.Get<SettingsRepository>().Get("ui.trayHintShown") != "1")
            {
                AppHost.Get<SettingsRepository>().Set("ui.trayHintShown", "1");
                AppHost.Get<TrayService>().Notify("KOALIX", Localization.Loc.I["Tray.StillRunning"], ToastKind.Info);
            }
            return;
        }
        e.Cancel = true;
        _ = App.Current.ExitAsync();
    }
}
