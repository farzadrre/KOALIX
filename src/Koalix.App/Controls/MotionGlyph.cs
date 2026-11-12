using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Koalix.Core;
using Serilog.Events;
// ImplicitUsings pulls in System.IO — alias the WPF shape so "Path" stays unambiguous.
using Path = System.Windows.Shapes.Path;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.19 — the animated section logos («لوگوها») the user asked for, drawn as live vector icons instead of
/// static font glyphs. v0.4.22 (user: «کاربر وقتی کلیک کنه روشون مثلا تونل زنی انتخابیو کلیک کنن اون فلش ها
/// متحرک بشن»): the motion belongs to the CLICK — a glyph animates while its section is SELECTED and stands
/// still the rest of the time (flip <see cref="IsActive"/>).
/// v0.4.23 (user: «بازرس مسیریابی همچنان ثابته و متحرک نیست» + «تونل زنی هم انیمیشنش اصلا دیده نمیشه»):
/// activation no longer depends on any single XAML trigger — the glyph hooks the owning ListBoxItem's
/// Selected/Unselected events directly, keeps the IsSelected DataTrigger as a second signal, and self-heals
/// on IsVisibleChanged/SizeChanged.
/// v0.4.24 (approved list: «فقط هنگام hover یا وقتی آن صفحه باز است حرکت کند»): a glyph now also comes alive
/// while its nav row is HOVERED — two independent flags (selected + hover), whichever is true runs the loop.
/// The routing inspector got a brand-new animated logo: a viewfinder frame with a route line and a bright
/// packet constantly travelling through it (node-to-node), arrival blink on the destination node.
/// v0.4.25 (user: «موقع باز شدن اپ ارور داد — InvalidCastException: Specified cast is not valid»): THE STARTUP
/// CRASH FIX. InspectorIcon hard-cast «(PathGeometry)Geometry.Parse(route)» — but Geometry.Parse ALWAYS
/// returns a StreamGeometry, so the cast threw the moment the glyph loaded with real pixels and the global
/// handler declared the app unable to start. The route geometry is now created via
/// PathGeometry.CreateFromGeometry (the sanctioned conversion, guarded by RuntimeCastGuardTests), and Build()
/// itself is failure-proof: ANY future vector-icon exception degrades to the plain font glyph (with a log
/// line) instead of taking the whole app down at startup.
/// v0.4.26 (user: «بازم لوگو بازرس مسیر و عوض کن»): a THIRD design — a three-node zigzag route whose dashes
/// MARCH toward the destination, and a packet that HOPS node-to-node (every landing flashes the node).
///  - <c>Nav.Split</c>     (تونل‌زنی انتخابی): two swap arrows gliding against their own directions + packets.
///  - <c>Nav.Inspector</c> (بازرس مسیریابی): hopping packet on a marching-dash route (v0.4.26).
///  - <c>Nav.Ping</c>      (پایش پینگ): a signal dot with waves that ripple and shiver.
///  - <c>Nav.Logs</c>      (گزارش‌ها): a report sheet whose bars rise and fall.
///  - <c>Nav.Gaming</c>    (گیمینگ): a gamepad that steers, then has its two buttons pressed (v0.4.46).
/// Any other key renders nothing — the normal font glyph stays visible (see MainWindow's nav template).
/// All motion honours <see cref="Fx.Enabled"/>, runs at 30 fps, and stops on unload/deactivation.
/// </summary>
public sealed class MotionGlyph : ContentControl
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(nameof(Key), typeof(string), typeof(MotionGlyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnKeyChanged));
    public string? Key { get => (string?)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    /// <summary>v0.4.22 — THE CLICK RULE: true while the glyph's section is the open one. The vector icon is
    /// always drawn; its storyboard runs only while this is true (and Fx.Enabled).
    /// v0.4.24: hover animates too — see <c>_hover</c>; this property remains the page-open signal.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(MotionGlyph), new PropertyMetadata(false, OnIsActiveChanged));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private string? _builtKey;

    private Storyboard? _story;
    /// <summary>v0.4.24 — the two independent live signals: the section is open (selected) OR the nav row is hovered.</summary>
    private bool _selected, _hover;
    private bool Active => _selected || _hover;
    private bool _running;
    private ListBoxItem? _hookedItem;

    public MotionGlyph()
    {
        Focusable = false;
        IsTabStop = false;
        Loaded += (_, _) => { TryHookItem(); Build(); Apply(); };
        Unloaded += (_, _) => { _hookedItem = null; Stop(); };
        // v0.4.23 — the glyph starts COLLAPSED inside the nav template and becomes visible via a Key trigger;
        // whichever of these fires first with real pixels rebuilds and (re)starts the storyboard.
        IsVisibleChanged += (_, _) => { if (IsVisible) { TryHookItem(); Build(); Apply(); } else Stop(); };
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MotionGlyph g && g.IsLoaded) { g.Stop(); g.Build(); g.Apply(); }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MotionGlyph g) { g._selected = (bool)e.NewValue; g.Apply(); }
    }

    /// <summary>v0.4.23 — direct selection wiring (belt to the DataTrigger's suspenders) + v0.4.24 hover wiring:
    /// the nav ListBoxItem's Selected/Unselected/MouseEnter/MouseLeave events drive the two live flags. If the
    /// ancestor isn't resolved yet (binding/visual-tree timing), the Loaded/IsVisibleChanged handlers retry.</summary>
    private void TryHookItem()
    {
        if (_hookedItem is not null) { SyncFromItem(); return; }
        var item = FindListBoxItem(this);
        if (item is null) return;
        _hookedItem = item;
        item.Selected += (_, _) => { _selected = true; Apply(); };
        item.Unselected += (_, _) => { _selected = false; Apply(); };
        // v0.4.24 (approved list): the logo also moves while the row is hovered — even on other pages.
        item.MouseEnter += (_, _) => { _hover = true; Apply(); };
        item.MouseLeave += (_, _) => { _hover = false; Apply(); };
        SyncFromItem();
    }

    private void SyncFromItem()
    {
        if (_hookedItem is null) return;
        var was = _selected;
        _selected = _hookedItem.IsSelected;
        if (was != _selected || !_running && Active) Apply();
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ListBoxItem item) return item;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Stop()
    {
        if (_story is null || !_running) return;
        try { _story.Remove(this); } catch { }
        _running = false;
    }

    /// <summary>Starts or stops the looping storyboard according to selected/hover/Fx.Enabled (the icon itself stays).</summary>
    private void Apply()
    {
        Stop();
        if (!IsLoaded || !Active || !Fx.Enabled) return;
        if (Content is null) Build();
        if (_story is null) return;               // key without a vector icon → nothing to animate
        Timeline.SetDesiredFrameRate(_story, 30);
        _story.Begin(this, true);
        _running = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // v0.4.23 — rebuild whenever real pixels arrive (Loaded often fires while the glyph is still 0×0 inside
        // the collapsed nav slot) and ALWAYS (re)apply: if IsActive already flipped, this is the call that starts it.
        if (IsLoaded && ActualWidth > 4 && ActualHeight > 4) { Build(); Apply(); }
    }

    private void Build()
    {
        if (!IsLoaded || ActualWidth < 4 || ActualHeight < 4) return;
        Stop();
        // Rebuilding on every size/visibility tick must not tear down a running animation for nothing —
        // only rebuild when the content is actually missing (first show) or the key changed.
        if (Content is not null && _builtKey == Key) return;
        // v0.4.25 — HARDENING: a vector icon must never take the app down. The 0.4.24 startup crash taught
        // this the hard way (a bad Geometry cast inside InspectorIcon threw on Loaded → "KOALIX could not
        // start"). From now on ANY build failure falls back to the plain font glyph and keeps the app alive.
        try
        {
            _builtKey = Key;
            _story = null;
            var w = ActualWidth; var h = ActualHeight;
            Canvas? canvas;
            Storyboard? sb = null;
            switch (Key ?? "")
            {
                case "Nav.Split": (canvas, sb) = SplitIcon(w, h); break;
                case "Nav.Inspector": (canvas, sb) = InspectorIcon(w, h); break;
                case "Nav.Ping": (canvas, sb) = PingIcon(w, h); break;
                case "Nav.Logs": (canvas, sb) = LogsIcon(w, h); break;
                case "Nav.About": (canvas, sb) = AboutIcon(w, h); break;
                case "Nav.Gaming": (canvas, sb) = GamingIcon(w, h); break;
                default: Content = null; return; // every other section keeps its font glyph
            }
            Content = canvas;
            _story = sb;                        // Apply() decides whether it actually runs (the click rule)
        }
        catch (Exception ex)
        {
            // Never let the fallback itself throw (the DI root may not even be up yet — we sit on a Loaded event).
            try
            {
                Services.AppHost.Log(LogCategory.Application,
                    $"[MotionGlyph] vector icon '{Key}' failed to build — falling back to the font glyph: {ex.Message}", LogEventLevel.Warning);
            }
            catch { /* logging is best-effort */ }
            Content = null;
            _story = null;
            _builtKey = Key;                    // don't rebuild-loop on a permanently broken icon
        }
    }

    /// <summary>The nav Foreground (subtle → bright on hover/select) flows in from the ListBoxItem; every stroke follows it live.</summary>
    private Path Stroke(Geometry geo, double thickness)
    {
        var p = new Path { Data = geo, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        return p;
    }

    private static Geometry Geo(string path) { var g = Geometry.Parse(path); g.Freeze(); return g; }

    private static string F(double x) => x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /* ══════ تونل‌زنی انتخابی — each arrow glides slowly AGAINST its own direction, then returns home ══════ */

    private (Canvas, Storyboard) SplitIcon(double w, double h)
    {
        var c = new Canvas();
        var mid = h / 2;
        var topY = mid - h * 0.30;
        var botY = mid + h * 0.30;
        var head = h * 0.22;

        // Top arrow → points RIGHT; it drifts LEFT (backwards against its own direction) and comes back.
        var top = new Canvas();
        top.Children.Add(Stroke(Geo($"M {F(w * 0.05)},{F(topY)} H {F(w * 0.74)}"), 1.9));
        top.Children.Add(Stroke(Geo($"M {F(w * 0.58)},{F(topY - head)} L {F(w * 0.82)},{F(topY)} L {F(w * 0.58)},{F(topY + head)}"), 1.9));
        // Bottom arrow → points LEFT; it drifts RIGHT and comes back.
        var bot = new Canvas();
        bot.Children.Add(Stroke(Geo($"M {F(w * 0.95)},{F(botY)} H {F(w * 0.26)}"), 1.9));
        bot.Children.Add(Stroke(Geo($"M {F(w * 0.42)},{F(botY - head)} L {F(w * 0.18)},{F(botY)} L {F(w * 0.42)},{F(botY + head)}"), 1.9));

        var topShift = new TranslateTransform();
        var botShift = new TranslateTransform();
        top.RenderTransform = topShift;
        bot.RenderTransform = botShift;
        c.Children.Add(top);
        c.Children.Add(bot);

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        // v0.4.20 — a WIDER, faster glide (was ±19% over 3.4 s — too subtle to read as motion): ±28% over 2.2 s.
        var aTop = new DoubleAnimation(0, -w * 0.28, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, EasingFunction = ease };
        var aBot = new DoubleAnimation(0, w * 0.28, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, EasingFunction = ease };
        Storyboard.SetTarget(aTop, topShift); Storyboard.SetTargetProperty(aTop, new PropertyPath(TranslateTransform.XProperty));
        Storyboard.SetTarget(aBot, botShift); Storyboard.SetTargetProperty(aBot, new PropertyPath(TranslateTransform.XProperty));
        sb.Children.Add(aTop); sb.Children.Add(aBot);

        // v0.4.20 — two little packets race along each arrow's track so the motion is unmistakable at a glance.
        // They ride the FIXED tracks (added straight to the root canvas, not the moving arrows).
        void Packet(double x1, double x2, double y, double dur, double delay)
        {
            var size = Math.Max(3, w * 0.13);
            var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(1.6), Opacity = 0 };
            dot.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
            Canvas.SetTop(dot, y - size / 2);
            c.Children.Add(dot);
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.22)) { BeginTime = TimeSpan.FromSeconds(delay) };
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.22)) { BeginTime = TimeSpan.FromSeconds(delay + dur - 0.22) };
            var move = new DoubleAnimation(x1, x2, TimeSpan.FromSeconds(dur)) { BeginTime = TimeSpan.FromSeconds(delay), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(fadeIn, dot); Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(fadeOut, dot); Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(move, dot); Storyboard.SetTargetProperty(move, new PropertyPath("(Canvas.Left)"));
            sb.Children.Add(fadeIn); sb.Children.Add(fadeOut); sb.Children.Add(move);
        }
        Packet(w * 0.10, w * 0.62, topY, 1.6, 0.0);
        Packet(w * 0.62, w * 0.10, botY, 1.6, 0.8);
        return (c, sb);
    }

    /* ══════ بازرس مسیریابی — v0.4.26: a THIRD design, per the user's «بازم لوگو بازرس مسیر و عوض کن».
       A three-node zigzag ROUTE whose dashes keep MARCHING toward the destination, and a packet that HOPS
       node-to-node — every landing flashes that node, the packet blinks at the destination, fades and
       respawns back at the origin. Straight lines + rounded squares only (the no-ovals rule). ══════ */

    private (Canvas, Storyboard) InspectorIcon(double w, double h)
    {
        var c = new Canvas();
        var t = 1.7;                       // stroke thickness

        // three zigzag nodes: bottom-left → top-middle → bottom-right
        var ax = w * 0.10; var ay = h * 0.76;
        var bx = w * 0.50; var by = h * 0.24;
        var cx = w * 0.90; var cy = h * 0.76;

        // the links with MARCHING dashes — the route keeps streaming toward the destination between hops.
        var dashPeriod = 3.5;              // (dash + gap) in stroke-thickness units, matches {2.0, 1.5}
        Path Link(double x1, double y1, double x2, double y2)
        {
            var p = Stroke(Geo($"M {F(x1)},{F(y1)} L {F(x2)},{F(y2)}"), t);
            p.StrokeDashArray = new DoubleCollection { 2.0, 1.5 };
            c.Children.Add(p);
            return p;
        }
        var link1 = Link(ax, ay, bx, by);
        var link2 = Link(bx, by, cx, cy);

        // nodes — rounded squares (the no-ovals rule), lit by the packet's landings
        Border Node(double x, double y, double scale)
        {
            var s = Math.Max(4.5, w * 0.20 * scale);
            var node = new Border { Width = s, Height = s, CornerRadius = new CornerRadius(s * 0.3), Opacity = 0.45 };
            node.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
            Canvas.SetLeft(node, x - s / 2);
            Canvas.SetTop(node, y - s / 2);
            c.Children.Add(node);
            return node;
        }
        var nA = Node(ax, ay, 1.0);
        var nB = Node(bx, by, 1.0);
        var nC = Node(cx, cy, 1.15);       // the destination is slightly bigger

        // the packet — a small glowing square that HOPS from node to node
        var ps = Math.Max(4, w * 0.17);
        var packet = new Border
        {
            Width = ps, Height = ps, CornerRadius = new CornerRadius(ps * 0.3),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 7, ShadowDepth = 0, Opacity = 0.65 },
        };
        packet.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Canvas.SetLeft(packet, ax - ps / 2);
        Canvas.SetTop(packet, ay - ps / 2);
        c.Children.Add(packet);

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        void Add(AnimationTimeline a, DependencyObject target, PropertyPath prop)
        {
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }

        // marching dashes (independent 1.1 s loop — always flowing, even while the packet is mid-hop)
        foreach (var link in new[] { link1, link2 })
            Add(new DoubleAnimation(0, -dashPeriod, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever },
                link, new PropertyPath(Shape.StrokeDashOffsetProperty));

        // the hops — one 2.7 s master cycle: A →(0.85 s)→ B →(0.85 s)→ C, rest + blink, fade, respawn at A
        const double cycle = 2.7, hop = 0.85;
        DoubleAnimationUsingKeyFrames Ride(double from, double mid, double to)
        {
            var ride = new DoubleAnimationUsingKeyFrames();
            ride.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            ride.KeyFrames.Add(new EasingDoubleKeyFrame(mid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hop))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            ride.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hop * 2))));
            ride.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle))));
            ride.Duration = new Duration(TimeSpan.FromSeconds(cycle));
            return ride;
        }
        Add(Ride(ax - ps / 2, bx - ps / 2, cx - ps / 2), packet, new PropertyPath("(Canvas.Left)"));
        Add(Ride(ay - ps / 2, by - ps / 2, cy - ps / 2), packet, new PropertyPath("(Canvas.Top)"));

        // visibility: solid all the way to C, fade out, respawn at A
        var vis = new DoubleAnimationUsingKeyFrames();
        vis.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        vis.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.30))));
        vis.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.45))));
        vis.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle))));
        vis.Duration = new Duration(TimeSpan.FromSeconds(cycle));
        Add(vis, packet, new PropertyPath(UIElement.OpacityProperty));

        // landing flashes: the node the packet just touched flares and settles back
        void Flash(Border node, double land, double decay)
        {
            var f = new DoubleAnimationUsingKeyFrames();
            f.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (land > 0) f.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(land))));
            f.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(land + 0.10))));
            f.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(decay))));
            f.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle))));
            f.Duration = new Duration(TimeSpan.FromSeconds(cycle));
            Add(f, node, new PropertyPath(UIElement.OpacityProperty));
        }
        Flash(nA, 0.0, 0.35);              // the packet is born here
        Flash(nB, hop, hop + 0.5);
        Flash(nC, hop * 2, hop * 2 + 0.6);

        return (c, sb);
    }

    /* ══════ گیمینگ — v0.4.46 (user: «متحرک بودن ایکونش و تغییر بده»): the font gamepad that only "hopped" is now a
       drawn gamepad that is being PLAYED — it steers left and right (the D-pad leans with it), then the two
       buttons are pressed one after the other: each press pops its button, kicks the pad up and flashes three
       rays above it. Drawn in a fixed 19×17 box and scaled, so the Gaming page reuses it large in its header. ══════ */

    private (Canvas, Storyboard) GamingIcon(double w, double h)
    {
        var root = new Canvas { RenderTransform = new ScaleTransform(w / 19, h / 17) };
        var kick = new TranslateTransform();
        var lift = new Canvas { RenderTransform = kick };
        var tilt = new RotateTransform(0, 9.5, 9);
        var pad = new Canvas { RenderTransform = tilt };
        root.Children.Add(lift);
        lift.Children.Add(pad);

        // the body: flat top, round shoulders, two grips
        pad.Children.Add(Stroke(Geo("M 5.2,3.6 H 13.8 C 16.2,3.6 17.3,5.2 17.7,7.6 L 18.2,11.4 C 18.5,13.8 16.3,14.6 14.9,13.1 L 13.4,11.5 H 5.6 L 4.1,13.1 C 2.7,14.6 0.5,13.8 0.8,11.4 L 1.3,7.6 C 1.7,5.2 2.8,3.6 5.2,3.6 Z"), 1.5));

        var lean = new TranslateTransform();
        var dpad = Stroke(Geo("M 6.1,5.9 V 9.3 M 4.4,7.6 H 7.8"), 1.4);
        dpad.RenderTransform = lean;
        pad.Children.Add(dpad);

        // the two action buttons — rounded squares (the no-ovals rule)
        (Border, ScaleTransform) Button(double x, double y)
        {
            const double s = 2.2;
            var pop = new ScaleTransform(1, 1);
            var b = new Border { Width = s, Height = s, CornerRadius = new CornerRadius(0.7), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pop };
            b.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
            Canvas.SetLeft(b, x - s / 2);
            Canvas.SetTop(b, y - s / 2);
            pad.Children.Add(b);
            return (b, pop);
        }
        var (_, popA) = Button(12.0, 8.7);
        var (_, popB) = Button(14.3, 6.5);

        // three rays above the pad — invisible at rest, they flash with every press
        var rays = Stroke(Geo("M 9.5,0.4 V 1.8 M 5.3,0.9 L 6.1,2.1 M 13.7,0.9 L 12.9,2.1"), 1.2);
        rays.Opacity = 0;
        root.Children.Add(rays);

        const double cycle = 2.4;
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        void Keys(DependencyObject target, DependencyProperty prop, params (double At, double Value)[] frames)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromSeconds(cycle)) };
            foreach (var (at, value) in frames)
                a.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, new PropertyPath(prop));
            sb.Children.Add(a);
        }

        // steer: left, right, back to level
        Keys(tilt, RotateTransform.AngleProperty, (0, 0), (0.34, -12), (0.92, 12), (1.26, 0), (cycle, 0));
        Keys(lean, TranslateTransform.XProperty, (0, 0), (0.34, -0.8), (0.92, 0.8), (1.26, 0), (cycle, 0));

        // two presses: button pops, the pad kicks up, the rays flash
        const double first = 1.34, second = 1.62;
        void Press(ScaleTransform pop, double at)
        {
            Keys(pop, ScaleTransform.ScaleXProperty, (0, 1), (at, 1), (at + 0.12, 1.6), (at + 0.30, 1), (cycle, 1));
            Keys(pop, ScaleTransform.ScaleYProperty, (0, 1), (at, 1), (at + 0.12, 1.6), (at + 0.30, 1), (cycle, 1));
        }
        Press(popA, first);
        Press(popB, second);
        Keys(kick, TranslateTransform.YProperty, (0, 0), (first, 0), (first + 0.12, -1.5), (first + 0.26, 0), (second, 0), (second + 0.12, -1.1), (second + 0.26, 0), (cycle, 0));
        Keys(rays, UIElement.OpacityProperty, (0, 0), (first, 0), (first + 0.12, 1), (first + 0.26, 0.25), (second + 0.12, 1), (second + 0.50, 0), (cycle, 0));

        return (root, sb);
    }

    /* ══════ پایش پینگ — the signal waves ripple and shiver ══════ */

    /// <summary>v0.4.38 — About: the KOALIX mark — a hexagon badge that turns (one sixth per cycle, so it loops
    /// seamlessly) around a "K" that breathes, with a spark orbiting the badge.</summary>
    private (Canvas, Storyboard) AboutIcon(double w, double h)
    {
        var c = new Canvas();
        double cx = w / 2, cy = h / 2, r = Math.Min(w, h) * 0.5;
        var hex = new System.Text.StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var a = Math.PI / 3 * i - Math.PI / 2;
            hex.Append(i == 0 ? "M " : " L ").Append(F(cx + r * Math.Cos(a))).Append(',').Append(F(cy + r * Math.Sin(a)));
        }
        hex.Append(" Z");
        var badge = Stroke(Geo(hex.ToString()), 1.5);
        var spin = new RotateTransform(0, cx, cy);
        badge.RenderTransform = spin;
        c.Children.Add(badge);

        var k = r * 0.42;
        var letter = Stroke(Geo($"M {F(cx - k * 0.55)},{F(cy - k)} L {F(cx - k * 0.55)},{F(cy + k)} M {F(cx + k * 0.6)},{F(cy - k)} L {F(cx - k * 0.5)},{F(cy + k * 0.05)} L {F(cx + k * 0.65)},{F(cy + k)}"), 1.6);
        var pop = new ScaleTransform(1, 1, cx, cy);
        letter.RenderTransform = pop;
        c.Children.Add(letter);

        var spark = new Border { Width = 3, Height = 3, CornerRadius = new CornerRadius(1) };
        spark.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Canvas.SetLeft(spark, cx - 1.5);
        Canvas.SetTop(spark, cy - r - 1.5);
        var orbit = new RotateTransform(0, 1.5, r + 1.5);
        spark.RenderTransform = orbit;
        c.Children.Add(spark);

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        void Add(Timeline a, DependencyObject target, DependencyProperty prop)
        {
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, new PropertyPath(prop));
            sb.Children.Add(a);
        }
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        Add(new DoubleAnimation(0, 60, TimeSpan.FromSeconds(2.4)) { EasingFunction = ease }, spin, RotateTransform.AngleProperty);
        Add(new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)), orbit, RotateTransform.AngleProperty);
        Add(new DoubleAnimation(1, 1.18, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, EasingFunction = ease }, pop, ScaleTransform.ScaleXProperty);
        Add(new DoubleAnimation(1, 1.18, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, EasingFunction = ease }, pop, ScaleTransform.ScaleYProperty);
        return (c, sb);
    }

    private (Canvas, Storyboard) PingIcon(double w, double h)
    {
        var c = new Canvas();
        var mid = h / 2;

        // Signal origin: a small filled rounded square (the no-ovals design rule).
        var dot = new Border
        {
            Width = Math.Max(3, w * 0.17), Height = Math.Max(3, w * 0.17),
            CornerRadius = new CornerRadius(1.4),
        };
        dot.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Canvas.SetLeft(dot, w * 0.02);
        Canvas.SetTop(dot, mid - w * 0.085);
        c.Children.Add(dot);

        // Two wave arcs to the right of the dot (right-opening brackets, like a sound/ping ripple).
        Path Arc(double reach, double half)
        {
            var r = Math.Sqrt(reach * reach + half * half) * 0.95;
            return Stroke(Geo($"M {F(reach)},{F(mid - half)} A {F(r)},{F(r)} 0 0 1 {F(reach)},{F(mid + half)}"), 1.7);
        }
        var arc1 = Arc(w * 0.40, h * 0.28);
        var arc2 = Arc(w * 0.74, h * 0.44);
        c.Children.Add(arc1);
        c.Children.Add(arc2);

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        void Add(DoubleAnimation a, DependencyObject target, PropertyPath prop)
        {
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        // The two waves breathe in counter-phase (opacity + a tiny horizontal shiver, same period so the
        // group cycles as one) — unmistakably MOVING waves, not a static pictogram.
        // v0.4.20 — stronger contrast (1 ↔ 0.15) and a faster 0.9 s cycle so the waves read as rippling.
        Add(new DoubleAnimation(1, 0.15, TimeSpan.FromSeconds(0.9)) { EasingFunction = ease }, arc1, new PropertyPath(UIElement.OpacityProperty));
        Add(new DoubleAnimation(0.15, 1, TimeSpan.FromSeconds(0.9)) { EasingFunction = ease }, arc2, new PropertyPath(UIElement.OpacityProperty));
        var wiggle1 = new TranslateTransform();
        var wiggle2 = new TranslateTransform();
        arc1.RenderTransform = wiggle1;
        arc2.RenderTransform = wiggle2;
        Add(new DoubleAnimation(0, w * 0.09, TimeSpan.FromSeconds(0.9)) { EasingFunction = ease }, wiggle1, new PropertyPath(TranslateTransform.XProperty));
        Add(new DoubleAnimation(0, -w * 0.09, TimeSpan.FromSeconds(0.9)) { EasingFunction = ease }, wiggle2, new PropertyPath(TranslateTransform.XProperty));
        return (c, sb);
    }

    /* ══════ گزارش‌ها — the report sheet whose bars rise and fall (v0.4.22, user: «میله ها مثلا بالا پایین شن») ══════ */

    private (Canvas, Storyboard) LogsIcon(double w, double h)
    {
        var c = new Canvas();
        // A little document sheet: portrait page with a folded top-right corner.
        var fold = w * 0.22;
        var page = $"M 0.9,0.9 H {F(w - fold)} L {F(w - 0.9)},{F(fold)} V {F(h - 0.9)} H 0.9 Z";
        c.Children.Add(Stroke(Geo(page), 1.6));
        c.Children.Add(Stroke(Geo($"M {F(w - fold)},{F(fold)} V 0.9 H {F(w - 0.9)}"), 1.3));

        // Four bars on the page, each breathing up/down with its own phase — a live report.
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var n = 4;
        var areaTop = fold + h * 0.10;
        var areaBottom = h - h * 0.14;
        var maxBarH = areaBottom - areaTop;
        var barW = w * 0.11;
        var gap = (w - 0.18 * w * 2 - n * barW) / (n - 1);
        var phases = new[] { 0.0, 0.55, 1.05, 1.5 };
        var spans = new[] { 0.65, 1.0, 0.8, 0.5 };
        for (var i = 0; i < n; i++)
        {
            var bar = new Border
            {
                Width = barW,
                Height = maxBarH,
                CornerRadius = new CornerRadius(1.2, 1.2, 0.4, 0.4),
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform(1, spans[i] * 0.8),
            };
            bar.SetBinding(Border.BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
            Canvas.SetLeft(bar, w * 0.18 + i * (barW + gap));
            Canvas.SetTop(bar, areaTop);
            c.Children.Add(bar);
            var a = new DoubleAnimation(spans[i] * 0.8, Math.Max(0.2, spans[i] * 0.3), TimeSpan.FromSeconds(1.1))
            {
                AutoReverse = true,
                BeginTime = TimeSpan.FromSeconds(phases[i]),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(a, bar);
            Storyboard.SetTargetProperty(a, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            sb.Children.Add(a);
        }
        return (c, sb);
    }
}
