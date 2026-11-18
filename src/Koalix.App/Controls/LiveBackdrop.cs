using System.Windows;
using System.Windows.Media;
using Koalix.App.Services;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.38 — the animated dashboard background (user: «پس‌زمینهٔ متحرک داشبورد: ذرات یا موج ملایمی که با وصل شدن
/// جان می‌گیرد»). Slow drifting accent particles plus two soft sine waves along the bottom. Disconnected it barely
/// breathes; connected it wakes up — the particles speed up, brighten and link with faint lines, and the waves rise
/// and quicken with the live download speed (<see cref="ThemeService.LiveActivity"/>). Renders at ~30 fps only while
/// visible on screen, honours the Effects switch and its own Settings toggle, and never takes input.
/// </summary>
public sealed class LiveBackdrop : FrameworkElement
{
    public const string SettingKey = "ui.liveBg";
    // v0.4.52 — selectable animated backgrounds (user: «بک گراند های متحرک بیشتری و اضافه کنی و قابل انتخاب باشن»):
    // particles (the classic), mesh (an always-linked constellation), orbits (rotating rounded-square rings with
    // riders), meteors (diagonal streaks) — or off. ui.bgKind is the setting; ui.liveBg stays in sync with it.
    // v0.4.54 — «پس زمینه متحرک هارو بیشتر کن 5 تا دیگه اضافه کن»: +5 skies — aurora (شفق قطبی: three hanging
    // curtains of light), rain (باران: steep falling streaks), bubbles (حباب‌ها: rising ring bubbles that wobble),
    // starfield (آسمان ستاره‌ای: parallax stars streaming outward from the centre), fireflies (کرم شب‌تاب: slow
    // glowing wanderers that blink).
    public static string StaticKind { get; private set; } = "particles";
    public static event Action? KindChanged;
    public static void PushKind(string kind)
    {
        // v0.4.56 — «پس زمینه متحرک 5 تا دیگه بیار با خلاقیت بیشتر»: +5 skies — grid (شبکهٔ نئون سینث‌ویو), waves
        // (امواج نور), nebula (سحابی), snow (برف), kaleido (کالیدوسکوپ).
        kind = kind is "mesh" or "orbits" or "meteors" or "aurora" or "rain" or "bubbles" or "starfield" or "fireflies"
            or "grid" or "waves" or "nebula" or "snow" or "kaleido" or "off"
            ? kind : "particles";
        if (kind == StaticKind) return;
        StaticKind = kind;
        KindChanged?.Invoke();
    }
    private static string Kind => StaticKind;

    private struct Met { public double X, Y, V, L; }
    private readonly Met[] _mets = new Met[14];
    /// <summary>The Settings toggle (default on). Effects off also stops it.</summary>
    public static bool Allowed { get; set; } = true;

    public static readonly DependencyProperty IsLiveProperty =
        DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(LiveBackdrop), new PropertyMetadata(false));
    /// <summary>Bound to the dashboard's IsConnected.</summary>
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    private struct Dot { public double X, Y, Vx, Vy, R, Phase; }
    private readonly Dot[] _dots = new Dot[34];
    private readonly Random _rnd = new(7);
    private bool _hooked;
    private TimeSpan _last;
    private double _t, _energy, _wave;

    public LiveBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        for (var i = 0; i < _dots.Length; i++)
            _dots[i] = new Dot
            {
                X = _rnd.NextDouble(), Y = _rnd.NextDouble(),
                Vx = (_rnd.NextDouble() - 0.5) * 0.012, Vy = (_rnd.NextDouble() - 0.5) * 0.010,
                R = 1.2 + _rnd.NextDouble() * 2.2, Phase = _rnd.NextDouble() * Math.PI * 2,
            };
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Loaded += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
        ResetWeather();
        KindChanged += ResetWeather; // a kind switch rebuilds the sky (single-window app; one live instance)
        Unloaded += (_, _) => KindChanged -= ResetWeather;

    void ResetWeather()
    {
        for (var i = 0; i < _mets.Length; i++)
            _mets[i] = new Met { X = _rnd.NextDouble() * 1.3 - 0.15, Y = _rnd.NextDouble() * 1.2 - 0.1, V = 0.10 + _rnd.NextDouble() * 0.16, L = 0.10 + _rnd.NextDouble() * 0.14 };
        InvalidateVisual();
    }
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) { _last = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; }
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        var now = re.RenderingTime;
        if (_last != TimeSpan.Zero && (now - _last).TotalMilliseconds < 32) return; // ~30 fps is plenty for a backdrop
        var dt = _last == TimeSpan.Zero ? 0.033 : Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        var window = Window.GetWindow(this);
        if (window is { WindowState: WindowState.Minimized } || !Fx.Enabled || !Allowed)
        {
            if (Opacity > 0) InvalidateVisual();
            _energy = 0;
            return;
        }
        // energy glides toward the target: ~0.15 idle, 0.55-1 connected (more with traffic)
        var target = IsLive ? 0.55 + 0.45 * ThemeService.LiveActivity : 0.15;
        _energy += (target - _energy) * Math.Min(1, dt * 1.6);
        _wave += dt * (0.5 + 2.2 * _energy);
        _t += dt;
        if (Kind == "meteors" || Kind == "rain")
            for (var i = 0; i < _mets.Length; i++)
            {
                ref var m = ref _mets[i];
                if (Kind == "rain") { m.Y += m.V * 2.1 * dt; m.X += m.V * dt * 0.07; } // v0.4.54 — steep, fast drops
                else { m.Y += m.V * dt; m.X += m.V * dt * 0.35; }
                if (m.Y > 1.15) { m.Y = -0.15; m.X = _rnd.NextDouble() * 1.3 - 0.15; m.V = 0.10 + _rnd.NextDouble() * 0.16; m.L = 0.10 + _rnd.NextDouble() * 0.14; }
            }
        if (Kind == "bubbles") // v0.4.54 — bubbles rise, wobbling sideways, and respawn at the bottom
            for (var i = 0; i < _dots.Length; i++)
            {
                ref var d = ref _dots[i];
                d.Y -= dt * (0.03 + d.R * 0.014) * (0.6 + _energy);
                d.X += Math.Sin(_t * (0.7 + d.Phase) + i) * dt * 0.012;
                if (d.Y < -0.06) { d.Y = 1.06; d.X = _rnd.NextDouble(); }
            }
        if (Kind == "starfield") // v0.4.54 — stars stream outward from the centre (depth = distance)
            for (var i = 0; i < _dots.Length; i++)
            {
                ref var d = ref _dots[i];
                var f = 1 + dt * (0.05 + 0.30 * _energy);
                d.X = 0.5 + (d.X - 0.5) * f; d.Y = 0.5 + (d.Y - 0.5) * f;
                if (d.X < -0.05 || d.X > 1.05 || d.Y < -0.05 || d.Y > 1.05)
                {
                    d.X = 0.5 + (_rnd.NextDouble() - 0.5) * 0.10; d.Y = 0.5 + (_rnd.NextDouble() - 0.5) * 0.10;
                }
            }
        if (Kind == "snow") // v0.4.56 — flakes drift down with a lazy sway and respawn on top
            for (var i = 0; i < _dots.Length; i++)
            {
                ref var d = ref _dots[i];
                d.Y += dt * (0.025 + d.R * 0.011) * (0.55 + 0.45 * _energy);
                d.X += Math.Sin(_t * (0.5 + d.Phase * 0.4) + i) * dt * 0.016;
                if (d.Y > 1.06) { d.Y = -0.06; d.X = _rnd.NextDouble(); }
            }
        // fireflies ride the generic wander below; aurora is pure time
        var speed = 0.35 + 2.4 * _energy;
        for (var i = 0; i < _dots.Length; i++)
        {
            ref var d = ref _dots[i];
            d.X += d.Vx * speed * dt * 4; d.Y += d.Vy * speed * dt * 4;
            if (d.X < -0.05) d.X = 1.05; else if (d.X > 1.05) d.X = -0.05;
            if (d.Y < -0.05) d.Y = 1.05; else if (d.Y > 1.05) d.Y = -0.05;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 10 || h < 10 || !Fx.Enabled || !Allowed) return;
        var accent = TryFindResource("AccentColor") is Color a ? a : Color.FromRgb(0x7C, 0x5C, 0xFF);
        var accent2 = TryFindResource("Accent2Color") is Color a2 ? a2 : accent;
        var e = _energy;
        if (Kind == "off") return;

        if (Kind == "orbits") DrawOrbits(dc, w, h, accent, accent2, e);
        if (Kind == "aurora") DrawAurora(dc, w, h, accent, accent2, e); // v0.4.54
        // v0.4.56 — the four time/pool-driven skies; snow keeps the particle field below
        if (Kind == "grid") DrawGrid(dc, w, h, accent, accent2, e);
        if (Kind == "waves") DrawWaves(dc, w, h, accent, accent2, e);
        if (Kind == "nebula") DrawNebula(dc, w, h, accent, accent2, e);
        if (Kind == "kaleido") DrawKaleido(dc, w, h, accent, accent2, e);
        if (Kind is "grid" or "waves" or "nebula" or "kaleido") return;

        // particles + constellation links ("mesh" is always linked; "particles" links once awake)
        var linkDist = Math.Min(w, h) * 0.18;
        if ((Kind == "mesh" || e > 0.3) && Kind != "meteors" && Kind != "orbits" && Kind is not ("grid" or "waves" or "nebula" or "kaleido"))
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(40 * (e - 0.3) / 0.7), accent.R, accent.G, accent.B)), 0.8);
            pen.Freeze();
            for (var i = 0; i < _dots.Length; i++)
                for (var j = i + 1; j < _dots.Length; j++)
                {
                    var dx = (_dots[i].X - _dots[j].X) * w; var dy = (_dots[i].Y - _dots[j].Y) * h;
                    if (dx * dx + dy * dy < linkDist * linkDist)
                        dc.DrawLine(pen, new Point(_dots[i].X * w, _dots[i].Y * h), new Point(_dots[j].X * w, _dots[j].Y * h));
                }
        }
        for (var i = 0; i < _dots.Length; i++)
        {
            var d = _dots[i];
            if (Kind == "snow") // v0.4.56 — soft white flakes with a faint halo
            {
                var sa = (byte)Math.Clamp(70 + 110 * e, 0, 255);
                var fb = new SolidColorBrush(Color.FromArgb(sa, 255, 255, 255)); fb.Freeze();
                var hb2 = new SolidColorBrush(Color.FromArgb((byte)(sa / 4), 255, 255, 255)); hb2.Freeze();
                dc.DrawEllipse(hb2, null, new Point(d.X * w, d.Y * h), d.R * 2.6, d.R * 2.6);
                dc.DrawEllipse(fb, null, new Point(d.X * w, d.Y * h), d.R * 0.85, d.R * 0.85);
                continue;
            }
            var tw = 0.6 + 0.4 * Math.Sin(_t * (0.8 + e * 2) + d.Phase);
            var alpha = (byte)Math.Clamp((22 + 90 * e) * tw, 0, 255);
            var c = i % 3 == 0 ? accent2 : accent;
            var r = d.R * (0.8 + 0.5 * e);
            if (Kind == "bubbles") // v0.4.54 — rings, not filled dots
            {
                var pb = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(40 + 80 * e, 0, 255), c.R, c.G, c.B)); pb.Freeze();
                var pen = new Pen(pb, 1.2); pen.Freeze();
                dc.DrawRoundedRectangle(null, pen, new Rect(d.X * w - r * 1.6, d.Y * h - r * 1.6, r * 3.2, r * 3.2), r * 1.6, r * 1.6);
                continue;
            }
            if (Kind == "starfield") // v0.4.54 — brighter when closer to the viewer (farther from centre)
            {
                var dist = Math.Sqrt(Math.Pow(d.X - 0.5, 2) + Math.Pow(d.Y - 0.5, 2));
                var sa = (byte)Math.Clamp((30 + 150 * Math.Min(1, dist * 2.4)) * (0.7 + 0.3 * e), 0, 255);
                var b2 = new SolidColorBrush(Color.FromArgb(sa, c.R, c.G, c.B)); b2.Freeze();
                dc.DrawEllipse(b2, null, new Point(d.X * w, d.Y * h), r * 0.8, r * 0.8);
                continue;
            }
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze();
            dc.DrawEllipse(b, null, new Point(d.X * w, d.Y * h), r, r);
            if (Kind == "fireflies") // v0.4.54 — a soft halo around every wanderer
            {
                var hb = new SolidColorBrush(Color.FromArgb((byte)(alpha / 3), c.R, c.G, c.B)); hb.Freeze();
                dc.DrawEllipse(hb, null, new Point(d.X * w, d.Y * h), r * 3.2, r * 3.2);
            }
        }

        if (Kind == "rain") // v0.4.54 — thin steep drops
            for (var i = 0; i < _mets.Length; i++)
            {
                var m = _mets[i];
                var x = m.X * w; var y = m.Y * h;
                var tail = m.L * h * 0.55;
                var c = i % 2 == 0 ? accent : accent2;
                var g = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb((byte)(70 + 60 * e), c.R, c.G, c.B), new Point(0, 0), new Point(0, 1));
                g.MappingMode = BrushMappingMode.Absolute;
                g.StartPoint = new Point(x, y - tail); g.EndPoint = new Point(x, y);
                g.Freeze();
                var pen = new Pen(g, 1.0); pen.Freeze();
                dc.DrawLine(pen, new Point(x, y - tail), new Point(x, y));
            }
        else if (Kind == "meteors")
            for (var i = 0; i < _mets.Length; i++)
            {
                var m = _mets[i];
                var x = m.X * w; var y = m.Y * h;
                var tail = m.L * h;
                var c = i % 2 == 0 ? accent : accent2;
                var g = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb((byte)(110 + 70 * e), c.R, c.G, c.B), new Point(0, 0), new Point(1, 1));
                g.MappingMode = BrushMappingMode.Absolute;
                g.StartPoint = new Point(x - tail * 0.35, y - tail); g.EndPoint = new Point(x, y);
                g.Freeze();
                var pen = new Pen(g, 1.3); pen.Freeze();
                dc.DrawLine(pen, new Point(x - tail * 0.35, y - tail), new Point(x, y));
            }

        if (Kind == "orbits" || Kind is "grid" or "waves" or "kaleido") return; // those skies replace the bottom waves (nebula keeps them — cosmic ground)

        // two soft waves along the bottom
        for (var k = 0; k < 2; k++)
        {
            var amp = h * (0.018 + 0.045 * e) * (k == 0 ? 1 : 0.7);
            var baseY = h * (0.86 - 0.05 * e - k * 0.035);
            var c = k == 0 ? accent : accent2;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                const int steps = 48;
                for (var s = 0; s <= steps; s++)
                {
                    var x = w * s / steps;
                    var y = baseY + amp * Math.Sin(x / w * Math.PI * (2.2 + k) + _wave * (k == 0 ? 1 : -1.3) + k);
                    ctx.LineTo(new Point(x, y), true, true);
                }
                ctx.LineTo(new Point(w, h), true, true);
            }
            g.Freeze();
            var fill = new LinearGradientBrush(Color.FromArgb((byte)(18 + 34 * e), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, g);
        }
    }

    /// <summary>v0.4.54 — the «شفق قطبی» sky: three curtains of light hanging from the top edge, their lower
    /// edge waving slowly; hue slides from accent to accent2 across the bands.</summary>
    private void DrawAurora(DrawingContext dc, double w, double h, Color accent, Color accent2, double e)
    {
        for (var k = 0; k < 3; k++)
        {
            var c = k == 1 ? accent2 : accent;
            var baseY = h * (0.14 + 0.10 * k);
            var amp = h * (0.035 + 0.02 * k) * (0.7 + 0.6 * e);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, 0), true, true);
                const int steps = 40;
                for (var s = 0; s <= steps; s++)
                {
                    var x = w * s / steps;
                    var y = baseY + amp * Math.Sin(x / w * Math.PI * (1.6 + 0.7 * k) + _t * (0.4 + 0.18 * k) + k * 2.1);
                    ctx.LineTo(new Point(x, y), true, true);
                }
                ctx.LineTo(new Point(w, 0), true, true);
            }
            g.Freeze();
            var fill = new LinearGradientBrush(Color.FromArgb((byte)(26 + 30 * e), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, g);
        }
    }

    /// <summary>v0.4.52 — the «مدارهای چرخان» sky: four rounded-square rings rotating around the centre (each at
    /// its own speed and direction) with a rider dot on every ring — a circular orbit, no ovals anywhere.</summary>
    private void DrawOrbits(DrawingContext dc, double w, double h, Color accent, Color accent2, double e)
    {
        var cx = w / 2; var cy = h / 2;
        var baseR = Math.Min(w, h) * 0.15;
        for (var k = 0; k < 4; k++)
        {
            var r = baseR * (1.15 + k * 0.6);
            var side = r * 1.4142; // the square's diagonal spans the orbit
            var rect = new Rect(cx - side / 2, cy - side / 2, side, side);
            var angle = _t * (5 + k * 3.2) * (k % 2 == 0 ? 1 : -1) + k * 23;
            var col = k % 2 == 0 ? accent : accent2;
            dc.PushTransform(new RotateTransform(angle, cx, cy));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(24 + 44 * e), col.R, col.G, col.B)), 1.1);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, rect, 8, 8);
            var dot = new SolidColorBrush(Color.FromArgb((byte)(80 + 130 * e), accent2.R, accent2.G, accent2.B)); dot.Freeze();
            var dr = 2.2 + k * 0.45;
            dc.DrawEllipse(dot, null, new Point(cx, cy - side / 2), dr, dr); // the rider rides the rotation = orbit
            dc.Pop();
        }
    }

    /* ── v0.4.56 — +4 skies: neon grid, light waves, nebula, kaleidoscope (snow rides the particle field) ── */

    private void DrawGrid(DrawingContext dc, double w, double h, Color accent, Color accent2, double e)
    {
        var horizon = h * 0.62;
        var glow = new Pen(new SolidColorBrush(Color.FromArgb((byte)(90 + 60 * e), accent.R, accent.G, accent.B)), 1.6); glow.Freeze();
        dc.DrawLine(glow, new Point(0, horizon), new Point(w, horizon));
        var vb = new SolidColorBrush(Color.FromArgb((byte)(30 + 30 * e), accent2.R, accent2.G, accent2.B)); vb.Freeze();
        var vpen = new Pen(vb, 1.0); vpen.Freeze();
        for (var i = -10; i <= 10; i++)
            dc.DrawLine(vpen, new Point(w / 2, horizon), new Point(w / 2 + i * w * 0.115, h));
        var hb = new SolidColorBrush(Color.FromArgb((byte)(38 + 44 * e), accent.R, accent.G, accent.B)); hb.Freeze();
        var hpen = new Pen(hb, 1.0); hpen.Freeze();
        for (var k = 0; k < 9; k++)
        {
            var pr = (k / 9.0 + _t * 0.06) % 1.0;
            var y = horizon + (h - horizon) * pr * pr; // accelerating toward the viewer — the synthwave floor
            dc.DrawLine(hpen, new Point(0, y), new Point(w, y));
        }
    }

    private void DrawWaves(DrawingContext dc, double w, double h, Color accent, Color accent2, double e)
    {
        for (var k = 0; k < 5; k++)
        {
            var c = k % 2 == 0 ? accent : accent2;
            var baseY = h * (0.30 + 0.11 * k);
            var amp = h * (0.05 + 0.012 * k) * (0.8 + 0.4 * e);
            var b = new SolidColorBrush(Color.FromArgb((byte)(30 + 26 * e), c.R, c.G, c.B)); b.Freeze();
            var pen = new Pen(b, 1.6 - 0.18 * k); pen.Freeze();
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                const int steps = 28;
                var first = true;
                for (var i = 0; i <= steps; i++)
                {
                    var x = i / (double)steps * w;
                    var y = baseY + Math.Sin(i / (double)steps * Math.PI * 2 * (1.2 + 0.3 * k) + _t * (0.7 + 0.22 * k) + k * 1.7) * amp;
                    if (first) { ctx.BeginFigure(new Point(x, y), false, false); first = false; }
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    private void DrawNebula(DrawingContext dc, double w, double h, Color accent, Color accent2, double e)
    {
        for (var i = 0; i < 8; i++)
        {
            var d = _dots[i];
            var c = i % 2 == 0 ? accent : accent2;
            var rr = Math.Min(w, h) * (0.16 + 0.05 * (i % 3));
            var g = new RadialGradientBrush(Color.FromArgb((byte)(24 + 22 * e), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B))
            {
                Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5,
            };
            g.Freeze();
            dc.DrawEllipse(g, null, new Point(d.X * w, d.Y * h), rr, rr * 0.8);
        }
        // a handful of fixed twinkling stars behind the clouds
        for (var i = 0; i < 26; i++)
        {
            var fx = (i * 37 % 100) / 100.0;
            var fy = (i * 61 % 100) / 100.0;
            var tw = 0.5 + 0.5 * Math.Sin(_t * (0.6 + i % 5 * 0.2) + i);
            var b = new SolidColorBrush(Color.FromArgb((byte)(40 + 70 * tw * e), 255, 255, 255)); b.Freeze();
            dc.DrawEllipse(b, null, new Point(fx * w, fy * h), 1.1, 1.1);
        }
    }

    private void DrawKaleido(DrawingContext dc, double w, double h, Color accent, Color accent2, double e)
    {
        var cx = w / 2;
        var cy = h / 2;
        var R = Math.Min(w, h) * 0.36;
        const int petals = 8;
        for (var s = 0; s < petals; s++)
        {
            var a = s * 2 * Math.PI / petals + _t * 0.3;
            var a2 = a + 2 * Math.PI / petals * 0.5;
            var c = s % 2 == 0 ? accent : accent2;
            var b = new SolidColorBrush(Color.FromArgb((byte)(26 + 26 * e), c.R, c.G, c.B)); b.Freeze();
            var pen = new Pen(b, 1.2); pen.Freeze();
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(cx, cy), false, false);
                ctx.BezierTo(
                    new Point(cx + R * 0.55 * Math.Cos(a + 0.5), cy + R * 0.55 * Math.Sin(a + 0.5)),
                    new Point(cx + R * 0.85 * Math.Cos(a2 - 0.5), cy + R * 0.85 * Math.Sin(a2 - 0.5)),
                    new Point(cx + R * Math.Cos(a2), cy + R * Math.Sin(a2)), true, false);
                ctx.BezierTo(
                    new Point(cx + R * 0.6 * Math.Cos(a2 + 0.4), cy + R * 0.6 * Math.Sin(a2 + 0.4)),
                    new Point(cx + R * 0.3 * Math.Cos(a2), cy + R * 0.3 * Math.Sin(a2)),
                    new Point(cx, cy), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        var rr = R * 0.30 * (1 + 0.06 * Math.Sin(_t * 1.3));
        var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(50 + 40 * e), accent.R, accent.G, accent.B)), 1.4); ring.Freeze();
        dc.DrawEllipse(null, ring, new Point(cx, cy), rr, rr);
    }
}
