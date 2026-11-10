using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Koalix.App.Localization;
using Koalix.App.ViewModels;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.40 (user: «نقشه جهان باشه و سرورارو نشون بده ... با انیمیشن و موشن مخصوص خودش») — the dashboard server map.
/// A dot-matrix world (a 144×56 land mask, equirectangular) with one glowing pin per country that hosts servers:
/// the pin grows with the server count and takes the colour of its best ping. Pins pop in, breathe with a pulse ring,
/// a slow accent sweep runs across the continents, the selected country wears a spinning dashed ring and, while
/// connected, a comet travels the route from home to the exit country. Clicking a pin runs <see cref="CountryCommand"/>
/// with its <see cref="CountryRow"/> (the view opens the server picker). Everything is drawn in OnRender; the land is
/// cached as a bitmap per size/colour, the animation ticks at ~30 fps only while visible and only when
/// <see cref="Fx.Enabled"/> — with effects off the map is a still image.
/// </summary>
public sealed partial class WorldMap : FrameworkElement
{
    private static Loc L => Loc.I;
    private const int Cols = 144, Rows = 56;
    private const double Step = 2.5, Top = 82.5, MaxHeight0 = 300;

    public static readonly DependencyProperty CountriesProperty = DependencyProperty.Register(nameof(Countries), typeof(IEnumerable), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((WorldMap)d).OnCountriesChanged(e.OldValue, e.NewValue)));
    public static readonly DependencyProperty CountryCommandProperty = DependencyProperty.Register(nameof(CountryCommand), typeof(ICommand), typeof(WorldMap));
    public static readonly DependencyProperty ActiveCodeProperty = DependencyProperty.Register(nameof(ActiveCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedCodeProperty = DependencyProperty.Register(nameof(SelectedCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HomeCodeProperty = DependencyProperty.Register(nameof(HomeCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata("IR", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The <see cref="CountryRow"/> collection (rows without a known centroid are simply not drawn).</summary>
    public IEnumerable? Countries { get => (IEnumerable?)GetValue(CountriesProperty); set => SetValue(CountriesProperty, value); }
    public ICommand? CountryCommand { get => (ICommand?)GetValue(CountryCommandProperty); set => SetValue(CountryCommandProperty, value); }
    /// <summary>Exit country of the live tunnel (null when disconnected) — gets the travelling route.</summary>
    public string? ActiveCode { get => (string?)GetValue(ActiveCodeProperty); set => SetValue(ActiveCodeProperty, value); }
    /// <summary>Country of the selected server card — gets the spinning ring and a faint preview route.</summary>
    public string? SelectedCode { get => (string?)GetValue(SelectedCodeProperty); set => SetValue(SelectedCodeProperty, value); }
    /// <summary>Where the user is (route origin). Defaults to Iran.</summary>
    public string? HomeCode { get => (string?)GetValue(HomeCodeProperty); set => SetValue(HomeCodeProperty, value); }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _introAt, _lastFrame;
    private bool _ticking;
    private Pin? _hover;
    private readonly List<Pin> _pins = [];
    private BitmapSource? _land, _landAccent;
    private (int W, int H, Color Dot, Color Accent, double Dpi) _landKey;

    private sealed record Pin(CountryRow Row, Point At, double Radius, Color Color, int Index);

    public WorldMap()
    {
        FlowDirection = FlowDirection.LeftToRight; // geography never mirrors, even in the RTL layout
        Focusable = false;
        Loaded += (_, _) => { _introAt = Now; UpdateTicking(); };
        Unloaded += (_, _) => StopTicking();
        IsVisibleChanged += (_, _) => { if (IsVisible) _introAt = Now; UpdateTicking(); };
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void OnCountriesChanged(object? oldValue, object? newValue)
    {
        if (oldValue is INotifyCollectionChanged o) o.CollectionChanged -= OnCollectionChanged;
        if (newValue is INotifyCollectionChanged n) n.CollectionChanged += OnCollectionChanged;
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { _hover = null; InvalidateVisual(); }

    // ───────────────────────────── animation loop ─────────────────────────────
    private void UpdateTicking() { if (IsLoaded && IsVisible && Fx.Enabled) StartTicking(); else StopTicking(); }
    private void StartTicking() { if (_ticking) return; _ticking = true; CompositionTarget.Rendering += OnFrame; }
    private void StopTicking() { if (!_ticking) return; _ticking = false; CompositionTarget.Rendering -= OnFrame; }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!Fx.Enabled) { StopTicking(); InvalidateVisual(); return; }
        var t = Now;
        if (t - _lastFrame < 1 / 30.0) return; // ~30 fps is plenty for a breathing map, and half the cost
        _lastFrame = t;
        InvalidateVisual();
    }

    // ───────────────────────────── layout ─────────────────────────────
    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 640 : available.Width;
        var h = Math.Min(w * Rows / Cols, MaxHeight0);
        if (!double.IsInfinity(available.Height)) h = Math.Min(h, available.Height);
        return new Size(w, h);
    }

    /// <summary>Camera → screen frame: <paramref name="v"/> is (centre lon, centre lat, visible lon span).</summary>
    private (double Ox, double Oy, double Cell) Frame((double Lon, double Lat, double Span) v)
    {
        var cell = ActualWidth / (v.Span / Step);
        return (ActualWidth / 2 - (v.Lon + 180) / Step * cell, ActualHeight / 2 - (Top - v.Lat) / Step * cell, cell);
    }

    /// <summary>v0.4.40 — auto-zoom: frame every country that hosts servers (plus home) with some breathing room, so a
    /// Europe-heavy fleet is not a clump of overlapping pins; never tighter than ~100° of longitude, never past the world.</summary>
    private (double Lon, double Lat, double Span) TargetView(IEnumerable<(double Lat, double Lon)> points)
    {
        var w = ActualWidth; var h = ActualHeight;
        const double worldLatTop = Top, worldLatBottom = Top - Rows * Step;
        var pts = points.ToList();
        if (pts.Count == 0 || w < 1 || h < 1) return (0, worldLatTop - Rows * Step / 2, 360);
        double lonMin = pts.Min(x => x.Lon), lonMax = pts.Max(x => x.Lon), latMin = pts.Min(x => x.Lat), latMax = pts.Max(x => x.Lat);
        var span = Math.Max(lonMax - lonMin + 2 * 16, 100);
        span = Math.Max(span, (latMax - latMin + 2 * 11) * w / h);
        span = Math.Min(span, 360);
        var half = span / 2;
        var cx = span >= 360 ? 0 : Math.Clamp((lonMin + lonMax) / 2, -180 + half, 180 - half);
        var halfLat = span * h / w / 2;
        var cy = halfLat * 2 >= worldLatTop - worldLatBottom ? (worldLatTop + worldLatBottom) / 2
            : Math.Clamp((latMin + latMax) / 2, worldLatBottom + halfLat, worldLatTop - halfLat);
        return (cx, cy, span);
    }

    private (double Lon, double Lat, double Span)? _view;
    private (double Ox, double Oy, double Cell) _landFrame;

    private static Point Project((double Ox, double Oy, double Cell) f, double lat, double lon) =>
        new(f.Ox + (lon + 180) / Step * f.Cell, f.Oy + (Top - lat) / Step * f.Cell);

    private static bool IsLand(int row, int col)
    {
        var nib = Convert.ToInt32(Land[row][col >> 2].ToString(), 16);
        return (nib & (8 >> (col & 3))) != 0;
    }

    // ───────────────────────────── theme ─────────────────────────────
    private Color Col(string key, Color fallback) => TryFindResource(key) switch
    {
        Color c => c,
        SolidColorBrush b => b.Color,
        _ => fallback,
    };

    private static Color A(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    private static BitmapSource RenderLand(int w, int h, Color dot, double dpi, (double Ox, double Oy, double Cell) f)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var brush = new SolidColorBrush(dot); brush.Freeze();
            var r = Math.Clamp(f.Cell * 0.3, 0.6, 2.4);
            for (var row = 0; row < Rows; row++)
            {
                var y = f.Oy + (row + 0.5) * f.Cell;
                if (y < -f.Cell || y > h + f.Cell) continue;
                for (var col = 0; col < Cols; col++)
                {
                    var x = f.Ox + (col + 0.5) * f.Cell;
                    if (x < -f.Cell || x > w + f.Cell || !IsLand(row, col)) continue;
                    dc.DrawEllipse(brush, null, new Point(x, y), r, r);
                }
            }
        }
        var scale = dpi / 96.0;
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    // ───────────────────────────── input ─────────────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        Pin? best = null; var bestD = double.MaxValue;
        foreach (var pin in _pins)
        {
            var d = (pin.At - p).Length;
            if (d < Math.Max(pin.Radius + 6, 11) && d < bestD) { best = pin; bestD = d; }
        }
        if (!ReferenceEquals(best, _hover)) { _hover = best; Cursor = best is null ? null : Cursors.Hand; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null) { _hover = null; Cursor = null; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_hover is { } pin && CountryCommand is { } cmd && cmd.CanExecute(pin.Row)) { cmd.Execute(pin.Row); e.Handled = true; }
    }

    // ───────────────────────────── render ─────────────────────────────
    private static double Frac(double v) => v - Math.Floor(v);
    private static double EaseOutBack(double x) { const double c1 = 1.70158, c3 = c1 + 1; x = Math.Clamp(x, 0, 1); return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2); }
    private static Point Bezier(Point a, Point c, Point b, double t) =>
        new((1 - t) * (1 - t) * a.X + 2 * (1 - t) * t * c.X + t * t * b.X, (1 - t) * (1 - t) * a.Y + 2 * (1 - t) * t * c.Y + t * t * b.Y);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 20 || h < 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole surface hit-testable for hover

        var anim = Fx.Enabled;
        var t = Now;
        var rows = Countries?.OfType<CountryRow>().ToList() ?? [];

        // camera: glide toward the framing of the current fleet (snap when effects are off / first frame)
        var targets = rows.Where(r => r.Code.Length == 2 && Centroids.ContainsKey(r.Code)).Select(r => Centroids[r.Code]).ToList();
        if (HomeCode is { Length: 2 } hk && Centroids.TryGetValue(hk, out var hkc)) targets.Add(hkc);
        var goal = TargetView(targets);
        var moving = false;
        if (_view is not { } cur || !anim) _view = goal;
        else
        {
            const double k = 0.14;
            var next = (cur.Lon + (goal.Lon - cur.Lon) * k, cur.Lat + (goal.Lat - cur.Lat) * k, cur.Span + (goal.Span - cur.Span) * k);
            moving = Math.Abs(next.Item3 - goal.Span) > 0.4 || Math.Abs(next.Item1 - goal.Lon) > 0.3 || Math.Abs(next.Item2 - goal.Lat) > 0.3;
            _view = moving ? next : goal;
        }
        var f = Frame(_view.Value);
        var accent = Col("AccentColor", Color.FromRgb(0x7C, 0x5C, 0xFF));
        var subtle = Col("SubtleTextBrush", Color.FromRgb(0x88, 0x88, 0x99));
        var text = Col("TextBrush", Colors.White);
        var good = Col("SuccessBrush", Color.FromRgb(0x2E, 0xCC, 0x71));
        var warn = Col("WarningBrush", Color.FromRgb(0xF3, 0x9C, 0x12));
        var bad = Col("DangerBrush", Color.FromRgb(0xE7, 0x4C, 0x3C));
        var popup = Col("PopupBrush", Color.FromRgb(0x1E, 0x1E, 0x2A));
        var border = Col("StrongBorderBrush", Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        // 1) continents — cached dot matrix, plus an accent sweep that drifts west→east across the land only.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        var key = ((int)w, (int)h, subtle, accent, dpi);
        // while the camera glides the cached bitmap is just re-scaled; it is re-rasterised crisp once the camera settles
        var stale = Math.Abs(_landFrame.Cell - f.Cell) > 0.001 || Math.Abs(_landFrame.Ox - f.Ox) > 0.3 || Math.Abs(_landFrame.Oy - f.Oy) > 0.3;
        if (_land is null || _landKey != key || (stale && !moving))
        {
            _land = RenderLand((int)w, (int)h, A(subtle, 0.42), dpi, f);
            _landAccent = RenderLand((int)w, (int)h, A(accent, 0.95), dpi, f);
            _landKey = key;
            _landFrame = f;
        }
        var s0 = f.Cell / _landFrame.Cell;
        var bounds = new Rect(f.Ox - _landFrame.Ox * s0, f.Oy - _landFrame.Oy * s0, _land.PixelWidth * 96.0 / dpi * s0, _land.PixelHeight * 96.0 / dpi * s0);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawImage(_land, bounds);
        if (anim && _landAccent is not null)
        {
            var sx = -0.35 + Frac(t / 9.0) * 1.7; // band centre in 0..1 of the width (overshoots so it fully exits)
            var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.25) };
            band.GradientStops.Add(new GradientStop(Colors.Transparent, sx - 0.16));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0, 0, 0), sx));
            band.GradientStops.Add(new GradientStop(Colors.Transparent, sx + 0.16));
            dc.PushOpacityMask(band);
            dc.DrawImage(_landAccent, bounds);
            dc.Pop();
        }
        dc.Pop(); // land clip

        // 2) pins
        _pins.Clear();
        var i = 0;
        foreach (var row in rows)
        {
            if (row.Code.Length != 2 || !Centroids.TryGetValue(row.Code, out var c)) continue;
            var radius = Math.Min(4 + 2.1 * Math.Sqrt(Math.Max(1, row.Count)), 13) * Math.Clamp(f.Cell / 5.0, 0.7, 1.3);
            var color = row.BestMs is not { } ms ? accent : ms < 300 ? good : ms < 800 ? warn : bad;
            _pins.Add(new Pin(row, Project(f, c.Lat, c.Lon), radius, color, i++));
        }

        Point? home = HomeCode is { Length: 2 } hc && Centroids.TryGetValue(hc, out var hcc) ? Project(f, hcc.Lat, hcc.Lon) : null;
        var active = _pins.FirstOrDefault(p => string.Equals(p.Row.Code, ActiveCode, StringComparison.OrdinalIgnoreCase));
        Point? activeAt = active?.At ?? (ActiveCode is { Length: 2 } ac && Centroids.TryGetValue(ac, out var acc) ? Project(f, acc.Lat, acc.Lon) : null);
        var selected = _pins.FirstOrDefault(p => string.Equals(p.Row.Code, SelectedCode, StringComparison.OrdinalIgnoreCase));

        // 3) routes: live (connected) = bright marching arc + comet; otherwise a faint preview to the selected country.
        if (home is { } h0)
        {
            if (activeAt is { } a0 && (a0 - h0).Length > 4) DrawRoute(dc, h0, a0, accent, live: true, anim, t);
            else if (selected is not null && (selected.At - h0).Length > 4) DrawRoute(dc, h0, selected.At, accent, live: false, anim, t);
        }

        foreach (var pin in _pins)
        {
            var pop = anim ? EaseOutBack((t - _introAt - pin.Index * 0.07) / 0.55) : 1;
            if (pop <= 0.01) continue;
            var r = pin.Radius * pop;
            var isActive = ReferenceEquals(pin, active);
            var isSel = ReferenceEquals(pin, selected);
            var isHover = ReferenceEquals(pin, _hover);
            if (isHover) r *= 1.18;

            // soft halo
            var halo = new RadialGradientBrush(A(pin.Color, isActive ? 0.55 : 0.32), A(pin.Color, 0));
            dc.DrawEllipse(halo, null, pin.At, r * 2.8, r * 2.8);

            // breathing pulse rings (two, offset) — each country on its own phase so the map shimmers, not blinks
            if (anim)
            {
                for (var k = 0; k < (isActive ? 2 : 1); k++)
                {
                    var ph = Frac(t / (isActive ? 1.6 : 2.6) + pin.Index * 0.173 + k * 0.5);
                    var pen = new Pen(new SolidColorBrush(A(pin.Color, (1 - ph) * 0.75)), 1.3);
                    dc.DrawEllipse(null, pen, pin.At, r + ph * r * 2.3, r + ph * r * 2.3);
                }
            }

            // selected: a spinning dashed orbit
            if (isSel)
            {
                var orbit = new Pen(new SolidColorBrush(A(text, 0.9)), 1.4) { DashStyle = new DashStyle([2.2, 2.2], anim ? -t * 4 : 0), DashCap = PenLineCap.Round };
                dc.DrawEllipse(null, orbit, pin.At, r + 4.5, r + 4.5);
            }

            // core: gradient bead with a bright rim
            var core = new RadialGradientBrush(Color.FromArgb(0xFF, (byte)Math.Min(255, pin.Color.R + 70), (byte)Math.Min(255, pin.Color.G + 70), (byte)Math.Min(255, pin.Color.B + 70)), pin.Color)
            { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.4, 0.38), RadiusX = 0.75, RadiusY = 0.75 };
            dc.DrawEllipse(core, new Pen(new SolidColorBrush(A(text, isHover || isActive ? 0.95 : 0.55)), isActive ? 1.6 : 1), pin.At, r, r);

            if (pin.Row.Count > 1 && r >= 7.5)
            {
                var ft = Text(pin.Row.Count.ToString(CultureInfo.InvariantCulture), Math.Max(8, r * 0.95), Color.FromRgb(0x10, 0x10, 0x18), FontWeights.Bold);
                dc.DrawText(ft, new Point(pin.At.X - ft.Width / 2, pin.At.Y - ft.Height / 2));
            }
        }

        // home marker: a small diamond "you are here"
        if (home is { } hp)
        {
            var d = 4.2;
            var g = new StreamGeometry();
            using (var sg = g.Open())
            {
                sg.BeginFigure(new Point(hp.X, hp.Y - d), true, true);
                sg.LineTo(new Point(hp.X + d, hp.Y), true, false);
                sg.LineTo(new Point(hp.X, hp.Y + d), true, false);
                sg.LineTo(new Point(hp.X - d, hp.Y), true, false);
            }
            g.Freeze();
            if (anim)
            {
                var ph = Frac(t / 2.0);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(text, (1 - ph) * 0.6)), 1), hp, d + ph * 10, d + ph * 10);
            }
            dc.DrawGeometry(new SolidColorBrush(A(text, 0.95)), new Pen(new SolidColorBrush(accent), 1.2), g);
            var you = Text(L["Map.You"], 10, A(text, 0.85), FontWeights.SemiBold);
            dc.DrawText(you, new Point(hp.X - you.Width / 2, hp.Y + d + 2));
        }

        // 4) hover card
        if (_hover is { } hv)
        {
            var lines = new List<FormattedText> { Text(hv.Row.Title, 12.5, text, FontWeights.SemiBold), Text(hv.Row.CountText, 11, subtle, FontWeights.Normal) };
            if (hv.Row.Best.Length > 0) lines.Add(Text(hv.Row.Best, 11, good, FontWeights.SemiBold));
            lines.Add(Text(L["Map.ClickHint"], 10, A(accent, 1), FontWeights.Normal));
            var cw = lines.Max(l => l.Width) + 20; var ch = lines.Sum(l => l.Height) + 14;
            var x = hv.At.X + hv.Radius + 10; var y = hv.At.Y - ch / 2;
            if (x + cw > w - 2) x = hv.At.X - hv.Radius - 10 - cw;
            y = Math.Clamp(y, 2, Math.Max(2, h - ch - 2));
            dc.DrawRoundedRectangle(new SolidColorBrush(A(popup, 0.96)), new Pen(new SolidColorBrush(border), 1), new Rect(x, y, cw, ch), 9, 9);
            dc.DrawRoundedRectangle(new SolidColorBrush(hv.Color), null, new Rect(x + 1, y + 7, 3, ch - 14), 1.5, 1.5);
            var ly = y + 7;
            foreach (var l in lines) { dc.DrawText(l, new Point(x + cw - 10 - l.Width, ly)); ly += l.Height; } // right-aligned: reads well in fa and en
        }
    }

    private void DrawRoute(DrawingContext dc, Point a, Point b, Color accent, bool live, bool anim, double t)
    {
        var dist = (b - a).Length;
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var ctrl = new Point(mid.X, mid.Y - Math.Min(dist * 0.38, ActualHeight * 0.45)); // arc bows northward, like a flight path
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(a, false, false); g.QuadraticBezierTo(ctrl, b, true, false); }
        geo.Freeze();

        if (live)
        {
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(A(accent, 0.22)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
            var dash = new Pen(new SolidColorBrush(A(accent, 0.9)), 1.6) { DashStyle = new DashStyle([3, 3], anim ? -t * 6 : 0), DashCap = PenLineCap.Round };
            dc.DrawGeometry(null, dash, geo);
            if (!anim) return;
            // comet: a bright head with a fading tail, looping home → exit
            var head = Frac(t / 1.9);
            for (var k = 0; k < 10; k++)
            {
                var tt = head - k * 0.022;
                if (tt < 0) continue;
                var p = Bezier(a, ctrl, b, tt);
                var alpha = (1 - k / 10.0);
                dc.DrawEllipse(new SolidColorBrush(A(k == 0 ? Colors.White : accent, alpha * 0.95)), null, p, 3.2 * (1 - k * 0.07), 3.2 * (1 - k * 0.07));
            }
        }
        else
        {
            var dash = new Pen(new SolidColorBrush(A(accent, 0.45)), 1.1) { DashStyle = new DashStyle([1.5, 3.5], anim ? -t * 2 : 0), DashCap = PenLineCap.Round };
            dc.DrawGeometry(null, dash, geo);
        }
    }

    private FormattedText Text(string s, double size, Color color, FontWeight weight)
    {
        var family = TextElement.GetFontFamily(this);
        var brush = new SolidColorBrush(color); brush.Freeze();
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
