using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Koalix.App.Controls;

/// <summary>
/// The page-header emblems (v0.4.17) — the live decorative strip beside each section title the user asked for
/// across two rounds: the Routing Inspector's beating ECG line, the Split-Tunneling shuttling arrows, the Logs
/// rising/falling bars and the Ping Monitor's drifting waves. Pure decoration, never blocking input; one looping
/// storyboard per emblem, 30 fps, stopped on unload and disabled entirely with Fx.Enabled (ui.effects).
/// (v0.4.22: the koala-tile heartbeat was removed at the user's request — «نوار قلبه و حذف کن خوشم نیومد».)
/// </summary>
public sealed class SectionEmblem : Viewbox
{
    public static readonly IReadOnlyList<string> Kinds = ["Ecg", "Arrows", "Bars", "Waves"];

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(string), typeof(SectionEmblem),
            new PropertyMetadata("Ecg", (d, _) => ((SectionEmblem)d).Rebuild()));
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private Storyboard? _sb;

    public SectionEmblem()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    private void Rebuild()
    {
        Stop();
        Child = null;
        if (IsLoaded) Start();
    }

    private void Start()
    {
        Stop();
        if (!Fx.Enabled || DesignerProperties.GetIsInDesignMode(this)) return;
        var (panel, sb) = Kind switch
        {
            "Arrows" => Arrows(),
            "Bars" => Bars(),
            "Waves" => Waves(),
            _ => Ecg(),
        };
        Child = panel;
        _sb = sb;
        Timeline.SetDesiredFrameRate(sb, 30);
        sb.Begin(this, true);
    }

    private void Stop()
    {
        if (_sb is null) return;
        try { _sb.Remove(this); } catch { }
        _sb = null;
    }

    /* ─────────── shared helpers ─────────── */

    private static Color Res(string key, Color fallback) => Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    /// <summary>One ECG beat, period <paramref name="p"/> wide, on a canvas <paramref name="h"/> tall (baseline low-middle).</summary>
    private static PointCollection EcgPattern(double p, double h)
    {
        // classic monitor beat: baseline → P bump → Q dip → R spike → S dip → T bump → baseline
        double B() => h * 0.66;
        return
        [
            new(0, B()), new(p * 0.18, B()), new(p * 0.26, h * 0.58), new(p * 0.34, B()),
            new(p * 0.44, h * 0.72), new(p * 0.50, h * 0.16), new(p * 0.56, h * 0.86), new(p * 0.64, B()),
            new(p * 0.78, h * 0.56), new(p * 0.88, B()), new(p, B()),
        ];
    }

    private static Brush Accent(double opacity = 1) => new SolidColorBrush { Color = Res("AccentColor", Color.FromRgb(0x7C, 0x5C, 0xFF)), Opacity = opacity };
    private static Brush Success(double opacity = 1) => new SolidColorBrush { Color = Res("SuccessColor", Color.FromRgb(0x30, 0xD1, 0x58)), Opacity = opacity };
    private static void Target(Timeline a, string path) => Storyboard.SetTargetProperty(a, new PropertyPath(path));

    /// <summary>A scrolling polyline + its already-targeted animation (the helper that actually wires the storyboard).</summary>
    private static (Polyline line, DoubleAnimation anim) MakeScroller(PointCollection one, double period, double w, double h,
        Brush stroke, double thickness, double xFrom, double xTo, double seconds)
    {
        var pts = new PointCollection();
        var copies = (int)Math.Ceiling((w + Math.Abs(Math.Min(xFrom, xTo)) + period) / period) + 1;
        for (var i = 0; i < copies; i++)
            foreach (var pt in one) pts.Add(new Point(pt.X + i * period, pt.Y));
        var line = new Polyline
        {
            Points = pts,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        line.RenderTransform = new TranslateTransform(xFrom, 0);
        var a = new DoubleAnimation(xFrom, xTo, TimeSpan.FromSeconds(seconds)); // no easing = linear sweep
        Target(a, "(UIElement.RenderTransform).(TranslateTransform.X)");
        Storyboard.SetTarget(a, line);
        return (line, a);
    }

    /* ─────────── ECG (Routing Inspector) — 168×36 ─────────── */

    private (FrameworkElement, Storyboard) Ecg()
    {
        const double w = 168, h = 36, p = 84;
        var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
        var beat = EcgPattern(p, h);

        Storyboard sb = new() { RepeatBehavior = RepeatBehavior.Forever };

        // wide soft glow under the crisp line + a dim tail chasing the head
        var glow = MakeScroller(beat, p, w, h, Accent(0.28), 5.5, 0, -p, 2.4);
        var line = MakeScroller(beat, p, w, h, Accent(0.95), 1.7, 0, -p, 2.4);
        var lag = MakeScroller(beat, p, w, h, Accent(0.5), 1.2, p, 0, 2.4);

        canvas.Children.Add(glow.line); canvas.Children.Add(lag.line); canvas.Children.Add(line.line);
        sb.Children.Add(glow.anim); sb.Children.Add(lag.anim); sb.Children.Add(line.anim);

        // the beating dot at the head of the freshest spike — a soft pulse on the R peak
        var dot = new Ellipse { Width = 7, Height = 7, Fill = Accent(0.9) };
        Canvas.SetLeft(dot, w - 6); Canvas.SetTop(dot, h * 0.16 - 3.5);
        var pulse = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(0.55)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Target(pulse, "Opacity");
        Storyboard.SetTarget(pulse, dot);
        canvas.Children.Add(dot);
        sb.Children.Add(pulse);

        return (canvas, sb);
    }

    /* ─────────── Arrows (Split Tunneling) — 168×36 ─────────── */

    private (FrameworkElement, Storyboard) Arrows()
    {
        const double w = 168, h = 36;
        var canvas = new Canvas { Width = w, Height = h };

        // → arrow (tunnel direction) shuttles right and back; ← arrow mirrors it with opposite phase.
        // Both arrows carry a TransformGroup(flip?, translate) — the animation drives the group's TranslateTransform.
        var right = ArrowPath(accent: true, flip: false);
        var left = ArrowPath(accent: false, flip: true);
        Canvas.SetTop(right, h / 2 - 8); Canvas.SetTop(left, h / 2 - 8);
        canvas.Children.Add(right); canvas.Children.Add(left);

        Storyboard sb = new() { RepeatBehavior = RepeatBehavior.Forever };
        // gentle shuttling, opposite phases — "packets deciding which way to go"
        var ra = new DoubleAnimation(8, 30, TimeSpan.FromSeconds(1.15)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, RepeatBehavior = RepeatBehavior.Forever };
        var la = new DoubleAnimation(96, 74, TimeSpan.FromSeconds(1.15)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, RepeatBehavior = RepeatBehavior.Forever };
        Target(ra, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"); Storyboard.SetTarget(ra, right);
        Target(la, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"); Storyboard.SetTarget(la, left);
        sb.Children.Add(ra); sb.Children.Add(la);

        // three little packets flowing between the arrows
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 4.5, Height = 4.5, Fill = Accent(0.55), RenderTransform = new TranslateTransform(0, 0) };
            Canvas.SetTop(dot, h / 2 - 2.25);
            canvas.Children.Add(dot);
            var da = new DoubleAnimation(30, 74, TimeSpan.FromSeconds(1.5))
            {
                BeginTime = TimeSpan.FromSeconds(i * 0.5),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Target(da, "(UIElement.RenderTransform).(TranslateTransform.X)");
            Storyboard.SetTarget(da, dot);
            sb.Children.Add(da);
        }
        return (canvas, sb);
    }

    private static System.Windows.Shapes.Path ArrowPath(bool accent, bool flip)
    {
        // 64×16 arrow glyph pointing right (the mirrored one flips with ScaleX=-1 about its own centre)
        var figure = new PathFigure { StartPoint = new Point(0, 5) };
        figure.Segments.Add(new LineSegment(new Point(46, 5), true));
        figure.Segments.Add(new LineSegment(new Point(46, 0), true));
        figure.Segments.Add(new LineSegment(new Point(64, 8), true));
        figure.Segments.Add(new LineSegment(new Point(46, 16), true));
        figure.Segments.Add(new LineSegment(new Point(46, 11), true));
        figure.Segments.Add(new LineSegment(new Point(0, 11), true));
        var geo = new PathGeometry([figure]);
        var group = new TransformGroup();
        if (flip) group.Children.Add(new ScaleTransform(-1, 1, 32, 8));
        group.Children.Add(new TranslateTransform(0, 0));
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = accent ? Accent(0.9) : Success(0.75),
            RenderTransform = group,
        };
    }

    /* ─────────── Bars (Logs) — 168×36 ─────────── */

    private (FrameworkElement, Storyboard) Bars()
    {
        const double w = 168, h = 36, n = 7;
        var canvas = new Canvas { Width = w, Height = h };
        Storyboard sb = new() { RepeatBehavior = RepeatBehavior.Forever };

        // baseline
        var baseLine = new Rectangle { Width = w, Height = 1.5, Fill = Accent(0.25) };
        Canvas.SetTop(baseLine, h - 3);
        canvas.Children.Add(baseLine);

        var heights = new[] { 0.45, 0.75, 0.55, 1.0, 0.62, 0.85, 0.5 };
        var rng = new Random(17);
        for (var i = 0; i < n; i++)
        {
            var barW = 12;
            var bar = new Border
            {
                Width = barW,
                Height = 24,
                CornerRadius = new CornerRadius(3, 3, 1, 1),
                Background = i % 3 == 1 ? Success(0.55) : Accent(0.6),
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform(1, heights[i]),
            };
            Canvas.SetLeft(bar, 8 + i * (w - 16 - barW) / (n - 1));
            Canvas.SetTop(bar, h - 4.5 - 24);
            canvas.Children.Add(bar);

            // each bar breathes up/down with its own phase and span — like scrolling log activity
            var to = 0.35 + rng.NextDouble() * 0.65;
            var a = new DoubleAnimation(heights[i], Math.Max(0.22, to), TimeSpan.FromSeconds(1.0 + rng.NextDouble() * 1.1))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(rng.NextDouble() * 0.8),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Target(a, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            Storyboard.SetTarget(a, bar);
            sb.Children.Add(a);
        }
        return (canvas, sb);
    }

    /* ─────────── Waves (Ping Monitor) — 168×36 ─────────── */

    private (FrameworkElement, Storyboard) Waves()
    {
        const double w = 168, h = 36, p = 56;
        var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
        Storyboard sb = new() { RepeatBehavior = RepeatBehavior.Forever };

        PointCollection Sine(double period, double y, double amp)
        {
            var pts = new PointCollection();
            for (var x = 0d; x <= period; x += 4)
                pts.Add(new Point(x, y + Math.Sin(x / period * 2 * Math.PI) * amp));
            return pts;
        }

        var back = MakeScroller(Sine(p, h * 0.58, 6), p, w, h, Accent(0.35), 1.6, 0, -p, 2.6);
        var front = MakeScroller(Sine(p, h * 0.5, 8), p, w, h, Accent(0.9), 1.9, -p, 0, 2.6);
        var green = MakeScroller(Sine(p * 1.25, h * 0.42, 5), p * 1.25, w, h, Success(0.55), 1.3, 0, -p * 1.25, 3.4);
        canvas.Children.Add(back.line); canvas.Children.Add(green.line); canvas.Children.Add(front.line);
        sb.Children.Add(back.anim); sb.Children.Add(green.anim); sb.Children.Add(front.anim);
        return (canvas, sb);
    }
}
