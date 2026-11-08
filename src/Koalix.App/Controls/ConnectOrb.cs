using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Koalix.Core;

namespace Koalix.App.Controls;

/// <summary>
/// The big connect button (v0.4.13 redesign): a glowing rounded-square tile instead of an orb — the no-ovals
/// design rule. Everything that made the orb feel alive is kept: a dash-spinner runs around the rounded
/// rectangle while connecting, pulse rings expand outward, a shockwave fires when the tunnel comes up and the
/// core pops with a checkmark flash. Colour still makes the state obvious at a glance
/// (accent = ready, green = connected, amber = degraded, red = error).
/// v0.4.14: the shape is a SETTING again (Settings → Appearance → "شکل دکمهٔ اتصال"): the rounded-square tile
/// (default) or the classic circle orb — <see cref="UiPrefs.CircleConnect"/> drives every radius, the spinner
/// dash array and the pulse rings, and they re-morph live while the app runs.
/// </summary>
public sealed class ConnectOrb : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(ConnectionState), typeof(ConnectOrb),
        new PropertyMetadata(ConnectionState.Disconnected, (d, _) => ((ConnectOrb)d).Refresh()));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ConnectOrb));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ConnectOrb),
        new PropertyMetadata("", (d, e) => ((ConnectOrb)d)._label.Text = (string)e.NewValue));

    public ConnectionState State { get => (ConnectionState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private const double Size = 250, CoreSize = 150, TrackSize = 180;
    private const double CoreRadius = 36, TrackRadius = 46;      // rounded-square mode
    private const double CoreRadiusCircle = CoreSize / 2, TrackRadiusCircle = TrackSize / 2; // circle mode (v0.4.14)
    // Perimeter of the track: 4 straights + 4 quarter-arcs — recomputed per shape (a circle's is 2πr).
    private static double TrackPerimeter => 4 * (TrackSize - 2 * CurrentTrackRadius) + 2 * Math.PI * CurrentTrackRadius;
    private static double CurrentCoreRadius => UiPrefs.CircleConnect ? CoreRadiusCircle : CoreRadius;
    private static double CurrentTrackRadius => UiPrefs.CircleConnect ? TrackRadiusCircle : TrackRadius;
    private const double ArcThickness = 4, Arc2Thickness = 3;

    private readonly Border _glow;                                   // breathing halo (rounded square, radial fade)
    private readonly Rectangle _ring1 = Ring(), _ring2 = Ring();     // expanding pulse rings
    private readonly Rectangle _track = new()
    {
        Width = TrackSize, Height = TrackSize, RadiusX = TrackRadius, RadiusY = TrackRadius, StrokeThickness = 3, IsHitTestVisible = false,
    };
    private readonly Rectangle _arc;                                 // dash spinner (clockwise)
    private readonly Rectangle _arc2;                                // short dash spinner (counter-clockwise)
    /// <summary>One-shot success shockwave (v0.4.6): expands past the core and fades when the tunnel comes up.</summary>
    private readonly Rectangle _burst = new()
    {
        Width = CoreSize, Height = CoreSize, RadiusX = CoreRadius, RadiusY = CoreRadius, StrokeThickness = 3,
        IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
    };
    private readonly Border _core = new() { Width = CoreSize, Height = CoreSize, CornerRadius = new CornerRadius(CoreRadius), BorderThickness = new Thickness(2) };
    private readonly Border _shine = new() { Width = CoreSize - 6, Height = CoreSize - 6, CornerRadius = new CornerRadius(CoreRadius - 4), IsHitTestVisible = false };
    private readonly TextBlock _icon = new() { Text = "\uE7E8", FontSize = 42, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
    /// <summary>Brief checkmark overlay right after connecting (v0.4.6).</summary>
    private readonly TextBlock _flash = new() { Text = "\uE73E", FontSize = 42, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _label = new() { FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.NoWrap };
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

        _glow = new Border
        {
            Width = Size, Height = Size, CornerRadius = new CornerRadius(90), IsHitTestVisible = false,
            Background = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5) },
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

        var iconHost = new Grid();
        iconHost.Children.Add(_icon); iconHost.Children.Add(_flash);
        _flash.Foreground = Brushes.White;
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        content.Children.Add(iconHost); content.Children.Add(_label);
        _shine.Background = new LinearGradientBrush(Color.FromArgb(0x40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        var contentHost = new Grid();
        contentHost.Children.Add(_shine); contentHost.Children.Add(content);
        _core.Child = contentHost;
        _coreHost = new Grid { Width = CoreSize, Height = CoreSize, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _coreScale, Background = Brushes.Transparent };
        _coreHost.Children.Add(_core);
        _coreHost.MouseEnter += (_, _) => ScaleCore(1.05);
        _coreHost.MouseLeave += (_, _) => ScaleCore(1);
        _coreHost.MouseLeftButtonDown += (_, e) => { ScaleCore(0.95); e.Handled = true; };
        _coreHost.MouseLeftButtonUp += (_, e) =>
        {
            ScaleCore(_coreHost.IsMouseOver ? 1.05 : 1);
            if (Command?.CanExecute(null) == true) Command.Execute(null);
            e.Handled = true;
        };

        var root = new Grid();
        foreach (var el in new UIElement[] { _glow, _ring1, _ring2, _track, _arc, _arc2, _coreHost, _burst }) root.Children.Add(el);
        foreach (FrameworkElement el in root.Children) { el.HorizontalAlignment = HorizontalAlignment.Center; el.VerticalAlignment = VerticalAlignment.Center; }
        Content = root;
        FlowDirection = FlowDirection.LeftToRight;
        Loaded += (_, _) => { Services.ThemeService.Changed += Refresh; Services.ThemeService.RgbTick += OnRgbTick; UiPrefs.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { Services.ThemeService.Changed -= Refresh; Services.ThemeService.RgbTick -= OnRgbTick; UiPrefs.Changed -= Refresh; };
        IsVisibleChanged += (_, _) => Refresh();
    }

    /// <summary>Applies the chosen shape (Settings → شکل دکمهٔ اتصال) to every radius, ring and spinner dash (v0.4.14).</summary>
    private void ApplyShape()
    {
        double coreR = CurrentCoreRadius, trackR = CurrentTrackRadius;
        _core.CornerRadius = new CornerRadius(coreR);
        _shine.CornerRadius = new CornerRadius(Math.Max(0, coreR - 4));
        _burst.RadiusX = _burst.RadiusY = coreR;
        _ring1.RadiusX = _ring1.RadiusY = _ring2.RadiusX = _ring2.RadiusY = coreR;
        _track.RadiusX = _track.RadiusY = trackR;
        _arc.RadiusX = _arc.RadiusY = _arc2.RadiusX = _arc2.RadiusY = trackR;
        // The spinner dashes are fractions of the track perimeter — recompute for the new radius.
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

    private Color C(string key, Color fallback) => TryFindResource(key) is Color c ? c : fallback;

    /// <summary>v0.4.37 — the ARGB palette repaints the idle orb with the live hue; busy/connected states keep
    /// their own colours (a full Refresh there would restart the spinner animations every step).</summary>
    private void OnRgbTick() { if (State is ConnectionState.Disconnected && IsVisible) Refresh(); }

    private void Refresh()
    {
        ApplyShape();
        var burst = State is ConnectionState.Connected && _last is ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnected;
        var prev = _last; // v0.4.15: previous state BEFORE the update — drives the disconnect choreography
        _last = State;
        var accent = C("AccentColor", Color.FromRgb(0x7C, 0x5C, 0xFF));
        var accent2 = C("Accent2Color", accent);
        var (main, second, busy) = State switch
        {
            ConnectionState.Connected => (C("SuccessColor", Color.FromRgb(0x2E, 0xE0, 0x8A)), accent, false),
            ConnectionState.Degraded => (Color.FromRgb(0xFF, 0xB5, 0x47), Color.FromRgb(0xFF, 0x7A, 0x45), false),
            ConnectionState.Error => (C("DangerColor", Color.FromRgb(0xFF, 0x5C, 0x70)), Color.FromRgb(0xC0, 0x2A, 0x6A), false),
            // v0.4.15: DISCONNECTING gets its own dedicated look — the danger red while the tunnel powers down.
            ConnectionState.Disconnecting => (C("DangerColor", Color.FromRgb(0xFF, 0x5C, 0x70)), Color.FromRgb(0xC0, 0x2A, 0x6A), true),
            ConnectionState.Connecting or ConnectionState.Reconnecting => (accent, accent2, true),
            _ => (accent, accent2, false),
        };
        var idle = State is ConnectionState.Disconnected;
        // v0.4.15: one-shot DISCONNECT choreography — triggered when a live/busy tunnel lands on Disconnected.
        var tearDown = idle && prev is ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting or ConnectionState.Disconnecting;

        if (_glow.Background is RadialGradientBrush glow)
        {
            glow.GradientStops.Clear();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(idle ? 0x30 : 0x70), main.R, main.G, main.B), 0.35));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, main.R, main.G, main.B), 1));
        }
        if (idle)
        {
            _core.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
            _core.BorderBrush = new LinearGradientBrush(main, second, 45);
            _icon.Foreground = new SolidColorBrush(main);
            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        else
        {
            _core.Background = new LinearGradientBrush(main, second, 45) { Opacity = busy ? 0.75 : 1 };
            _core.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
            _icon.Foreground = _label.Foreground = Brushes.White;
        }
        _ring1.Stroke = _ring2.Stroke = new SolidColorBrush(main);
        _coreHost.Effect = Fx.Enabled ? new System.Windows.Media.Effects.DropShadowEffect { Color = main, BlurRadius = idle ? 18 : 36, ShadowDepth = 0, Opacity = idle ? 0.35 : 0.75 } : null;

        var animate = Fx.Enabled && IsVisible;
        // Spinner: the dash runs around the track clockwise while connecting (v0.4.13) — and COUNTER-clockwise
        // while disconnecting (v0.4.15): the motion itself reads "the tunnel is unwinding".
        if (busy && animate)
        {
            var dir = State is ConnectionState.Disconnecting ? -1 : 1;
            _arc.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));
            _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, dir * TrackPerimeter / ArcThickness, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            _arc2.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, TimeSpan.FromSeconds(0.3)));
            _arc2.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -dir * TrackPerimeter / Arc2Thickness, TimeSpan.FromSeconds(1.7)) { RepeatBehavior = RepeatBehavior.Forever });
            // The core breathes while working (v0.4.6).
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
        // Dedicated disconnect choreography (v0.4.15): the implode ring collapses into the core, the core pops
        // back with a spring, and a red ✖ flashes where the checkmark appears on connect.
        if (animate && tearDown)
        {
            var danger = C("DangerColor", Color.FromRgb(0xFF, 0x5C, 0x70));
            var st = (ScaleTransform)_burst.RenderTransform;
            var implode = new DoubleAnimation(1.7, 0.72, TimeSpan.FromSeconds(0.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromSeconds(0.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            _burst.Stroke = new SolidColorBrush(danger);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, implode); st.BeginAnimation(ScaleTransform.ScaleYProperty, implode);
            _burst.BeginAnimation(OpacityProperty, fade);

            // Core settle: a quick dip and a spring back to the idle tile.
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

            // Red ✖ flash (same slot as the green checkmark on connect).
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

            // The glow dies out fast instead of the usual breathing fade.
            _glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5)));
        }
        // Success choreography (v0.4.6): shockwave + core pop + checkmark flash when the tunnel comes up.
        if (animate && burst)
        {
            var st = (ScaleTransform)_burst.RenderTransform;
            var grow = new DoubleAnimation(0.8, 1.78, TimeSpan.FromSeconds(0.75)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromSeconds(0.75)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _burst.Stroke = new SolidColorBrush(C("SuccessColor", Color.FromRgb(0x2E, 0xE0, 0x8A)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow); st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _burst.BeginAnimation(OpacityProperty, fade);

            // Core pop: up fast, settle back with a spring — two phases per axis, so a storyboard.
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

            // Checkmark flash replaces the icon for a moment, then swaps back (restore the ✓ after a disconnect ✖).
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
            // Stop the connecting "breathing" in every non-busy state; the burst storyboard (separate layer) keeps playing.
            _coreScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _coreScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        // Pulse rings: fast while connecting, slow "heartbeat" when connected, off otherwise.
        var period = busy ? 1.3 : State is ConnectionState.Connected or ConnectionState.Degraded ? 2.8 : 0;
        Pulse(_ring1, animate ? period : 0, 0);
        Pulse(_ring2, animate ? period : 0, period / 2);
        // Breathing glow
        if (animate && !idle)
            _glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromSeconds(busy ? 0.7 : 2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        else { _glow.BeginAnimation(OpacityProperty, null); _glow.Opacity = 1; }
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
