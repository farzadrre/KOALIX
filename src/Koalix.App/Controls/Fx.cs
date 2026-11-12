using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Koalix.App.Controls;

/// <summary>
/// Visual effects as attached behaviours. <c>Fx.HoverGlow</c> paints a Windows 11 Fluent Reveal spotlight that follows
/// the mouse inside a glass card and illuminates the card edge near the cursor. Only the hovered card gets a custom brush;
/// on leave the exact pre-hover resource/style values are restored, so theme/accent switching and selection triggers keep working.
/// </summary>
public static class Fx
{
    public static bool Enabled { get; set; } = true;
    private const double Radius = 250;

    public static readonly DependencyProperty HoverGlowProperty =
        DependencyProperty.RegisterAttached("HoverGlow", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnHoverGlowChanged));
    public static bool GetHoverGlow(DependencyObject d) => (bool)d.GetValue(HoverGlowProperty);
    public static void SetHoverGlow(DependencyObject d, bool v) => d.SetValue(HoverGlowProperty, v);

    private sealed class GlowState
    {
        public RadialGradientBrush Fill = null!;
        public RadialGradientBrush Edge = null!;
        public RectangleGeometry RectGeo = null!;
        public DrawingBrush FillBrush = null!;
        public DrawingBrush EdgeBrush = null!;
        public object? OrigBg;
        public object? OrigBorder;
        public bool Leaving;
    }
    private static readonly ConditionalWeakTable<Border, GlowState> States = new();

    private static void OnHoverGlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b) return;
        if ((bool)e.NewValue) { b.MouseEnter += Enter; b.MouseMove += Move; b.MouseLeave += Leave; }
        else { b.MouseEnter -= Enter; b.MouseMove -= Move; b.MouseLeave -= Leave; }
    }

    private static Color Res(string key, Color fallback) => Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    private static void Enter(object sender, MouseEventArgs e)
    {
        if (!Enabled || sender is not Border b || b.ActualWidth < 4 || b.ActualHeight < 4) return;
        var accent = Res("AccentColor", Color.FromRgb(0x7C, 0x5C, 0xFF));
        var spec = Color.FromArgb(0xFF,
            (byte)Math.Min(255, accent.R + 78),
            (byte)Math.Min(255, accent.G + 78),
            (byte)Math.Min(255, accent.B + 78));
        var p = e.GetPosition(b);

        object? origBg = DependencyProperty.UnsetValue;
        object? origBorder = DependencyProperty.UnsetValue;
        if (States.TryGetValue(b, out var prev))
        {
            origBg = prev.OrigBg;
            origBorder = prev.OrigBorder;
        }
        else
        {
            origBg = b.ReadLocalValue(Border.BackgroundProperty);
            origBorder = b.ReadLocalValue(Border.BorderBrushProperty);
        }

        var fill = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = p, GradientOrigin = p, RadiusX = 0, RadiusY = 0 };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x34, accent.R, accent.G, accent.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, accent.R, accent.G, accent.B), 0.52));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1));

        // Windows 11 Fluent Reveal border spotlight: bright specular core fading through the accent into transparent
        var edge = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = p, GradientOrigin = p, RadiusX = 0, RadiusY = 0 };
        edge.GradientStops.Add(new GradientStop(spec, 0));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, accent.R, accent.G, accent.B), 0.36));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x44, accent.R, accent.G, accent.B), 0.72));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));

        if (States.TryGetValue(b, out _))
        {
            RestoreBrushes(b, origBg, origBorder);
        }

        var baseBrush = b.Background ?? b.TryFindResource("CardBrush") as Brush ?? Brushes.Transparent;
        var baseEdge = b.BorderBrush ?? b.TryFindResource("CardBorderBrush") as Brush ?? Brushes.Transparent;
        var rect = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight));

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(baseBrush, null, rect));
        group.Children.Add(new GeometryDrawing(fill, null, rect));
        var fillBrush = new DrawingBrush(group) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect.Rect };
        b.Background = fillBrush;

        var edgeGroup = new DrawingGroup();
        edgeGroup.Children.Add(new GeometryDrawing(baseEdge, null, rect));
        edgeGroup.Children.Add(new GeometryDrawing(edge, null, rect));
        var edgeBrush = new DrawingBrush(edgeGroup) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect.Rect };
        b.BorderBrush = edgeBrush;

        States.AddOrUpdate(b, new GlowState
        {
            Fill = fill,
            Edge = edge,
            RectGeo = rect,
            FillBrush = fillBrush,
            EdgeBrush = edgeBrush,
            OrigBg = origBg,
            OrigBorder = origBorder,
        });
        Animate(fill, Radius, 0.32);
        Animate(edge, Math.Min(Radius * 0.78, Math.Max(b.ActualWidth, b.ActualHeight) * 0.85), 0.28);
    }

    private static void Move(object sender, MouseEventArgs e)
    {
        if (sender is not Border b || !States.TryGetValue(b, out var s) || s.Leaving) return;
        var p = e.GetPosition(b);
        if (Math.Abs(s.RectGeo.Rect.Width - b.ActualWidth) > 1 || Math.Abs(s.RectGeo.Rect.Height - b.ActualHeight) > 1)
        {
            var nr = new Rect(0, 0, Math.Max(1, b.ActualWidth), Math.Max(1, b.ActualHeight));
            s.RectGeo.Rect = nr;
            s.FillBrush.Viewbox = nr;
            s.EdgeBrush.Viewbox = nr;
        }
        s.Fill.Center = s.Fill.GradientOrigin = p;
        s.Edge.Center = s.Edge.GradientOrigin = p;
    }

    private static void Leave(object sender, MouseEventArgs e)
    {
        if (sender is not Border b || !States.TryGetValue(b, out var s)) return;
        s.Leaving = true;
        var a = Animate(s.Fill, 0, 0.38);
        Animate(s.Edge, 0, 0.30);
        a.Completed += (_, _) =>
        {
            if (!States.TryGetValue(b, out var cur) || cur != s) return; // re-entered meanwhile
            States.Remove(b);
            RestoreBrushes(b, s.OrigBg, s.OrigBorder);
        };
    }

    /// <summary>v0.4.42 — drops a live hover-glow: its composed brush holds a SNAPSHOT of the card colour, so after a
    /// theme/palette change the card would keep the old colour until the mouse left it. Restores the themed brushes.</summary>
    internal static void ResetGlow(Border b)
    {
        if (!States.TryGetValue(b, out var s)) return;
        States.Remove(b);
        s.Fill.BeginAnimation(RadialGradientBrush.RadiusXProperty, null);
        s.Fill.BeginAnimation(RadialGradientBrush.RadiusYProperty, null);
        s.Edge.BeginAnimation(RadialGradientBrush.RadiusXProperty, null);
        s.Edge.BeginAnimation(RadialGradientBrush.RadiusYProperty, null);
        RestoreBrushes(b, s.OrigBg, s.OrigBorder);
    }

    private static void RestoreBrushes(Border b, object? origBg, object? origBorder)
    {
        if (origBg == DependencyProperty.UnsetValue || origBg is null)
            b.ClearValue(Border.BackgroundProperty);
        else
            b.SetValue(Border.BackgroundProperty, origBg);

        if (origBorder == DependencyProperty.UnsetValue || origBorder is null)
            b.ClearValue(Border.BorderBrushProperty);
        else
            b.SetValue(Border.BorderBrushProperty, origBorder);
    }

    private static DoubleAnimation Animate(RadialGradientBrush brush, double to, double seconds)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        brush.BeginAnimation(RadialGradientBrush.RadiusXProperty, a);
        brush.BeginAnimation(RadialGradientBrush.RadiusYProperty, a);
        return a;
    }

    /// <summary>Fade + slide-in used for page transitions and toasts.</summary>
    public static void SlideIn(FrameworkElement el, double fromY = 16, double seconds = 0.32)
    {
        if (!Enabled) { el.Opacity = 1; return; }
        if (el.RenderTransform is not TranslateTransform t || t.IsFrozen) el.RenderTransform = t = new TranslateTransform();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds * 0.8)));
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
    }

    /// <summary>Staggered entrance for the direct children of a panel (cards appear one after another).</summary>
    public static void Stagger(Panel panel, double step = 0.045)
    {
        if (!Enabled) return;
        var i = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            var t = new TranslateTransform(0, 18);
            // v0.4.12: keep any existing transform (e.g. HoverScale's ScaleTransform) — wrap, don't replace.
            fe.RenderTransform = fe.RenderTransform switch
            {
                null => (Transform)t,
                TransformGroup g when !g.IsFrozen => AddTo(g, t),
                Transform existing when !existing.IsFrozen => new TransformGroup { Children = { t, existing } },
                _ => (Transform)t,
            };
            fe.Opacity = 0;
            var begin = TimeSpan.FromSeconds(step * i++);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)) { BeginTime = begin });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromSeconds(0.42)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    private static TransformGroup AddTo(TransformGroup g, Transform t)
    {
        g.Children.Insert(0, t);
        return g;
    }

    /* ── v0.4.12: gentle scale-up on hover (cards grow by a hair, spring back on leave) ── */

    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached("HoverScale", typeof(double), typeof(Fx), new PropertyMetadata(0.0, OnHoverScaleChanged));
    public static double GetHoverScale(DependencyObject d) => (double)d.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject d, double v) => d.SetValue(HoverScaleProperty, v);

    private static void OnHoverScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((double)e.NewValue > 1.0)
        {
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            EnsureScale(fe);
            fe.MouseEnter += ScaleEnter;
            fe.MouseLeave += ScaleLeave;
        }
        else { fe.MouseEnter -= ScaleEnter; fe.MouseLeave -= ScaleLeave; }
    }

    /// <summary>Finds (or creates) the ScaleTransform to animate, whatever the current transform composition is.</summary>
    private static ScaleTransform EnsureScale(FrameworkElement fe)
    {
        switch (fe.RenderTransform)
        {
            case ScaleTransform s when !s.IsFrozen:
                return s;
            case TransformGroup g when !g.IsFrozen:
                var found = g.Children.OfType<ScaleTransform>().FirstOrDefault(x => !x.IsFrozen);
                if (found is null) { found = new ScaleTransform(1, 1); g.Children.Add(found); }
                return found;
            default:
                var fresh = new ScaleTransform(1, 1);
                fe.RenderTransform = fresh;
                return fresh;
        }
    }

    private static void ScaleEnter(object sender, MouseEventArgs e)
    {
        if (!Enabled || sender is not FrameworkElement fe) return;
        var s = EnsureScale(fe);
        var to = GetHoverScale(fe);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.28)) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.28)) { EasingFunction = ease });
    }

    private static void ScaleLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        ScaleTransform? s = fe.RenderTransform switch
        {
            TransformGroup g => g.Children.OfType<ScaleTransform>().FirstOrDefault(),
            ScaleTransform st => st,
            _ => null,
        };
        if (s is null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)) { EasingFunction = ease });
    }

    /* ── v0.4.13: per-section live motion ────────────────────────────────────────────────────────
       The active section's sidebar icon keeps its own private animation while you are on that page:
       the DNS globe slowly spins, the settings gear turns, the wrench rocks, the speedometer needle
       wobbles, the home heartbeats … Set Fx.LiveMotion="{Binding Key}" on the nav glyph; the property
       hooks the owning ListBoxItem's Selected/Unselected and starts/stops the matching storyboard. */

    public static readonly DependencyProperty LiveMotionProperty =
        DependencyProperty.RegisterAttached("LiveMotion", typeof(string), typeof(Fx), new PropertyMetadata(null, OnLiveMotionChanged));
    public static string? GetLiveMotion(DependencyObject d) => (string?)d.GetValue(LiveMotionProperty);
    public static void SetLiveMotion(DependencyObject d, string? v) => d.SetValue(LiveMotionProperty, v);

    private sealed class MotionState
    {
        public string Key = "";
        public Storyboard? Story;
        public ListBoxItem? Item;
        public RoutedEventHandler? OnSel, OnUnsel;
        public bool Hooked;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, MotionState> LiveMotions = new();

    private static void OnLiveMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (!LiveMotions.TryGetValue(fe, out var st)) { st = new MotionState(); LiveMotions.AddOrUpdate(fe, st); }
        StopMotion(fe, st);
        st.Key = e.NewValue as string ?? "";
        HookItem(fe, st);
    }

    private static void HookItem(FrameworkElement fe, MotionState st)
    {
        var item = FindAncestor<ListBoxItem>(fe);
        if (item is null)
        {
            // Binding may evaluate before the template enters the tree — retry on load.
            if (!st.Hooked) { st.Hooked = true; fe.Loaded += (_, _) => { if (LiveMotions.TryGetValue(fe, out var s2)) HookItem(fe, s2); }; }
            return;
        }
        if (item == st.Item) { if (item.IsSelected) StartMotion(fe, st); return; }
        if (st.Item is not null && st.OnSel is not null && st.OnUnsel is not null)
        { st.Item.Selected -= st.OnSel; st.Item.Unselected -= st.OnUnsel; }
        st.Item = item;
        st.OnSel = (_, _) => StartMotion(fe, st);
        st.OnUnsel = (_, _) => StopMotion(fe, st);
        item.Selected += st.OnSel;
        item.Unselected += st.OnUnsel;
        if (item.IsSelected) StartMotion(fe, st);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static void StartMotion(FrameworkElement fe, MotionState st)
    {
        if (!Enabled || st.Key.Length == 0) return;
        StopMotion(fe, st);
        var motion = BuildMotion(st.Key);
        if (motion is null) return;
        var (sb, transform) = motion.Value;
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        fe.RenderTransform = transform;
        st.Story = sb;
        Timeline.SetDesiredFrameRate(sb, 30); // 30 fps is plenty for a 16px glyph — negligible CPU
        sb.Begin(fe, true);
    }

    private static void StopMotion(FrameworkElement fe, MotionState st)
    {
        if (st.Story is null) return;
        try { st.Story.Remove(fe); } catch { }
        st.Story = null;
        fe.RenderTransform = Transform.Identity;
    }

    /// <summary>The motion catalogue — every section gets its own personality. Returns the storyboard plus
    /// the transform it animates (installed on the glyph before the storyboard starts). Null = no motion
    /// (v0.4.19: Split/Inspector/Ping moved to their own always-on <see cref="MotionGlyph"/> vector icons).</summary>
    private static (Storyboard, Transform)? BuildMotion(string key) => key switch
    {
        "Nav.Dns" => Spin(3.8),                 // globe — slow full spin
        "Nav.Proxy" => Spin(-4.6),              // second globe — counter-spin
        "Nav.Settings" => Spin(6.5),            // gear — turning
        "Nav.Dashboard" => Pulse(1.16, 1.25),   // home — heartbeat
        "Nav.Profiles" => Bob(-3.2, 1.0),       // servers — stack bob
        "Nav.Logs" => Shuffle(1.8, 1.35),       // list — nudge
        "Nav.Repair" => Rock(20, 1.35),         // wrench — rock
        "Nav.Split" or "Nav.Inspector" or "Nav.Ping" or "Nav.About" or "Nav.Gaming" => null, // live vector icons now (MotionGlyph, v0.4.19; Gaming joined in v0.4.46)
        _ => Pulse(1.1, 1.6),
    };

    private static (Storyboard, Transform) Spin(double seconds)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var a = seconds < 0
            ? new DoubleAnimation(360, 0, TimeSpan.FromSeconds(-seconds))
            : new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds));
        Prop(a, "(RenderTransform).(RotateTransform.Angle)");
        sb.Children.Add(a);
        return (sb, new RotateTransform());
    }

    private static (Storyboard, Transform) Pulse(double to, double seconds)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var sx = new DoubleAnimation(1, to, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease };
        var sy = new DoubleAnimation(1, to, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease };
        Prop(sx, "(RenderTransform).(ScaleTransform.ScaleX)");
        Prop(sy, "(RenderTransform).(ScaleTransform.ScaleY)");
        sb.Children.Add(sx); sb.Children.Add(sy);
        return (sb, new ScaleTransform());
    }

    private static (Storyboard, Transform) Bob(double y, double seconds)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var a = new DoubleAnimation(0, y, TimeSpan.FromSeconds(seconds)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Prop(a, "(RenderTransform).(TranslateTransform.Y)");
        sb.Children.Add(a);
        return (sb, new TranslateTransform());
    }

    private static (Storyboard, Transform) Shuffle(double x, double seconds)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var a = new DoubleAnimation(-x, x, TimeSpan.FromSeconds(seconds)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Prop(a, "(RenderTransform).(TranslateTransform.X)");
        sb.Children.Add(a);
        return (sb, new TranslateTransform());
    }

    /* ── v0.4.19: bar charts live ───────────────────────────────────────────────────────────────
       «گزارش‌ها اون نمودارا میله‌ای‌ش حرکت کنه» — three layers of motion on the usage share bars:
       (1) the fill sweeps a soft sheen across every bar continuously (ProgressBar template, Controls.xaml);
       (2) on attach each bar GROWS from zero (staggered) so the card visibly assembles itself;
       (3) real value changes still glide (the 200 ms debounce below keeps bindings untouched). */

    public static readonly DependencyProperty AnimateProgressProperty =
        DependencyProperty.RegisterAttached("AnimateProgress", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnAnimateProgressChanged));
    public static bool GetAnimateProgress(DependencyObject d) => (bool)d.GetValue(AnimateProgressProperty);
    public static void SetAnimateProgress(DependencyObject d, bool v) => d.SetValue(AnimateProgressProperty, v);

    private static readonly ConditionalWeakTable<ProgressBar, System.Runtime.CompilerServices.StrongBox<DateTime>> ProgressStarts = new();

    private static void OnAnimateProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar) return;
        if ((bool)e.NewValue)
        {
            bar.Loaded -= BarLoaded; bar.Loaded += BarLoaded;   // entrance: grow from zero (v0.4.19)
            bar.ValueChanged += OnAnimatedProgressValueChanged;
        }
        else { bar.Loaded -= BarLoaded; bar.ValueChanged -= OnAnimatedProgressValueChanged; }
    }

    /// <summary>Entrance (v0.4.19): when the usage card appears, every bar starts empty and grows to its value —
    /// staggered so the card assembles top-to-bottom instead of popping in fully drawn.</summary>
    private static void BarLoaded(object sender, RoutedEventArgs e)
    {
        var bar = (ProgressBar)sender;
        bar.Loaded -= BarLoaded;
        if (!Enabled || bar.Value <= 0) return;
        var delay = TimeSpan.FromMilliseconds(60 + Random.Shared.Next(0, 200));
        var a = new DoubleAnimation(0, bar.Value, TimeSpan.FromMilliseconds(700)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(a, 30);
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedProgressValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var bar = (ProgressBar)sender;
        if (!Enabled) return;
        var box = ProgressStarts.GetValue(bar, _ => new System.Runtime.CompilerServices.StrongBox<DateTime>(DateTime.MinValue));
        var now = DateTime.UtcNow;
        if ((now - box.Value).TotalMilliseconds < 200) return; // our own animation ticking — the target is already set
        box.Value = now;
        var a = new DoubleAnimation(bar.Value, e.NewValue, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(a, 30);
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    /* ── v0.4.20: the usage bars BREATHE («گزارش هم ثابته قرار بود میله هاش بالا پایین شن») ─────────────
       A gentle infinite length-oscillation of every share bar's FILL (ScaleX 0.94 ↔ 1.06 around the bar's
       origin edge, soft sine, per-bar phase). It rides ON TOP of whatever the real value is — the binding,
       the entrance grow and the value chase are untouched, and the numbers stay honest. */

    public static readonly DependencyProperty EqualizeProperty =
        DependencyProperty.RegisterAttached("Equalize", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnEqualizeChanged));
    public static bool GetEqualize(DependencyObject d) => (bool)d.GetValue(EqualizeProperty);
    public static void SetEqualize(DependencyObject d, bool v) => d.SetValue(EqualizeProperty, v);

    private static void OnEqualizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar) return;
        if ((bool)e.NewValue) { bar.Loaded -= EqualizeLoaded; bar.Loaded += EqualizeLoaded; bar.Unloaded -= EqualizeUnloaded; bar.Unloaded += EqualizeUnloaded; if (bar.IsLoaded) EqualizeLoaded(bar, RoutedEventArgsEmpty); }
        else { bar.Loaded -= EqualizeLoaded; bar.Unloaded -= EqualizeUnloaded; StopEqualize(bar); }
    }

    private static readonly RoutedEventArgs RoutedEventArgsEmpty = new();

    private static void EqualizeUnloaded(object sender, RoutedEventArgs _) => StopEqualize((ProgressBar)sender);

    private static void EqualizeLoaded(object sender, RoutedEventArgs _)
    {
        var bar = (ProgressBar)sender;
        StopEqualize(bar);
        if (!Enabled) return;
        bar.ApplyTemplate();
        if (bar.Template?.FindName("PART_Indicator", bar) is not FrameworkElement fill) return;
        var originLeft = bar.FlowDirection == FlowDirection.LeftToRight;
        fill.RenderTransformOrigin = new Point(originLeft ? 0 : 1, 0.5); // grow/shrink from the bar's origin edge
        var scale = new ScaleTransform(1, 1);
        fill.RenderTransform = scale;
        // every bar breathes at its own pace/phase so the card reads as a living equalizer, not one machine loop
        var phase = Math.Abs(bar.GetHashCode());
        var period = 1.15 + (phase % 7) * 0.13;
        var amp = 0.05 + (phase % 3) * 0.012;
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var a = new DoubleAnimation(1 - amp, 1 + amp, TimeSpan.FromSeconds(period)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(a, scale);
        Storyboard.SetTargetProperty(a, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(a);
        Timeline.SetDesiredFrameRate(sb, 30);
        _equalizers.AddOrUpdate(bar, sb);
        sb.Begin(bar, true);
    }

    private static void StopEqualize(ProgressBar bar)
    {
        if (_equalizers.TryGetValue(bar, out var sb))
        {
            try { sb.Remove(bar); } catch { }
            _equalizers.Remove(bar);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProgressBar, Storyboard> _equalizers = new();

    private static (Storyboard, Transform) Rock(double deg, double seconds)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var a = new DoubleAnimation(-deg, deg, TimeSpan.FromSeconds(seconds)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Prop(a, "(RenderTransform).(RotateTransform.Angle)");
        sb.Children.Add(a);
        return (sb, new RotateTransform());
    }

    private static void Prop(DoubleAnimation a, string path) => Storyboard.SetTargetProperty(a, new PropertyPath(path));

    /* ── v0.4.41: Rolling Odometer Numbers (شمارندهٔ انیمیشنی اعداد داشبورد و سایدبار) ─────────────────────
       Smoothly interpolates numeric strings ("98 ms" → "42 ms", "1.2 MB/s" → "8.4 MB/s") over 260 ms while
       gliding the TextBlock vertically with a directional odometer slide and micro-fade. */

    public static readonly DependencyProperty RollValueProperty =
        DependencyProperty.RegisterAttached("RollValue", typeof(string), typeof(Fx), new PropertyMetadata(null, OnRollValueChanged));
    public static string? GetRollValue(DependencyObject d) => (string?)d.GetValue(RollValueProperty);
    public static void SetRollValue(DependencyObject d, string? v) => d.SetValue(RollValueProperty, v);

    private static readonly Regex NumPattern = new(@"^(?<pre>[^\d\-]*)(?<num>-?\d+(?:\.\d+)?)(?<suf>.*)$", RegexOptions.Compiled);

    private sealed class RollState
    {
        public string LastRaw = "";
        public double CurrentNum;
        public bool HasNum;
        public string Prefix = "", Suffix = "", Format = "0";
        public DispatcherTimer? Timer;
        public DateTime StartAt;
        public double FromNum, ToNum;
    }

    private static readonly ConditionalWeakTable<TextBlock, RollState> RollStates = new();

    private static void OnRollValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var next = e.NewValue as string ?? "";
        var st = RollStates.GetValue(tb, _ => new RollState());
        if (string.Equals(st.LastRaw, next, StringComparison.Ordinal)) return;

        var firstLoad = st.LastRaw.Length == 0;
        st.LastRaw = next;

        if (!Enabled || firstLoad || !tb.IsLoaded)
        {
            st.Timer?.Stop();
            tb.Text = next;
            if (TryParseMetric(next, out var p0, out var n0, out var fmt0, out var s0))
            {
                st.HasNum = true; st.CurrentNum = n0; st.Prefix = p0; st.Suffix = s0; st.Format = fmt0;
            }
            else st.HasNum = false;
            return;
        }

        if (tb.RenderTransform is not TranslateTransform tr || tr.IsFrozen)
        {
            tr = new TranslateTransform();
            tb.RenderTransform = tr;
        }

        if (TryParseMetric(next, out var pre, out var num, out var fmt, out var suf)
            && st.HasNum
            && string.Equals(st.Prefix, pre, StringComparison.Ordinal)
            && string.Equals(st.Suffix, suf, StringComparison.Ordinal))
        {
            var delta = num - st.CurrentNum;
            if (Math.Abs(delta) < 0.001)
            {
                tb.Text = next;
                return;
            }

            // Directional odometer vertical roll (+6px when rising, -6px when dropping)
            double slideFrom = delta > 0 ? 5.5 : -5.5;
            var slide = new DoubleAnimation(slideFrom, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(slide, 30);
            tr.BeginAnimation(TranslateTransform.YProperty, slide);

            st.FromNum = st.CurrentNum;
            st.ToNum = num;
            st.Prefix = pre;
            st.Suffix = suf;
            st.Format = fmt;
            st.StartAt = DateTime.UtcNow;

            if (st.Timer is null)
            {
                st.Timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(28) };
                st.Timer.Tick += (_, _) =>
                {
                    var t = Math.Clamp((DateTime.UtcNow - st.StartAt).TotalMilliseconds / 240.0, 0, 1);
                    var ease = 1 - Math.Pow(1 - t, 3);
                    st.CurrentNum = st.FromNum + (st.ToNum - st.FromNum) * ease;
                    if (t >= 1.0)
                    {
                        st.CurrentNum = st.ToNum;
                        st.Timer.Stop();
                        tb.Text = st.LastRaw;
                    }
                    else
                    {
                        tb.Text = st.Prefix + st.CurrentNum.ToString(st.Format, CultureInfo.InvariantCulture) + st.Suffix;
                    }
                };
            }
            st.Timer.Start();
        }
        else
        {
            st.Timer?.Stop();
            if (TryParseMetric(next, out var p1, out var n1, out var fmt1, out var s1))
            {
                st.HasNum = true; st.CurrentNum = n1; st.Prefix = p1; st.Suffix = s1; st.Format = fmt1;
            }
            else st.HasNum = false;

            tb.Text = next;
            var slide = new DoubleAnimation(6.0, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(200));
            Timeline.SetDesiredFrameRate(slide, 30);
            Timeline.SetDesiredFrameRate(fade, 30);
            tr.BeginAnimation(TranslateTransform.YProperty, slide);
            tb.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private static bool TryParseMetric(string s, out string prefix, out double value, out string format, out string suffix)
    {
        prefix = ""; value = 0; format = "0"; suffix = "";
        if (string.IsNullOrWhiteSpace(s) || s.Contains(':') || s.Count(c => c == '.') > 1) return false; // skip IPs and timestamps
        var m = NumPattern.Match(s);
        if (!m.Success) return false;
        var numStr = m.Groups["num"].Value;
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        prefix = m.Groups["pre"].Value;
        suffix = m.Groups["suf"].Value;
        var dot = numStr.IndexOf('.');
        format = dot >= 0 && numStr.Length - dot - 1 > 0
            ? "0." + new string('0', numStr.Length - dot - 1)
            : "0";
        return true;
    }
}
