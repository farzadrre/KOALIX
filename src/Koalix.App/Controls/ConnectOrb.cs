using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Koalix.Core;

namespace Koalix.App.Controls;

/// <summary>
/// The big connect button (v0.4.13 → v0.4.41 redesign): a glowing glassmorphic rounded-square tile (or classic circle
/// orb when <see cref="UiPrefs.CircleConnect"/> is enabled).
/// v0.4.41 upgrades:
/// 1) Theme-harmonized glassmorphic palette &amp; smooth 420ms ColorAnimation transitions (replaces the old opaque
///    neon green-to-purple snap that clashed with dark/light glass themes).
/// 2) Live dual sine-wave energy liquid inside the core while connected/connecting whose height and ripple speed
///    respond in real time to instantaneous download traffic (<see cref="Activity"/>).
/// </summary>
public sealed class ConnectOrb : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(ConnectionState), typeof(ConnectOrb),
        new PropertyMetadata(ConnectionState.Disconnected, (d, _) => ((ConnectOrb)d).Refresh(animateColors: true)));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ConnectOrb));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ConnectOrb),
        new PropertyMetadata("", (d, e) => ((ConnectOrb)d)._label.Text = (string)e.NewValue));
    /// <summary>v0.4.41 — normalized live download activity (0..1) driving the sine-wave energy height &amp; ripple speed.</summary>
    public static readonly DependencyProperty ActivityProperty = DependencyProperty.Register(nameof(Activity), typeof(double), typeof(ConnectOrb),
        new PropertyMetadata(0.0));

    public ConnectionState State { get => (ConnectionState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Activity { get => (double)GetValue(ActivityProperty); set => SetValue(ActivityProperty, value); }

    private const double Size = 250, CoreSize = 150, TrackSize = 180;
    private const double CoreRadius = 36, TrackRadius = 46;      // rounded-square mode
    private const double CoreRadiusCircle = CoreSize / 2, TrackRadiusCircle = TrackSize / 2; // circle mode (v0.4.14)
    private static double TrackPerimeter => 4 * (TrackSize - 2 * CurrentTrackRadius) + 2 * Math.PI * CurrentTrackRadius;
    private static double CurrentCoreRadius => UiPrefs.CircleConnect ? CoreRadiusCircle : CoreRadius;
    private static double CurrentTrackRadius => UiPrefs.CircleConnect ? TrackRadiusCircle : TrackRadius;
    private const double ArcThickness = 4, Arc2Thickness = 3;

    private readonly Border _glow;                                   // breathing halo (radial fade)
    private readonly GradientStop _glowStop0 = new(Colors.Transparent, 0.28);
    private readonly GradientStop _glowStop1 = new(Colors.Transparent, 1.0);

    private readonly Rectangle _ring1 = Ring(), _ring2 = Ring();     // expanding pulse rings
    private readonly Rectangle _track = new()
    {
        Width = TrackSize, Height = TrackSize, RadiusX = TrackRadius, RadiusY = TrackRadius, StrokeThickness = 3, IsHitTestVisible = false,
    };
    private readonly Rectangle _arc;                                 // dash spinner (clockwise)
    private readonly Rectangle _arc2;                                // short dash spinner (counter-clockwise)
    /// <summary>One-shot success/disconnect shockwave: expands or implodes around the core.</summary>
    private readonly Rectangle _burst = new()
    {
        Width = CoreSize, Height = CoreSize, RadiusX = CoreRadius, RadiusY = CoreRadius, StrokeThickness = 3,
        IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
    };

    private readonly Border _core = new() { Width = CoreSize, Height = CoreSize, CornerRadius = new CornerRadius(CoreRadius), BorderThickness = new Thickness(1.8) };
    private readonly GradientStop _coreStop0 = new(Color.FromRgb(0x1B, 0x1F, 0x32), 0.0);
    private readonly GradientStop _coreStop1 = new(Color.FromRgb(0x14, 0x17, 0x26), 0.55);
    private readonly GradientStop _coreStop2 = new(Color.FromRgb(0x10, 0x13, 0x20), 1.0);
    private readonly GradientStop _borderStop0 = new(Color.FromRgb(0x7C, 0x5C, 0xFF), 0.0);
    private readonly GradientStop _borderStop1 = new(Color.FromRgb(0x4E, 0x38, 0xB8), 1.0);
    private readonly SolidColorBrush _iconBrush = new(Color.FromRgb(0x7C, 0x5C, 0xFF));
    private readonly SolidColorBrush _labelBrush = new(Colors.White);
    private readonly SolidColorBrush _ringBrush = new(Color.FromRgb(0x7C, 0x5C, 0xFF));

    // v0.4.41 — Live sine-wave energy liquid inside the orb
    private readonly Grid _waveClipHost = new() { Width = CoreSize - 4, Height = CoreSize - 4, IsHitTestVisible = false, Opacity = 0 };
    private readonly RectangleGeometry _waveClipGeo = new(new Rect(0, 0, CoreSize - 4, CoreSize - 4), CoreRadius - 2, CoreRadius - 2);
    private readonly System.Windows.Shapes.Path _waveBack = new() { IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _waveFront = new() { IsHitTestVisible = false };
    private readonly GradientStop _waveBackStop0 = new(Color.FromArgb(0x44, 0x2E, 0xE0, 0x8A), 0);
    private readonly GradientStop _waveBackStop1 = new(Color.FromArgb(0x14, 0x06, 0xB6, 0xD4), 1);
    private readonly GradientStop _waveFrontStop0 = new(Color.FromArgb(0x6E, 0x2E, 0xE0, 0x8A), 0);
    private readonly GradientStop _waveFrontStop1 = new(Color.FromArgb(0x28, 0x06, 0xB6, 0xD4), 1);
    private readonly SolidColorBrush _waveCrestBrush = new(Color.FromArgb(0xB8, 0x6E, 0xF7, 0xB8));
    private readonly Stopwatch _waveClock = Stopwatch.StartNew();
    private double _waveLastTick, _wavePhase, _smoothActivity, _smoothWaveFill;
    private bool _waveTicking;

    private readonly Border _shine = new() { Width = CoreSize - 6, Height = CoreSize - 6, CornerRadius = new CornerRadius(CoreRadius - 4), IsHitTestVisible = false };
    private readonly TextBlock _icon = new() { Text = "\uE7E8", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
    /// <summary>Brief checkmark/cross overlay right after connecting or disconnecting.</summary>
    private readonly TextBlock _flash = new() { Text = "\uE73E", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _label = new() { FontSize = 13.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.NoWrap };
    private readonly ScaleTransform _coreScale = new(1, 1);
    private readonly Grid _coreHost;
    private ConnectionState _last = ConnectionState.Disconnected;

    private static Rectangle Ring() => new()
    {
        Width = CoreSize, Height = CoreSize, RadiusX = CoreRadius, RadiusY = CoreRadius, StrokeThickness = 2,
        IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
    };

    public ConnectOrb()
    {
        Width = Height = Size;
        _icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _flash.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        var glowBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        glowBrush.GradientStops.Add(_glowStop0);
        glowBrush.GradientStops.Add(_glowStop1);
        _glow = new Border
        {
            Width = Size, Height = Size, CornerRadius = new CornerRadius(90), IsHitTestVisible = false,
            Background = glowBrush,
        };

        double arcDash = 0.30 * TrackPerimeter / ArcThickness, arcGap = (TrackPerimeter - arcDash * ArcThickness) / ArcThickness;
        _arc = new Rectangle
        {
            Width = TrackSize, Height = TrackSize, RadiusX = TrackRadius, RadiusY = TrackRadius, StrokeThickness = ArcThickness,
            StrokeDashArray = new DoubleCollection { arcDash, arcGap }, StrokeDashCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false, Opacity = 0,
        };
        double arc2Dash = 0.12 * TrackPerimeter / Arc2Thickness, arc2Gap = (TrackPerimeter - arc2Dash * Arc2Thickness) / Arc2Thickness;
        _arc2 = new Rectangle
        {
            Width = TrackSize, Height = TrackSize, RadiusX = TrackRadius, RadiusY = TrackRadius, StrokeThickness = Arc2Thickness,
            StrokeDashArray = new DoubleCollection { arc2Dash, arc2Gap }, StrokeDashCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false, Opacity = 0,
        };
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentGradientBrush");
        _arc2.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _track.SetResourceReference(Shape.StrokeProperty, "TrackBrush");

        // Core glassmorphic surface brush & border brush (persistent stops for smooth ColorAnimation)
        var coreBg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        coreBg.GradientStops.Add(_coreStop0);
        coreBg.GradientStops.Add(_coreStop1);
        coreBg.GradientStops.Add(_coreStop2);
        _core.Background = coreBg;

        var coreBorder = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        coreBorder.GradientStops.Add(_borderStop0);
        coreBorder.GradientStops.Add(_borderStop1);
        _core.BorderBrush = coreBorder;

        _ring1.Stroke = _ring2.Stroke = _ringBrush;
        _icon.Foreground = _iconBrush;
        _label.Foreground = _labelBrush;

        // Wave layers inside the core
        var waveBackBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        waveBackBrush.GradientStops.Add(_waveBackStop0);
        waveBackBrush.GradientStops.Add(_waveBackStop1);
        _waveBack.Fill = waveBackBrush;

        var waveFrontBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        waveFrontBrush.GradientStops.Add(_waveFrontStop0);
        waveFrontBrush.GradientStops.Add(_waveFrontStop1);
        _waveFront.Fill = waveFrontBrush;
        _waveFront.Stroke = _waveCrestBrush;
        _waveFront.StrokeThickness = 1.25;

        _waveClipHost.Clip = _waveClipGeo;
        _waveClipHost.Children.Add(_waveBack);
        _waveClipHost.Children.Add(_waveFront);

        var iconHost = new Grid();
        iconHost.Children.Add(_icon); iconHost.Children.Add(_flash);
        _flash.Foreground = Brushes.White;
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        content.Children.Add(iconHost); content.Children.Add(_label);

        _shine.Background = new LinearGradientBrush(Color.FromArgb(0x24, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        var contentHost = new Grid();
        contentHost.Children.Add(_waveClipHost);
        contentHost.Children.Add(_shine);
        contentHost.Children.Add(content);
        _core.Child = contentHost;

        _coreHost = new Grid { Width = CoreSize, Height = CoreSize, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _coreScale, Background = Brushes.Transparent };
        _coreHost.Children.Add(_core);
        _coreHost.MouseEnter += (_, _) => ScaleCore(1.045);
        _coreHost.MouseLeave += (_, _) => ScaleCore(1);
        _coreHost.MouseLeftButtonDown += (_, e) => { ScaleCore(0.95); e.Handled = true; };
        _coreHost.MouseLeftButtonUp += (_, e) =>
        {
            ScaleCore(_coreHost.IsMouseOver ? 1.045 : 1);
            if (Command?.CanExecute(null) == true) Command.Execute(null);
            e.Handled = true;
        };

        var root = new Grid();
        foreach (var el in new UIElement[] { _glow, _ring1, _ring2, _track, _arc, _arc2, _coreHost, _burst }) root.Children.Add(el);
        foreach (FrameworkElement el in root.Children) { el.HorizontalAlignment = HorizontalAlignment.Center; el.VerticalAlignment = VerticalAlignment.Center; }
        Content = root;
        FlowDirection = FlowDirection.LeftToRight;
        Loaded += (_, _) =>
        {
            Services.ThemeService.Changed += OnThemeChanged;
            Services.ThemeService.RgbTick += OnRgbTick;
            UiPrefs.Changed += OnThemeChanged;
            Refresh(animateColors: false);
            UpdateWaveLoop();
        };
        Unloaded += (_, _) =>
        {
            Services.ThemeService.Changed -= OnThemeChanged;
            Services.ThemeService.RgbTick -= OnRgbTick;
            UiPrefs.Changed -= OnThemeChanged;
            StopWaveLoop();
        };
        IsVisibleChanged += (_, _) => { Refresh(animateColors: false); UpdateWaveLoop(); };
    }

    private void OnThemeChanged() => Refresh(animateColors: false);

    private void ApplyShape()
    {
        double coreR = CurrentCoreRadius, trackR = CurrentTrackRadius;
        _core.CornerRadius = new CornerRadius(coreR);
        _shine.CornerRadius = new CornerRadius(Math.Max(0, coreR - 4));
        _waveClipGeo.RadiusX = _waveClipGeo.RadiusY = Math.Max(0, coreR - 2);
        _burst.RadiusX = _burst.RadiusY = coreR;
        _ring1.RadiusX = _ring1.RadiusY = _ring2.RadiusX = _ring2.RadiusY = coreR;
        _track.RadiusX = _track.RadiusY = trackR;
        _arc.RadiusX = _arc.RadiusY = _arc2.RadiusX = _arc2.RadiusY = trackR;
        double per = TrackPerimeter;
        double arcDash = 0.30 * per / ArcThickness, arcGap = (per - arcDash * ArcThickness) / ArcThickness;
        _arc.StrokeDashArray = new DoubleCollection { arcDash, arcGap };
        double arc2Dash = 0.12 * per / Arc2Thickness, arc2Gap = (per - arc2Dash * Arc2Thickness) / Arc2Thickness;
        _arc2.StrokeDashArray = new DoubleCollection { arc2Dash, arc2Gap };
    }

    private void ScaleCore(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromSeconds(0.18)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _coreScale.BeginAnimation(ScaleTransform.ScaleXProperty, a); _coreScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private Color C(string key, Color fallback) => TryFindResource(key) switch
    {
        Color c => c,
        SolidColorBrush b => b.Color,
        _ => fallback,
    };

    private static Color Blend(Color baseColor, Color tint, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(baseColor.R + (tint.R - baseColor.R) * amount),
            (byte)Math.Round(baseColor.G + (tint.G - baseColor.G) * amount),
            (byte)Math.Round(baseColor.B + (tint.B - baseColor.B) * amount));
    }

    private static void SetOrAnimate(Animatable target, DependencyProperty prop, Color to, bool animate)
    {
        if (animate && Fx.Enabled)
        {
            var anim = new ColorAnimation(to, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(anim, 30);
            target.BeginAnimation(prop, anim);
        }
        else
        {
            target.BeginAnimation(prop, null);
            target.SetValue(prop, to);
        }
    }

    private void OnRgbTick()
    {
        if (IsVisible && State is ConnectionState.Disconnected or ConnectionState.Connecting or ConnectionState.Reconnecting)
            UpdatePalette(animateColors: false);
    }

    private void UpdatePalette(bool animateColors)
    {
        var accent = C("AccentColor", Color.FromRgb(0x7C, 0x5C, 0xFF));
        var accent2 = C("Accent2Color", accent);
        var surface = C("ControlBrush", Color.FromRgb(0x16, 0x19, 0x28));
        if (surface.A < 180)
        {
            var isLight = TryFindResource("WindowBrush") is SolidColorBrush wb && wb.Color.R > 180;
            surface = isLight ? Color.FromRgb(0xF3, 0xF6, 0xFC) : Color.FromRgb(0x15, 0x18, 0x27);
        }
        var textCol = C("TextBrush", Colors.White);

        // Harmonious state color pairs (no clashing neon green + purple slab!)
        var (main, second, busy) = State switch
        {
            ConnectionState.Connected => (C("SuccessColor", Color.FromRgb(0x2E, 0xE0, 0x8A)), Color.FromRgb(0x06, 0xB6, 0xD4), false),
            ConnectionState.Degraded => (Color.FromRgb(0xFF, 0xB5, 0x47), Color.FromRgb(0xF5, 0x9E, 0x0B), false),
            ConnectionState.Error => (C("DangerColor", Color.FromRgb(0xFF, 0x5C, 0x70)), Color.FromRgb(0xE1, 0x1D, 0x48), false),
            ConnectionState.Disconnecting => (C("DangerColor", Color.FromRgb(0xFF, 0x5C, 0x70)), Color.FromRgb(0xE1, 0x1D, 0x48), true),
            ConnectionState.Connecting or ConnectionState.Reconnecting => (accent, accent2, true),
            _ => (accent, accent2, false),
        };
        var idle = State is ConnectionState.Disconnected;

        // Core glassmorphic surface: blends the theme's glass surface with a rich inner aura of the state color
        var tintTop = idle ? 0.14 : busy ? 0.34 : 0.42;
        var tintMid = idle ? 0.06 : busy ? 0.22 : 0.28;
        var tintBot = idle ? 0.10 : busy ? 0.30 : 0.36;

        var c0 = Blend(surface, main, tintTop);
        var c1 = Blend(surface, main, tintMid);
        var c2 = Blend(surface, second, tintBot);

        var b0 = idle ? Color.FromArgb(0xC8, main.R, main.G, main.B) : Color.FromArgb(0xEE, main.R, main.G, main.B);
        var b1 = idle ? Color.FromArgb(0x78, second.R, second.G, second.B) : Color.FromArgb(0xAA, second.R, second.G, second.B);

        var g0 = Color.FromArgb((byte)(idle ? 0x2A : 0x58), main.R, main.G, main.B);
        var g1 = Color.FromArgb(0x00, second.R, second.G, second.B);

        var iconTarget = idle ? main : Blend(Colors.White, main, 0.22);
        var labelTarget = idle ? textCol : Colors.White;

        SetOrAnimate(_coreStop0, GradientStop.ColorProperty, c0, animateColors);
        SetOrAnimate(_coreStop1, GradientStop.ColorProperty, c1, animateColors);
        SetOrAnimate(_coreStop2, GradientStop.ColorProperty, c2, animateColors);
        SetOrAnimate(_borderStop0, GradientStop.ColorProperty, b0, animateColors);
        SetOrAnimate(_borderStop1, GradientStop.ColorProperty, b1, animateColors);
        SetOrAnimate(_glowStop0, GradientStop.ColorProperty, g0, animateColors);
        SetOrAnimate(_glowStop1, GradientStop.ColorProperty, g1, animateColors);
        SetOrAnimate(_iconBrush, SolidColorBrush.ColorProperty, iconTarget, animateColors);
        SetOrAnimate(_labelBrush, SolidColorBrush.ColorProperty, labelTarget, animateColors);
        SetOrAnimate(_ringBrush, SolidColorBrush.ColorProperty, main, animateColors);

        // Wave colors follow the active state's harmonious palette
        SetOrAnimate(_waveBackStop0, GradientStop.ColorProperty, Color.FromArgb(0x42, main.R, main.G, main.B), animateColors);
        SetOrAnimate(_waveBackStop1, GradientStop.ColorProperty, Color.FromArgb(0x18, second.R, second.G, second.B), animateColors);
        SetOrAnimate(_waveFrontStop0, GradientStop.ColorProperty, Color.FromArgb(0x68, main.R, main.G, main.B), animateColors);
        SetOrAnimate(_waveFrontStop1, GradientStop.ColorProperty, Color.FromArgb(0x2E, second.R, second.G, second.B), animateColors);
        SetOrAnimate(_waveCrestBrush, SolidColorBrush.ColorProperty, Color.FromArgb(0xB5, (byte)Math.Min(255, main.R + 65), (byte)Math.Min(255, main.G + 65), (byte)Math.Min(255, main.B + 65)), animateColors);

        _coreHost.Effect = Fx.Enabled
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = main, BlurRadius = idle ? 18 : 32, ShadowDepth = 0, Opacity = idle ? 0.28 : 0.55 }
            : null;
    }

    private void Refresh(bool animateColors = false)
    {
        ApplyShape();
        var burst = State is ConnectionState.Connected && _last is ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnected;
        var prev = _last;
        _last = State;
        var idle = State is ConnectionState.Disconnected;
        var busy = State is ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnecting;
        var tearDown = idle && prev is ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting or ConnectionState.Disconnecting;

        UpdatePalette(animateColors);
        UpdateWaveLoop();

        var animate = Fx.Enabled && IsVisible;
        if (busy && animate)
        {
            var dir = State is ConnectionState.Disconnecting ? -1 : 1;
            _arc.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));
            _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, dir * TrackPerimeter / ArcThickness, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            _arc2.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, TimeSpan.FromSeconds(0.3)));
            _arc2.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -dir * TrackPerimeter / Arc2Thickness, TimeSpan.FromSeconds(1.7)) { RepeatBehavior = RepeatBehavior.Forever });
            var breathe = new DoubleAnimation(1, 1.045, TimeSpan.FromSeconds(0.85)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(breathe, 40);
            _coreScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            _coreScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }
        else
        {
            _arc.BeginAnimation(OpacityProperty, new DoubleAnimation(busy ? 1 : 0, TimeSpan.FromSeconds(0.25)));
            _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            _arc2.BeginAnimation(OpacityProperty, new DoubleAnimation(busy ? 0.75 : 0, TimeSpan.FromSeconds(0.25)));
            _arc2.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        }

        if (animate && tearDown)
        {
            var danger = C("DangerColor", Color.FromRgb(0xFF, 0x5C, 0x70));
            var st = (ScaleTransform)_burst.RenderTransform;
            var implode = new DoubleAnimation(1.7, 0.72, TimeSpan.FromSeconds(0.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromSeconds(0.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            _burst.Stroke = new SolidColorBrush(danger);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, implode); st.BeginAnimation(ScaleTransform.ScaleYProperty, implode);
            _burst.BeginAnimation(OpacityProperty, fade);

            var settle = new Storyboard();
            foreach (var axis in new[] { "ScaleX", "ScaleY" })
            {
                var dip = new DoubleAnimation(1, 0.93, TimeSpan.FromSeconds(0.12)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var back = new DoubleAnimation(0.93, 1, TimeSpan.FromSeconds(0.4)) { BeginTime = TimeSpan.FromSeconds(0.12), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
                Storyboard.SetTarget(dip, _coreHost); Storyboard.SetTargetProperty(dip, new PropertyPath($"(RenderTransform).(ScaleTransform.{axis})"));
                Storyboard.SetTarget(back, _coreHost); Storyboard.SetTargetProperty(back, new PropertyPath($"(RenderTransform).(ScaleTransform.{axis})"));
                settle.Children.Add(dip); settle.Children.Add(back);
            }
            settle.Begin(_coreHost);

            _flash.Text = "\uE74D";
            _flash.Foreground = new SolidColorBrush(danger);
            var flash = new Storyboard();
            var fIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.14)) { EasingFunction = new SineEase() };
            var fOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5)) { BeginTime = TimeSpan.FromSeconds(0.85) };
            Storyboard.SetTarget(fIn, _flash); Storyboard.SetTargetProperty(fIn, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(fOut, _flash); Storyboard.SetTargetProperty(fOut, new PropertyPath(UIElement.OpacityProperty));
            flash.Children.Add(fIn); flash.Children.Add(fOut);
            flash.Begin();

            var swap = new Storyboard();
            var iOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.12));
            var iIn = new DoubleAnimation(1, TimeSpan.FromSeconds(0.35)) { BeginTime = TimeSpan.FromSeconds(0.85) };
            Storyboard.SetTarget(iOut, _icon); Storyboard.SetTargetProperty(iOut, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(iIn, _icon); Storyboard.SetTargetProperty(iIn, new PropertyPath(UIElement.OpacityProperty));
            swap.Children.Add(iOut); swap.Children.Add(iIn);
            swap.Begin();

            _glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5)));
        }

        if (animate && burst)
        {
            var st = (ScaleTransform)_burst.RenderTransform;
            var grow = new DoubleAnimation(0.8, 1.78, TimeSpan.FromSeconds(0.75)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromSeconds(0.75)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _burst.Stroke = new SolidColorBrush(C("SuccessColor", Color.FromRgb(0x2E, 0xE0, 0x8A)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow); st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _burst.BeginAnimation(OpacityProperty, fade);

            var pop = new Storyboard();
            foreach (var axis in new[] { "ScaleX", "ScaleY" })
            {
                var up = new DoubleAnimation(1, 1.09, TimeSpan.FromSeconds(0.1)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var down = new DoubleAnimation(1.09, 1, TimeSpan.FromSeconds(0.32)) { BeginTime = TimeSpan.FromSeconds(0.1), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 } };
                Storyboard.SetTarget(up, _coreHost); Storyboard.SetTargetProperty(up, new PropertyPath($"(RenderTransform).(ScaleTransform.{axis})"));
                Storyboard.SetTarget(down, _coreHost); Storyboard.SetTargetProperty(down, new PropertyPath($"(RenderTransform).(ScaleTransform.{axis})"));
                pop.Children.Add(up); pop.Children.Add(down);
            }
            pop.Begin(_coreHost);

            _flash.Text = "\uE73E";
            _flash.Foreground = Brushes.White;
            var flash = new Storyboard();
            var fIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)) { EasingFunction = new SineEase() };
            var fOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.45)) { BeginTime = TimeSpan.FromSeconds(1.05) };
            Storyboard.SetTarget(fIn, _flash); Storyboard.SetTargetProperty(fIn, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(fOut, _flash); Storyboard.SetTargetProperty(fOut, new PropertyPath(UIElement.OpacityProperty));
            flash.Children.Add(fIn); flash.Children.Add(fOut);
            flash.Begin();

            var swap = new Storyboard();
            var iOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.14));
            var iIn = new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)) { BeginTime = TimeSpan.FromSeconds(1.05) };
            Storyboard.SetTarget(iOut, _icon); Storyboard.SetTargetProperty(iOut, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(iIn, _icon); Storyboard.SetTargetProperty(iIn, new PropertyPath(UIElement.OpacityProperty));
            swap.Children.Add(iOut); swap.Children.Add(iIn);
            swap.Begin();
        }
        else if (!busy)
        {
            if (_flash.Opacity > 0) { _flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(1))); _icon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(1))); }
            _coreScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _coreScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        var period = busy ? 1.3 : State is ConnectionState.Connected or ConnectionState.Degraded ? 2.8 : 0;
        Pulse(_ring1, animate ? period : 0, 0);
        Pulse(_ring2, animate ? period : 0, period / 2);
        if (animate && !idle)
            _glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromSeconds(busy ? 0.7 : 2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        else { _glow.BeginAnimation(OpacityProperty, null); _glow.Opacity = 1; }
    }

    /* ── v0.4.41: Live Sine-Wave Energy Liquid inside ConnectOrb ── */

    private void UpdateWaveLoop()
    {
        var shouldRun = IsLoaded && IsVisible && Fx.Enabled && State is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Degraded;
        if (shouldRun)
        {
            _waveClipHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(350)));
            if (!_waveTicking)
            {
                _waveTicking = true;
                _waveLastTick = _waveClock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += OnWaveFrame;
            }
        }
        else
        {
            _waveClipHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300)));
            StopWaveLoop();
        }
    }

    private void StopWaveLoop()
    {
        if (!_waveTicking) return;
        _waveTicking = false;
        CompositionTarget.Rendering -= OnWaveFrame;
    }

    private void OnWaveFrame(object? sender, EventArgs e)
    {
        if (!Fx.Enabled || !IsVisible) { StopWaveLoop(); return; }
        var now = _waveClock.Elapsed.TotalSeconds;
        var dt = now - _waveLastTick;
        if (dt < 1.0 / 30.0) return; // ~30 fps
        _waveLastTick = now;

        var targetAct = Math.Clamp(Activity, 0, 1);
        _smoothActivity += (targetAct - _smoothActivity) * 0.16;

        var targetFill = State switch
        {
            ConnectionState.Connected or ConnectionState.Degraded => 0.34 + _smoothActivity * 0.34,
            ConnectionState.Connecting or ConnectionState.Reconnecting => 0.25,
            _ => 0.0,
        };
        _smoothWaveFill += (targetFill - _smoothWaveFill) * 0.12;

        // Advance wave phase faster when download traffic flows
        var speed = 2.4 + _smoothActivity * 4.2;
        _wavePhase += Math.Min(dt, 0.1) * speed;

        double w = CoreSize - 4, h = CoreSize - 4;
        double baseY = h * (1.0 - _smoothWaveFill);
        double ampFront = 4.2 + _smoothActivity * 6.5;
        double ampBack = 3.6 + _smoothActivity * 5.2;

        _waveBack.Data = BuildSineWaveGeometry(w, h, baseY - 3.0, ampBack, -_wavePhase * 0.82 + 1.4, 1.65);
        _waveFront.Data = BuildSineWaveGeometry(w, h, baseY, ampFront, _wavePhase, 1.35);
    }

    private static Geometry BuildSineWaveGeometry(double w, double h, double baseY, double amp, double phase, double freq)
    {
        const int steps = 24;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                var x = t * w;
                var y = baseY + Math.Sin(t * Math.PI * 2 * freq + phase) * amp
                              + Math.Cos(t * Math.PI * 4 * freq - phase * 0.6) * (amp * 0.25);
                if (i == 0) ctx.BeginFigure(new Point(x, y), isFilled: true, isClosed: true);
                else ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }
            ctx.LineTo(new Point(w, h + 4), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(0, h + 4), isStroked: false, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }

    private static void Pulse(Rectangle ring, double period, double delay)
    {
        var st = (ScaleTransform)ring.RenderTransform;
        if (period <= 0)
        {
            ring.BeginAnimation(OpacityProperty, null); ring.Opacity = 0;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var d = TimeSpan.FromSeconds(period);
        var begin = TimeSpan.FromSeconds(delay);
        var scale = new DoubleAnimation(1, 1.62, d) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.75, 0, d) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        Timeline.SetDesiredFrameRate(scale, 40); Timeline.SetDesiredFrameRate(fade, 40);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, scale); st.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        ring.BeginAnimation(OpacityProperty, fade);
    }
}
