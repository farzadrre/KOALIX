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
    private const double Step = 2.5, Top = 82.5, MaxHeight0 = 320;

    public static readonly DependencyProperty CountriesProperty = DependencyProperty.Register(nameof(Countries), typeof(IEnumerable), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((WorldMap)d).OnCountriesChanged(e.OldValue, e.NewValue)));
    public static readonly DependencyProperty CountryCommandProperty = DependencyProperty.Register(nameof(CountryCommand), typeof(ICommand), typeof(WorldMap));
    public static readonly DependencyProperty QuickConnectCommandProperty = DependencyProperty.Register(nameof(QuickConnectCommand), typeof(ICommand), typeof(WorldMap));
    public static readonly DependencyProperty ActiveCodeProperty = DependencyProperty.Register(nameof(ActiveCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedCodeProperty = DependencyProperty.Register(nameof(SelectedCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HoveredCodeProperty = DependencyProperty.Register(nameof(HoveredCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty RegionFilterProperty = DependencyProperty.Register(nameof(RegionFilter), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata("All", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WorldMap)d).ResetUserCamera()));
    public static readonly DependencyProperty HomeCodeProperty = DependencyProperty.Register(nameof(HomeCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata("IR", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PingTextProperty = DependencyProperty.Register(nameof(PingText), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GlobeModeProperty = DependencyProperty.Register(nameof(GlobeMode), typeof(bool), typeof(WorldMap),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((WorldMap)d).ResetUserCamera()));

    /// <summary>The <see cref="CountryRow"/> collection (rows without a known centroid are simply not drawn).</summary>
    public IEnumerable? Countries { get => (IEnumerable?)GetValue(CountriesProperty); set => SetValue(CountriesProperty, value); }
    public ICommand? CountryCommand { get => (ICommand?)GetValue(CountryCommandProperty); set => SetValue(CountryCommandProperty, value); }
    /// <summary>v0.4.41 — double-clicking a country pin immediately connects to its fastest server.</summary>
    public ICommand? QuickConnectCommand { get => (ICommand?)GetValue(QuickConnectCommandProperty); set => SetValue(QuickConnectCommandProperty, value); }
    /// <summary>Exit country of the live tunnel (null when disconnected) — gets the travelling route.</summary>
    public string? ActiveCode { get => (string?)GetValue(ActiveCodeProperty); set => SetValue(ActiveCodeProperty, value); }
    /// <summary>Country of the selected server card — gets the spinning ring and a faint preview route.</summary>
    public string? SelectedCode { get => (string?)GetValue(SelectedCodeProperty); set => SetValue(SelectedCodeProperty, value); }
    /// <summary>v0.4.41 — two-way synced with the country flag strip below the map.</summary>
    public string? HoveredCode { get => (string?)GetValue(HoveredCodeProperty); set => SetValue(HoveredCodeProperty, value); }
    /// <summary>v0.4.41 — region camera filter: All, EU, AS, AM.</summary>
    public string? RegionFilter { get => (string?)GetValue(RegionFilterProperty); set => SetValue(RegionFilterProperty, value); }
    /// <summary>Where the user is (route origin). Defaults to Iran.</summary>
    public string? HomeCode { get => (string?)GetValue(HomeCodeProperty); set => SetValue(HomeCodeProperty, value); }
    /// <summary>Live tunnel latency text shown on the flight arc badge.</summary>
    public string? PingText { get => (string?)GetValue(PingTextProperty); set => SetValue(PingTextProperty, value); }
    /// <summary>v0.4.41 — true = 3D Orthographic Globe projection; false = 2D flat map.</summary>
    public bool GlobeMode { get => (bool)GetValue(GlobeModeProperty); set => SetValue(GlobeModeProperty, value); }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _introAt, _lastFrame;
    private bool _ticking;
    private Pin? _hover;
    private readonly List<Pin> _pins = [];
    private BitmapSource? _land, _landAccent;
    private (int W, int H, Color Dot, Color Accent, double Dpi) _landKey;

    // Interactive camera state (zoom + drag pan + HUD buttons + 3D globe rotation)
    private double _userZoom = 1.0;
    private double _userPanLon, _userPanLat;
    private double _globeLon = 28.0, _globeLat = 32.0;
    private bool _isDragging, _didDrag;
    private Point _dragStart;
    private double _dragStartPanLon, _dragStartPanLat;
    private Rect _zoomInRect, _zoomOutRect, _zoomResetRect, _modeToggleRect;
    private int _hoverHud; // 0 = none, 1 = zoom in, 2 = zoom out, 3 = reset, 4 = 2D/3D toggle

    // Precomputed trig tables for ultra-fast 3D Orthographic Globe projection
    private static readonly double[] RowSinLat = new double[Rows];
    private static readonly double[] RowCosLat = new double[Rows];
    private static readonly double[] ColLonRad = new double[Cols];
    static WorldMap()
    {
        for (var r = 0; r < Rows; r++)
        {
            var latRad = (Top - (r + 0.5) * Step) * Math.PI / 180.0;
            RowSinLat[r] = Math.Sin(latRad);
            RowCosLat[r] = Math.Cos(latRad);
        }
        for (var c = 0; c < Cols; c++)
        {
            ColLonRad[c] = (-180.0 + (c + 0.5) * Step) * Math.PI / 180.0;
        }
    }

    private sealed record Pin(CountryRow Row, Point At, Point TrueAt, double Radius, Color Color, int Index);

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

    private void ResetUserCamera()
    {
        _userZoom = 1.0;
        _userPanLon = 0;
        _userPanLat = 0;
        InvalidateVisual();
    }

    /// <summary>v0.4.40/v0.4.41 — auto-zoom + region filter + user zoom/pan: frame every country that hosts servers
    /// (or the filtered region), with manual zoom (1.0×..3.5×) and drag panning layered smoothly on top.</summary>
    private (double Lon, double Lat, double Span) TargetView(IEnumerable<(double Lat, double Lon)> points, string? region)
    {
        var w = ActualWidth; var h = ActualHeight;
        const double worldLatTop = Top, worldLatBottom = Top - Rows * Step;
        var pts = points.ToList();
        (double Lon, double Lat, double Span) baseView;

        if (w < 1 || h < 1)
        {
            baseView = (0, worldLatTop - Rows * Step / 2, 360);
        }
        else if (pts.Count == 0)
        {
            baseView = region switch
            {
                "EU" => (14.0, 50.0, 88.0),
                "AS" => (86.0, 28.0, 145.0),
                "AM" => (-82.0, 20.0, 140.0),
                _ => (18.0, 26.0, 280.0),
            };
        }
        else
        {
            double lonMin = pts.Min(x => x.Lon), lonMax = pts.Max(x => x.Lon), latMin = pts.Min(x => x.Lat), latMax = pts.Max(x => x.Lat);
            var minSpan = region is "EU" or "AS" or "AM" ? 72.0 : 96.0;
            var span = Math.Max(lonMax - lonMin + 2 * 16, minSpan);
            span = Math.Max(span, (latMax - latMin + 2 * 11) * w / h);
            span = Math.Min(span, 360);
            var half = span / 2;
            var cx = span >= 360 ? 0 : Math.Clamp((lonMin + lonMax) / 2, -180 + half, 180 - half);
            var halfLat = span * h / w / 2;
            var cy = halfLat * 2 >= worldLatTop - worldLatBottom ? (worldLatTop + worldLatBottom) / 2
                : Math.Clamp((latMin + latMax) / 2, worldLatBottom + halfLat, worldLatTop - halfLat);
            baseView = (cx, cy, span);
        }

        // Apply interactive user zoom & pan
        var zSpan = Math.Clamp(baseView.Span / Math.Clamp(_userZoom, 1.0, 3.6), 38.0, 360.0);
        var zHalf = zSpan / 2;
        var zLon = zSpan >= 360 ? 0 : Math.Clamp(baseView.Lon + _userPanLon, -180 + zHalf, 180 - zHalf);
        var zHalfLat = w > 1 ? zSpan * h / w / 2 : 35;
        var zLat = zHalfLat * 2 >= worldLatTop - worldLatBottom
            ? (worldLatTop + worldLatBottom) / 2
            : Math.Clamp(baseView.Lat + _userPanLat, worldLatBottom + zHalfLat, worldLatTop - zHalfLat);
        return (zLon, zLat, zSpan);
    }

    private (double Lon, double Lat, double Span)? _view;
    private (double Ox, double Oy, double Cell) _landFrame;

    private static Point Project((double Ox, double Oy, double Cell) f, double lat, double lon) =>
        new(f.Ox + (lon + 180) / Step * f.Cell, f.Oy + (Top - lat) / Step * f.Cell);

    private static bool IsLand(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return false;
        var nib = Convert.ToInt32(Land[row][col >> 2].ToString(), 16);
        return (nib & (8 >> (col & 3))) != 0;
    }

    private static bool IsCoastalLand(int row, int col) =>
        !IsLand(row - 1, col) || !IsLand(row + 1, col) || !IsLand(row, col - 1) || !IsLand(row, col + 1);

    // ───────────────────────────── theme ─────────────────────────────
    private Color Col(string key, Color fallback) => TryFindResource(key) switch
    {
        Color c => c,
        SolidColorBrush b => b.Color,
        _ => fallback,
    };

    private static Color A(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>v0.4.41 — two-tone holographic dot-matrix continents: coastal cells are drawn brighter and slightly
    /// larger than inland cells so continent silhouettes read with crisp cartographic definition.</summary>
    private static BitmapSource RenderLand(int w, int h, Color inlandColor, Color coastColor, double dpi, (double Ox, double Oy, double Cell) f)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var inlandBrush = new SolidColorBrush(inlandColor); inlandBrush.Freeze();
            var coastBrush = new SolidColorBrush(coastColor); coastBrush.Freeze();
            var rInland = Math.Clamp(f.Cell * 0.27, 0.55, 2.15);
            var rCoast = Math.Clamp(f.Cell * 0.34, 0.72, 2.55);
            for (var row = 0; row < Rows; row++)
            {
                var y = f.Oy + (row + 0.5) * f.Cell;
                if (y < -f.Cell || y > h + f.Cell) continue;
                for (var col = 0; col < Cols; col++)
                {
                    var x = f.Ox + (col + 0.5) * f.Cell;
                    if (x < -f.Cell || x > w + f.Cell || !IsLand(row, col)) continue;
                    var coast = IsCoastalLand(row, col);
                    var r = coast ? rCoast : rInland;
                    dc.DrawEllipse(coast ? coastBrush : inlandBrush, null, new Point(x, y), r, r);
                }
            }
        }
        var scale = dpi / 96.0;
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    // ───────────────────────────── input (hover, zoom, pan, double-click) ─────────────────────────────
    private int HitHud(Point p)
    {
        if (_zoomInRect.Contains(p)) return 1;
        if (_zoomOutRect.Contains(p)) return 2;
        if (_modeToggleRect.Contains(p)) return 4;
        if ((_userZoom > 1.02 || Math.Abs(_userPanLon) > 0.5 || Math.Abs(_userPanLat) > 0.5) && _zoomResetRect.Contains(p)) return 3;
        return 0;
    }

    private Pin? HitPin(Point p)
    {
        Pin? best = null; var bestD = double.MaxValue;
        foreach (var pin in _pins)
        {
            var d = (pin.At - p).Length;
            if (d < Math.Max(pin.Radius + 6.5, 12) && d < bestD) { best = pin; bestD = d; }
        }
        return best;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ActualWidth < 20 || ActualHeight < 10) return;
        var factor = e.Delta > 0 ? 1.24 : 1.0 / 1.24;
        var nextZoom = Math.Clamp(_userZoom * factor, 1.0, 3.5);
        if (Math.Abs(nextZoom - _userZoom) < 0.005) return;

        // Anchor zoom toward the mouse cursor when zooming in (2D mode), and relax pan toward 0 when zooming back to 1x
        if (!GlobeMode && _view is { } cur && e.Delta > 0)
        {
            var p = e.GetPosition(this);
            var rx = (p.X / ActualWidth - 0.5) * cur.Span;
            var ry = (0.5 - p.Y / ActualHeight) * (cur.Span * ActualHeight / ActualWidth);
            var pull = 1.0 - _userZoom / nextZoom;
            _userPanLon += rx * pull * 0.65;
            _userPanLat += ry * pull * 0.65;
        }
        else if (!GlobeMode && nextZoom <= 1.02)
        {
            nextZoom = 1.0;
            _userPanLon = 0;
            _userPanLat = 0;
        }
        _userZoom = nextZoom;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        var hud = HitHud(p);
        if (hud != 0)
        {
            if (hud == 1) _userZoom = Math.Clamp(_userZoom * 1.32, 1.0, 3.5);
            else if (hud == 2)
            {
                _userZoom = Math.Clamp(_userZoom / 1.32, 1.0, 3.5);
                if (!GlobeMode && _userZoom <= 1.03) { _userZoom = 1.0; _userPanLon = 0; _userPanLat = 0; }
            }
            else if (hud == 3) ResetUserCamera();
            else if (hud == 4) GlobeMode = !GlobeMode;
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        var pin = HitPin(p);
        if (e.ClickCount == 2 && pin is not null && QuickConnectCommand is { } qcmd && qcmd.CanExecute(pin.Row))
        {
            qcmd.Execute(pin.Row);
            e.Handled = true;
            return;
        }

        _isDragging = true;
        _didDrag = false;
        _dragStart = p;
        _dragStartPanLon = _userPanLon;
        _dragStartPanLat = _userPanLat;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var dx = p.X - _dragStart.X;
            var dy = p.Y - _dragStart.Y;
            if (!_didDrag && Math.Sqrt(dx * dx + dy * dy) > 5.0)
            {
                _didDrag = true;
                Cursor = Cursors.SizeAll;
            }
            if (_didDrag && ActualWidth > 10)
            {
                if (GlobeMode)
                {
                    var degPerPx = 150.0 / Math.Max(120, Math.Min(ActualWidth, ActualHeight) * _userZoom);
                    var rawLon = _dragStartPanLon - dx * degPerPx;
                    // Wrap longitude smoothly in [-180, 180]
                    while (rawLon > 180) rawLon -= 360;
                    while (rawLon < -180) rawLon += 360;
                    _userPanLon = rawLon;
                    _userPanLat = Math.Clamp(_dragStartPanLat + dy * degPerPx, -55, 55);
                    InvalidateVisual();
                }
                else if (_view is { } cur)
                {
                    var degPerPx = cur.Span / ActualWidth;
                    _userPanLon = Math.Clamp(_dragStartPanLon - dx * degPerPx, -140, 140);
                    _userPanLat = Math.Clamp(_dragStartPanLat + dy * degPerPx, -65, 65);
                    InvalidateVisual();
                }
            }
            return;
        }

        var hud = HitHud(p);
        var best = hud == 0 ? HitPin(p) : null;
        if (hud != _hoverHud || !ReferenceEquals(best, _hover))
        {
            _hoverHud = hud;
            _hover = best;
            HoveredCode = best?.Row.Code;
            Cursor = hud != 0 || best is not null ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_isDragging) { _isDragging = false; ReleaseMouseCapture(); }
        if (_hover is not null || _hoverHud != 0)
        {
            _hover = null;
            _hoverHud = 0;
            HoveredCode = null;
            Cursor = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var wasDrag = _didDrag;
        if (_isDragging)
        {
            _isDragging = false;
            _didDrag = false;
            ReleaseMouseCapture();
        }
        if (wasDrag)
        {
            Cursor = _hover is not null ? Cursors.Hand : null;
            e.Handled = true;
            return;
        }
        var pin = _hover ?? HitPin(e.GetPosition(this));
        if (pin is not null && CountryCommand is { } cmd && cmd.CanExecute(pin.Row))
        {
            cmd.Execute(pin.Row);
            e.Handled = true;
        }
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
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 8, 8));

        var anim = Fx.Enabled;
        var t = Now;
        var rows = Countries?.OfType<CountryRow>().ToList() ?? [];
        var reg = RegionFilter;

        // camera: glide toward the framing of the current fleet / region filter + user zoom/pan
        var filteredRows = string.IsNullOrEmpty(reg) || string.Equals(reg, "All", StringComparison.OrdinalIgnoreCase)
            ? rows
            : rows.Where(r => string.Equals(Services.GeoNames.RegionOf(r.Code), reg, StringComparison.OrdinalIgnoreCase)).ToList();

        var targets = filteredRows.Where(r => r.Code.Length == 2 && Centroids.ContainsKey(r.Code)).Select(r => Centroids[r.Code]).ToList();
        if ((string.IsNullOrEmpty(reg) || string.Equals(reg, "All", StringComparison.OrdinalIgnoreCase))
            && HomeCode is { Length: 2 } hk && Centroids.TryGetValue(hk, out var hkc))
        {
            targets.Add(hkc);
        }

        var accent = Col("AccentColor", Color.FromRgb(0x7C, 0x5C, 0xFF));
        var subtle = Col("SubtleTextBrush", Color.FromRgb(0x88, 0x88, 0x99));
        var text = Col("TextBrush", Colors.White);
        var good = Col("SuccessBrush", Color.FromRgb(0x2E, 0xCC, 0x71));
        var warn = Col("WarningBrush", Color.FromRgb(0xF3, 0x9C, 0x12));
        var bad = Col("DangerBrush", Color.FromRgb(0xE7, 0x4C, 0x3C));
        var popup = Col("PopupBrush", Color.FromRgb(0x1E, 0x1E, 0x2A));
        var border = Col("StrongBorderBrush", Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        if (GlobeMode)
        {
            RenderGlobeMode(dc, w, h, rows, reg, targets, anim, t, accent, subtle, text, good, warn, bad, popup, border);
            dc.Pop();
            return;
        }

        var goal = TargetView(targets, reg);
        var moving = false;
        if (_view is not { } cur || !anim) _view = goal;
        else
        {
            var k = _isDragging ? 0.45 : 0.16;
            var next = (cur.Lon + (goal.Lon - cur.Lon) * k, cur.Lat + (goal.Lat - cur.Lat) * k, cur.Span + (goal.Span - cur.Span) * k);
            moving = Math.Abs(next.Item3 - goal.Span) > 0.35 || Math.Abs(next.Item1 - goal.Lon) > 0.25 || Math.Abs(next.Item2 - goal.Lat) > 0.25;
            _view = moving ? next : goal;
        }

        var f = Frame(_view.Value);

        // 0) Deep ocean atmosphere glow + cartographic graticule grid (lat/lon lines & subtle degree labels)
        var oceanGlow = new RadialGradientBrush(A(accent, 0.085), A(accent, 0.0))
        {
            Center = new Point(0.52, 0.45),
            GradientOrigin = new Point(0.52, 0.42),
            RadiusX = 0.68,
            RadiusY = 0.62,
        };
        dc.DrawRectangle(oceanGlow, null, new Rect(0, 0, w, h));
        DrawGraticule(dc, f, w, h, subtle, accent);

        // 1) Continents — cached two-tone dot matrix (inland + crisp coastlines), plus a diagonal aurora sweep.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        var key = ((int)w, (int)h, subtle, accent, dpi);
        var stale = Math.Abs(_landFrame.Cell - f.Cell) > 0.001 || Math.Abs(_landFrame.Ox - f.Ox) > 0.3 || Math.Abs(_landFrame.Oy - f.Oy) > 0.3;
        if (_land is null || _landKey != key || (stale && !moving))
        {
            var coastTint = Color.FromRgb(
                (byte)((subtle.R * 2 + accent.R) / 3),
                (byte)((subtle.G * 2 + accent.G) / 3),
                (byte)((subtle.B * 2 + accent.B) / 3));
            _land = RenderLand((int)w, (int)h, A(subtle, 0.31), A(coastTint, 0.64), dpi, f);
            _landAccent = RenderLand((int)w, (int)h, A(accent, 0.65), A(accent, 1.0), dpi, f);
            _landKey = key;
            _landFrame = f;
        }
        var s0 = f.Cell / _landFrame.Cell;
        var bounds = new Rect(f.Ox - _landFrame.Ox * s0, f.Oy - _landFrame.Oy * s0, _land.PixelWidth * 96.0 / dpi * s0, _land.PixelHeight * 96.0 / dpi * s0);
        dc.DrawImage(_land, bounds);
        if (anim && _landAccent is not null)
        {
            var sx = -0.35 + Frac(t / 9.0) * 1.7;
            var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.25) };
            band.GradientStops.Add(new GradientStop(Colors.Transparent, sx - 0.16));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0x88, 0, 0, 0), sx));
            band.GradientStops.Add(new GradientStop(Colors.Transparent, sx + 0.16));
            dc.PushOpacityMask(band);
            dc.DrawImage(_landAccent, bounds);
            dc.Pop();
        }

        // 2) Build pins & run iterative anti-overlap separation so dense European clusters never collide
        _pins.Clear();
        var rawPins = new List<(CountryRow Row, Point TrueAt, Point At, double Radius, Color Color, int Index)>();
        var idx = 0;
        foreach (var row in rows)
        {
            if (row.Code.Length != 2 || !Centroids.TryGetValue(row.Code, out var c)) continue;
            var radius = Math.Min(4.5 + 2.1 * Math.Sqrt(Math.Max(1, row.Count)), 13.0) * Math.Clamp(f.Cell / 5.0, 0.78, 1.32);
            var color = row.BestMs is not { } ms ? accent : ms < 300 ? good : ms < 800 ? warn : bad;
            var pt = Project(f, c.Lat, c.Lon);
            rawPins.Add((row, pt, pt, radius, color, idx++));
        }

        // 6 passes of gentle pairwise repulsion for closely clustered countries
        for (var iter = 0; iter < 6; iter++)
        {
            for (var a = 0; a < rawPins.Count; a++)
            {
                for (var b = a + 1; b < rawPins.Count; b++)
                {
                    var pa = rawPins[a]; var pb = rawPins[b];
                    var delta = pb.At - pa.At;
                    var dist = delta.Length;
                    var minDist = pa.Radius + pb.Radius + 5.5;
                    if (dist < minDist)
                    {
                        var dir = dist > 0.01 ? delta / dist : new Vector(Math.Cos(a * 1.7), Math.Sin(a * 1.7));
                        var push = (minDist - dist) * 0.46;
                        pa.At -= dir * push;
                        pb.At += dir * push;
                        rawPins[a] = pa;
                        rawPins[b] = pb;
                    }
                }
            }
        }

        foreach (var rp in rawPins)
            _pins.Add(new Pin(rp.Row, rp.At, rp.TrueAt, rp.Radius, rp.Color, rp.Index));

        Point? home = HomeCode is { Length: 2 } hc && Centroids.TryGetValue(hc, out var hcc) ? Project(f, hcc.Lat, hcc.Lon) : null;
        var active = _pins.FirstOrDefault(p => string.Equals(p.Row.Code, ActiveCode, StringComparison.OrdinalIgnoreCase));
        Point? activeAt = active?.At ?? (ActiveCode is { Length: 2 } ac && Centroids.TryGetValue(ac, out var acc) ? Project(f, acc.Lat, acc.Lon) : null);
        var selected = _pins.FirstOrDefault(p => string.Equals(p.Row.Code, SelectedCode, StringComparison.OrdinalIgnoreCase));
        var externalHover = _hover ?? (HoveredCode is { Length: 2 } hvc
            ? _pins.FirstOrDefault(p => string.Equals(p.Row.Code, hvc, StringComparison.OrdinalIgnoreCase))
            : null);

        // 3) Global network mesh + primary active/preview flight arcs
        if (home is { } h0)
        {
            // Subtle background telemetry mesh to all server countries
            var meshPen = new Pen(new SolidColorBrush(A(subtle, 0.13)), 0.85) { DashStyle = new DashStyle([2, 4], 0) };
            meshPen.Freeze();
            foreach (var pin in _pins)
            {
                if (ReferenceEquals(pin, active) || ReferenceEquals(pin, selected) || ReferenceEquals(pin, externalHover)) continue;
                var d = (pin.At - h0).Length;
                if (d < 8) continue;
                var mid = new Point((h0.X + pin.At.X) / 2, (h0.Y + pin.At.Y) / 2 - Math.Min(d * 0.26, h * 0.32));
                var g = new StreamGeometry();
                using (var sg = g.Open()) { sg.BeginFigure(h0, false, false); sg.QuadraticBezierTo(mid, pin.At, true, false); }
                g.Freeze();
                dc.DrawGeometry(null, meshPen, g);
            }

            // Preview route to hovered or selected country (when different from active)
            var previewPin = externalHover ?? selected;
            if (previewPin is not null && !ReferenceEquals(previewPin, active) && (previewPin.At - h0).Length > 4)
            {
                var prevBadge = previewPin.Row.Best.Length > 0
                    ? $"{HomeCode ?? "IR"} ➔ {previewPin.Row.Code} · {previewPin.Row.Best}"
                    : $"{HomeCode ?? "IR"} ➔ {previewPin.Row.Code}";
                DrawRoute(dc, h0, previewPin.At, previewPin.Color, live: false, anim, t, prevBadge, popup, border, text);
            }

            // Live connected route (marching neon arc + dual comet stream + mid-arc latency badge)
            if (activeAt is { } a0 && (a0 - h0).Length > 4)
            {
                var routeColor = active?.Color ?? good;
                var pingStr = !string.IsNullOrWhiteSpace(PingText) && PingText != "—" ? PingText : active?.Row.Best;
                var liveBadge = !string.IsNullOrWhiteSpace(pingStr)
                    ? $"{HomeCode ?? "IR"} ➔ {ActiveCode} · {pingStr}"
                    : $"{HomeCode ?? "IR"} ➔ {ActiveCode}";
                DrawRoute(dc, h0, a0, routeColor, live: true, anim, t, liveBadge, popup, border, text);
            }
        }

        // 4) Draw country pins, leader lines for de-clustered pins, and floating mini labels
        var leaderPen = new Pen(new SolidColorBrush(A(subtle, 0.48)), 0.9) { DashStyle = new DashStyle([1.5, 2], 0) };
        leaderPen.Freeze();
        var anchorBrush = new SolidColorBrush(A(text, 0.55));
        anchorBrush.Freeze();

        foreach (var pin in _pins)
        {
            var pop = anim ? EaseOutBack((t - _introAt - pin.Index * 0.06) / 0.52) : 1;
            if (pop <= 0.01) continue;
            var r = pin.Radius * pop;
            var isActive = ReferenceEquals(pin, active);
            var isSel = ReferenceEquals(pin, selected);
            var isHover = ReferenceEquals(pin, externalHover);
            var inFilteredRegion = string.IsNullOrEmpty(reg) || string.Equals(reg, "All", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Services.GeoNames.RegionOf(pin.Row.Code), reg, StringComparison.OrdinalIgnoreCase);
            var dimFactor = inFilteredRegion || isActive || isSel || isHover ? 1.0 : 0.35;
            if (isHover) r *= 1.22;

            // If anti-overlap nudged this pin away from its true geographic centroid, draw a crisp leader line + anchor dot
            if ((pin.At - pin.TrueAt).Length > 3.5)
            {
                dc.DrawLine(leaderPen, pin.TrueAt, pin.At);
                dc.DrawEllipse(anchorBrush, null, pin.TrueAt, 1.7, 1.7);
            }

            // Soft multi-stop radial halo
            var halo = new RadialGradientBrush(A(pin.Color, (isActive ? 0.62 : isHover ? 0.50 : 0.34) * dimFactor), A(pin.Color, 0));
            dc.DrawEllipse(halo, null, pin.At, r * 2.9, r * 2.9);

            // Breathing pulse rings
            if (anim && dimFactor > 0.5)
            {
                var ringCount = isActive || isHover ? 2 : 1;
                for (var k = 0; k < ringCount; k++)
                {
                    var ph = Frac(t / (isActive ? 1.5 : 2.5) + pin.Index * 0.173 + k * 0.5);
                    var pen = new Pen(new SolidColorBrush(A(pin.Color, (1 - ph) * (isActive ? 0.85 : 0.68))), isActive ? 1.5 : 1.2);
                    dc.DrawEllipse(null, pen, pin.At, r + ph * r * 2.4, r + ph * r * 2.4);
                }
            }

            // Selected or externally hovered: spinning dashed orbit
            if (isSel || (isHover && !ReferenceEquals(pin, _hover)))
            {
                var orbitColor = isHover ? accent : text;
                var orbit = new Pen(new SolidColorBrush(A(orbitColor, 0.92)), 1.45)
                {
                    DashStyle = new DashStyle([2.2, 2.2], anim ? -t * 4.5 : 0),
                    DashCap = PenLineCap.Round,
                };
                dc.DrawEllipse(null, orbit, pin.At, r + 4.8, r + 4.8);
            }

            // Fastest country crown ring
            if (pin.Row.IsBestCountry && !isActive && dimFactor > 0.5)
            {
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(good, 0.75)), 1.1), pin.At, r + 2.6, r + 2.6);
            }

            // Core: 3D specular bead with bright rim
            var cLight = Color.FromArgb(0xFF, (byte)Math.Min(255, pin.Color.R + 75), (byte)Math.Min(255, pin.Color.G + 75), (byte)Math.Min(255, pin.Color.B + 75));
            var core = new RadialGradientBrush(A(cLight, dimFactor), A(pin.Color, dimFactor))
            { GradientOrigin = new Point(0.32, 0.28), Center = new Point(0.4, 0.38), RadiusX = 0.78, RadiusY = 0.78 };
            var rimPen = new Pen(new SolidColorBrush(A(text, (isHover || isActive ? 0.98 : 0.62) * dimFactor)), isActive || isHover ? 1.7 : 1.05);
            dc.DrawEllipse(core, rimPen, pin.At, r, r);

            // Server count inside pin
            if (pin.Row.Count > 1 && r >= 7.2)
            {
                var ft = Text(pin.Row.Count.ToString(CultureInfo.InvariantCulture), Math.Max(8, r * 0.94), Color.FromArgb((byte)(255 * dimFactor), 0x10, 0x10, 0x18), FontWeights.Bold);
                dc.DrawText(ft, new Point(pin.At.X - ft.Width / 2, pin.At.Y - ft.Height / 2));
            }

            // Floating mini tag on active, selected, or fastest country (when not covered by the full hover card)
            if ((isActive || isSel || pin.Row.IsBestCountry) && !ReferenceEquals(pin, externalHover) && dimFactor > 0.5)
            {
                var tagStr = pin.Row.BestMs is { } ms ? $"{pin.Row.Code} · {ms}ms" : pin.Row.Code;
                var tagFt = Text(tagStr, 9.2, text, FontWeights.SemiBold);
                var tw = tagFt.Width + 10;
                var th = tagFt.Height + 3;
                var tx = Math.Clamp(pin.At.X - tw / 2, 4, Math.Max(4, w - tw - 4));
                var ty = pin.At.Y + r + 5;
                if (ty + th > h - 4) ty = pin.At.Y - r - th - 5;
                dc.DrawRoundedRectangle(new SolidColorBrush(A(popup, 0.88)), new Pen(new SolidColorBrush(A(pin.Color, 0.75)), 0.9), new Rect(tx, ty, tw, th), 5, 5);
                dc.DrawText(tagFt, new Point(tx + 5, ty + 1.5));
            }
        }

        // 5) Home marker: radar sweep + glowing diamond "you are here"
        if (home is { } hp)
        {
            var d = 4.6;
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
                var ph = Frac(t / 2.1);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(accent, (1 - ph) * 0.65)), 1.1), hp, d + ph * 13, d + ph * 13);
            }
            dc.DrawGeometry(new SolidColorBrush(A(text, 0.96)), new Pen(new SolidColorBrush(accent), 1.4), g);
            var you = Text(L["Map.You"], 9.8, A(text, 0.88), FontWeights.SemiBold);
            dc.DrawText(you, new Point(hp.X - you.Width / 2, hp.Y + d + 2.5));
        }

        // 6) On-canvas HUD controls (top-left zoom/reset buttons) & bottom-left ping legend
        DrawHudAndLegend(dc, w, h, popup, border, text, subtle, accent, good, warn, bad);

        // 7) Rich glassmorphic hover card (with embedded country flag bitmap, top server preview & double-click hint)
        if (externalHover is { } hv)
        {
            DrawHoverCard(dc, hv, w, h, popup, border, text, subtle, accent, good);
        }

        dc.Pop(); // map bounds clip
    }

    private void DrawGraticule(DrawingContext dc, (double Ox, double Oy, double Cell) f, double w, double h, Color subtle, Color accent)
    {
        var gridPen = new Pen(new SolidColorBrush(A(subtle, 0.085)), 0.75) { DashStyle = new DashStyle([3, 5], 0) };
        gridPen.Freeze();
        var eqPen = new Pen(new SolidColorBrush(A(accent, 0.14)), 0.85) { DashStyle = new DashStyle([4, 4], 0) };
        eqPen.Freeze();

        ReadOnlySpan<double> lats = [60, 30, 0, -30];
        foreach (var lat in lats)
        {
            var y = Project(f, lat, 0).Y;
            if (y < 14 || y > h - 14) continue;
            dc.DrawLine(lat == 0 ? eqPen : gridPen, new Point(0, y), new Point(w, y));
            var lbl = lat == 0 ? "0°" : lat > 0 ? $"{lat:0}°N" : $"{-lat:0}°S";
            var ft = Text(lbl, 8.5, A(subtle, 0.42), FontWeights.Normal);
            dc.DrawText(ft, new Point(w - ft.Width - 5, y - ft.Height - 1));
        }

        ReadOnlySpan<double> lons = [-120, -60, 0, 60, 120];
        foreach (var lon in lons)
        {
            var x = Project(f, 0, lon).X;
            if (x < 24 || x > w - 24) continue;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }
    }

    private void DrawRoute(DrawingContext dc, Point a, Point b, Color routeColor, bool live, bool anim, double t, string? badgeText, Color popup, Color border, Color text)
    {
        var dist = (b - a).Length;
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var lift = Math.Min(dist * 0.36, ActualHeight * 0.44);
        var ctrl = new Point(mid.X, mid.Y - lift);
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(a, false, false); g.QuadraticBezierTo(ctrl, b, true, false); }
        geo.Freeze();

        // Subtle ground shadow chord connecting origin & destination
        var shadowPen = new Pen(new SolidColorBrush(A(routeColor, live ? 0.14 : 0.08)), 1.0) { DashStyle = new DashStyle([2, 4], 0) };
        dc.DrawLine(shadowPen, a, b);

        if (live)
        {
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(A(routeColor, 0.24)), 5.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
            var dash = new Pen(new SolidColorBrush(A(routeColor, 0.94)), 1.75) { DashStyle = new DashStyle([3, 3], anim ? -t * 6.5 : 0), DashCap = PenLineCap.Round };
            dc.DrawGeometry(null, dash, geo);

            if (anim)
            {
                // Dual data-packet comets streaming along the tunnel arc
                for (var stream = 0; stream < 2; stream++)
                {
                    var head = Frac(t / 1.85 + stream * 0.5);
                    for (var k = 0; k < 10; k++)
                    {
                        var tt = head - k * 0.021;
                        if (tt < 0) continue;
                        var p = Bezier(a, ctrl, b, tt);
                        var alpha = 1 - k / 10.0;
                        var sz = (stream == 0 ? 3.3 : 2.5) * (1 - k * 0.07);
                        dc.DrawEllipse(new SolidColorBrush(A(k == 0 ? Colors.White : routeColor, alpha * 0.95)), null, p, sz, sz);
                    }
                }
            }
        }
        else
        {
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(A(routeColor, 0.14)), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
            var dash = new Pen(new SolidColorBrush(A(routeColor, 0.65)), 1.25) { DashStyle = new DashStyle([2, 3], anim ? -t * 3.0 : 0), DashCap = PenLineCap.Round };
            dc.DrawGeometry(null, dash, geo);
        }

        // Mid-arc telemetry badge (e.g. "IR ➔ DE · 98 ms")
        if (!string.IsNullOrWhiteSpace(badgeText) && dist > 65)
        {
            var apex = Bezier(a, ctrl, b, 0.5);
            var ft = Text(badgeText, 9.4, text, FontWeights.SemiBold);
            var bw = ft.Width + 12;
            var bh = ft.Height + 4;
            var bx = Math.Clamp(apex.X - bw / 2, 6, Math.Max(6, ActualWidth - bw - 6));
            var by = Math.Clamp(apex.Y - bh - 4, 4, Math.Max(4, ActualHeight - bh - 4));
            dc.DrawRoundedRectangle(new SolidColorBrush(A(popup, 0.90)), new Pen(new SolidColorBrush(A(routeColor, live ? 0.85 : 0.55)), 1), new Rect(bx, by, bw, bh), 6, 6);
            dc.DrawText(ft, new Point(bx + 6, by + 2));
        }
    }

    private void DrawHudAndLegend(DrawingContext dc, double w, double h, Color popup, Color border, Color text, Color subtle, Color accent, Color good, Color warn, Color bad)
    {
        // Top-left interactive zoom & 2D/3D mode HUD buttons (+, −, 2D/3D toggle, and ⟲ reset when zoomed/panned)
        const double btnSize = 22, pad = 8, gap = 4;
        _zoomInRect = new Rect(pad, pad, btnSize, btnSize);
        _zoomOutRect = new Rect(pad + btnSize + gap, pad, btnSize, btnSize);
        _modeToggleRect = new Rect(pad + (btnSize + gap) * 2, pad, btnSize + 10, btnSize);
        _zoomResetRect = new Rect(_modeToggleRect.Right + gap, pad, btnSize + 8, btnSize);

        void DrawBtn(Rect r, string sym, bool hovered, bool active = false)
        {
            var bg = new SolidColorBrush(A(hovered || active ? accent : popup, hovered ? 0.45 : active ? 0.30 : 0.82));
            var pen = new Pen(new SolidColorBrush(hovered || active ? A(accent, 0.92) : border), 1);
            dc.DrawRoundedRectangle(bg, pen, r, 6, 6);
            var ft = Text(sym, 10.5, active && !hovered ? accent : text, FontWeights.Bold);
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2 - 0.5));
        }

        DrawBtn(_zoomInRect, "+", _hoverHud == 1);
        DrawBtn(_zoomOutRect, "−", _hoverHud == 2);
        DrawBtn(_modeToggleRect, GlobeMode ? "3D" : "2D", _hoverHud == 4, active: GlobeMode);
        if (_userZoom > 1.02 || Math.Abs(_userPanLon) > 0.5 || Math.Abs(_userPanLat) > 0.5)
        {
            DrawBtn(_zoomResetRect, $"{_userZoom:0.0}×", _hoverHud == 3);
        }

        // Bottom-left compact Ping Quality Legend pill
        var l1 = Text("<300ms", 9, subtle, FontWeights.Normal);
        var l2 = Text("<800ms", 9, subtle, FontWeights.Normal);
        var l3 = Text("≥800ms", 9, subtle, FontWeights.Normal);
        var lw = 14 + l1.Width + 14 + l2.Width + 14 + l3.Width + 14;
        var lh = Math.Max(l1.Height, 12) + 6;
        var lx = pad;
        var ly = h - lh - pad;
        if (ly > 40 && lw < w * 0.65)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(A(popup, 0.78)), new Pen(new SolidColorBrush(A(border, 0.35)), 0.8), new Rect(lx, ly, lw, lh), 6, 6);
            var cx = lx + 8;
            var cy = ly + lh / 2;
            dc.DrawEllipse(new SolidColorBrush(good), null, new Point(cx, cy), 3.2, 3.2);
            cx += 6; dc.DrawText(l1, new Point(cx, cy - l1.Height / 2)); cx += l1.Width + 9;
            dc.DrawEllipse(new SolidColorBrush(warn), null, new Point(cx, cy), 3.2, 3.2);
            cx += 6; dc.DrawText(l2, new Point(cx, cy - l2.Height / 2)); cx += l2.Width + 9;
            dc.DrawEllipse(new SolidColorBrush(bad), null, new Point(cx, cy), 3.2, 3.2);
            cx += 6; dc.DrawText(l3, new Point(cx, cy - l3.Height / 2));
        }
    }

    private void DrawHoverCard(DrawingContext dc, Pin hv, double w, double h, Color popup, Color border, Color text, Color subtle, Color accent, Color good)
    {
        var flagBmp = FlagBox.TryGetBitmap(hv.Row.Code);
        var titleStr = hv.Row.IsBestCountry ? $"★ {hv.Row.Title} ({hv.Row.Code})" : $"{hv.Row.Title} ({hv.Row.Code})";
        var titleFt = Text(titleStr, 12.2, text, FontWeights.Bold);
        var subStr = hv.Row.Best.Length > 0 ? $"{hv.Row.CountText}  ·  ⚡ {hv.Row.Best}" : hv.Row.CountText;
        var subFt = Text(subStr, 10.8, hv.Row.Best.Length > 0 ? good : subtle, FontWeights.SemiBold);

        var lines = new List<FormattedText> { titleFt, subFt };
        if (!string.IsNullOrWhiteSpace(hv.Row.BestServerName))
        {
            lines.Add(Text(hv.Row.BestServerName, 9.8, subtle, FontWeights.Normal));
        }
        lines.Add(Text(L["Map.ClickHint"], 9.5, A(accent, 0.95), FontWeights.Normal));

        var flagPad = flagBmp is not null ? 28.0 : 0.0;
        var cw = Math.Max(lines.Max(l => l.Width) + 22, titleFt.Width + flagPad + 24);
        var ch = lines.Sum(l => l.Height + 1.5) + 14;
        var x = hv.At.X + hv.Radius + 12;
        var y = hv.At.Y - ch / 2;
        if (x + cw > w - 4) x = hv.At.X - hv.Radius - 12 - cw;
        x = Math.Clamp(x, 4, Math.Max(4, w - cw - 4));
        y = Math.Clamp(y, 4, Math.Max(4, h - ch - 4));

        dc.DrawRoundedRectangle(new SolidColorBrush(A(popup, 0.96)), new Pen(new SolidColorBrush(A(hv.Color, 0.75)), 1.15), new Rect(x, y, cw, ch), 10, 10);
        dc.DrawRoundedRectangle(new SolidColorBrush(hv.Color), null, new Rect(x + 1.5, y + 7, 3.2, ch - 14), 1.6, 1.6);

        if (flagBmp is not null)
        {
            var flagRect = new Rect(x + 10, y + 9, 20, 14);
            dc.PushClip(new RectangleGeometry(flagRect, 2.5, 2.5));
            dc.DrawImage(flagBmp, flagRect);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(border), 0.7), flagRect, 2.5, 2.5);
        }

        var ly = y + 7;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            dc.DrawText(l, new Point(x + cw - 10 - l.Width, ly));
            ly += l.Height + 1.5;
        }
    }

    private FormattedText Text(string s, double size, Color color, FontWeight weight)
    {
        var family = TextElement.GetFontFamily(this);
        var brush = new SolidColorBrush(color); brush.Freeze();
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    // ───────────────────────────── v0.4.41: 3D Orthographic Globe Renderer ─────────────────────────────
    private static bool ProjectGlobe(double latDeg, double lonDeg, double sinPhi0, double cosPhi0, double lon0Rad, double cx, double cy, double R, out Point pt, out double cosC)
    {
        var phi = latDeg * Math.PI / 180.0;
        var dLon = lonDeg * Math.PI / 180.0 - lon0Rad;
        var sinPhi = Math.Sin(phi);
        var cosPhi = Math.Cos(phi);
        var sinDLon = Math.Sin(dLon);
        var cosDLon = Math.Cos(dLon);
        cosC = sinPhi0 * sinPhi + cosPhi0 * cosPhi * cosDLon;
        pt = new Point(
            cx + R * cosPhi * sinDLon,
            cy - R * (cosPhi0 * sinPhi - sinPhi0 * cosPhi * cosDLon));
        return cosC >= 0.0;
    }

    private void RenderGlobeMode(
        DrawingContext dc, double w, double h, List<CountryRow> rows, string? reg,
        List<(double Lat, double Lon)> targets, bool anim, double t,
        Color accent, Color subtle, Color text, Color good, Color warn, Color bad, Color popup, Color border)
    {
        // Target center lon/lat based on region or hovered/active country + user drag rotation
        var baseLon = reg switch
        {
            "EU" => 15.0,
            "AS" => 82.0,
            "AM" => -82.0,
            _ => targets.Count > 0 ? targets.Average(p => p.Lon) : 28.0,
        };
        var baseLat = reg switch
        {
            "EU" => 48.0,
            "AS" => 28.0,
            "AM" => 22.0,
            _ => targets.Count > 0 ? Math.Clamp(targets.Average(p => p.Lat), 12.0, 44.0) : 30.0,
        };

        // If hovering a country chip below the map, rotate the globe smoothly to face that country
        if (HoveredCode is { Length: 2 } hvc0 && _hover is null && Centroids.TryGetValue(hvc0, out var hcTarget))
        {
            baseLon = hcTarget.Lon - _userPanLon;
            baseLat = Math.Clamp(hcTarget.Lat - _userPanLat, -45, 55);
        }
        else if (anim && !_isDragging && _hover is null && Math.Abs(_userPanLon) < 0.5 && (string.IsNullOrEmpty(reg) || string.Equals(reg, "All", StringComparison.OrdinalIgnoreCase)))
        {
            // Gentle orbital drift around the fleet centroid so the 3D globe feels alive
            baseLon += Math.Sin(t * 0.32) * 18.0;
        }

        var goalLon = baseLon + _userPanLon;
        while (goalLon > 180) goalLon -= 360;
        while (goalLon < -180) goalLon += 360;
        var goalLat = Math.Clamp(baseLat + _userPanLat, -60, 65);

        if (!anim || _isDragging)
        {
            _globeLon = goalLon;
            _globeLat = goalLat;
        }
        else
        {
            var dLon = goalLon - _globeLon;
            if (dLon > 180) dLon -= 360;
            if (dLon < -180) dLon += 360;
            _globeLon += dLon * 0.14;
            while (_globeLon > 180) _globeLon -= 360;
            while (_globeLon < -180) _globeLon += 360;
            _globeLat += (goalLat - _globeLat) * 0.14;
        }

        var cx = w * 0.5;
        var cy = h * 0.5;
        var R = Math.Min(w, h) * 0.435 * Math.Clamp(_userZoom, 0.85, 2.6);
        var center = new Point(cx, cy);
        var phi0 = _globeLat * Math.PI / 180.0;
        var sinPhi0 = Math.Sin(phi0);
        var cosPhi0 = Math.Cos(phi0);
        var lon0Rad = _globeLon * Math.PI / 180.0;

        // 1) Deep space ambient halo + 3D ocean sphere with atmospheric limb glow
        var spaceGlow = new RadialGradientBrush(A(accent, 0.18), A(accent, 0.0))
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        dc.DrawEllipse(spaceGlow, null, center, R * 1.24, R * 1.24);

        var sphereFill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.34, 0.28),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.52,
            RadiusY = 0.52,
        };
        sphereFill.GradientStops.Add(new GradientStop(A(accent, 0.16), 0.0));
        sphereFill.GradientStops.Add(new GradientStop(A(popup, 0.82), 0.72));
        sphereFill.GradientStops.Add(new GradientStop(A(accent, 0.28), 1.0));
        var rimPen = new Pen(new SolidColorBrush(A(accent, 0.58)), 1.45);
        rimPen.Freeze();
        dc.DrawEllipse(sphereFill, rimPen, center, R, R);

        // 2) 3D Orthographic Graticule (parallels & meridians)
        var gridPen = new Pen(new SolidColorBrush(A(subtle, 0.11)), 0.75) { DashStyle = new DashStyle([2.5, 4], 0) };
        gridPen.Freeze();
        var eqPen = new Pen(new SolidColorBrush(A(accent, 0.22)), 0.9) { DashStyle = new DashStyle([3.5, 3.5], 0) };
        eqPen.Freeze();

        ReadOnlySpan<double> lats = [-60, -30, 0, 30, 60];
        foreach (var lat in lats)
        {
            var g = new StreamGeometry();
            using (var sg = g.Open())
            {
                var open = false;
                for (var lon = -180; lon <= 180; lon += 6)
                {
                    if (ProjectGlobe(lat, lon, sinPhi0, cosPhi0, lon0Rad, cx, cy, R, out var gp, out var cc) && cc > 0.01)
                    {
                        if (!open) { sg.BeginFigure(gp, false, false); open = true; }
                        else sg.LineTo(gp, true, true);
                    }
                    else open = false;
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, lat == 0 ? eqPen : gridPen, g);
        }

        for (var lon = -180; lon < 180; lon += 30)
        {
            var g = new StreamGeometry();
            using (var sg = g.Open())
            {
                var open = false;
                for (var lat = -75; lat <= 75; lat += 5)
                {
                    if (ProjectGlobe(lat, lon, sinPhi0, cosPhi0, lon0Rad, cx, cy, R, out var gp, out var cc) && cc > 0.01)
                    {
                        if (!open) { sg.BeginFigure(gp, false, false); open = true; }
                        else sg.LineTo(gp, true, true);
                    }
                    else open = false;
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, gridPen, g);
        }

        // 3) 3D Orthographic Continents Dot-Matrix with depth shading & aurora sweep
        var coastTint = Color.FromRgb(
            (byte)((subtle.R * 2 + accent.R) / 3),
            (byte)((subtle.G * 2 + accent.G) / 3),
            (byte)((subtle.B * 2 + accent.B) / 3));
        var inlandBright = new SolidColorBrush(A(subtle, 0.36)); inlandBright.Freeze();
        var inlandDim = new SolidColorBrush(A(subtle, 0.19)); inlandDim.Freeze();
        var coastBright = new SolidColorBrush(A(coastTint, 0.72)); coastBright.Freeze();
        var coastDim = new SolidColorBrush(A(coastTint, 0.38)); coastDim.Freeze();
        var auroraBrush = new SolidColorBrush(A(accent, 0.88)); auroraBrush.Freeze();

        var baseDotR = Math.Clamp(R * 0.0125, 0.75, 2.25);
        var sweepPos = anim ? -1.1 + Frac(t / 7.5) * 2.2 : -99.0;

        for (var r = 0; r < Rows; r++)
        {
            var sinPhi = RowSinLat[r];
            var cosPhi = RowCosLat[r];
            for (var c = 0; c < Cols; c++)
            {
                if (!IsLand(r, c)) continue;
                var dLon = ColLonRad[c] - lon0Rad;
                var cosDLon = Math.Cos(dLon);
                var cosC = sinPhi0 * sinPhi + cosPhi0 * cosPhi * cosDLon;
                if (cosC <= 0.03) continue; // backface culling

                var sinDLon = Math.Sin(dLon);
                var px = cx + R * cosPhi * sinDLon;
                var py = cy - R * (cosPhi0 * sinPhi - sinPhi0 * cosPhi * cosDLon);
                var coast = IsCoastalLand(r, c);
                var rad = baseDotR * (coast ? 1.24 : 1.0) * (0.52 + 0.48 * cosC);

                var normDiag = ((px - cx) + (py - cy) * 0.35) / R;
                var inAurora = anim && Math.Abs(normDiag - sweepPos) < 0.18;
                var brush = inAurora
                    ? auroraBrush
                    : coast
                        ? (cosC > 0.45 ? coastBright : coastDim)
                        : (cosC > 0.45 ? inlandBright : inlandDim);
                dc.DrawEllipse(brush, null, new Point(px, py), rad, rad);
            }
        }

        // 4) Project visible Country Pins onto the 3D sphere & run anti-overlap separation
        _pins.Clear();
        var rawPins = new List<(CountryRow Row, Point TrueAt, Point At, double Radius, Color Color, int Index, double Depth)>();
        var idx = 0;
        foreach (var row in rows)
        {
            if (row.Code.Length != 2 || !Centroids.TryGetValue(row.Code, out var c)) continue;
            if (!ProjectGlobe(c.Lat, c.Lon, sinPhi0, cosPhi0, lon0Rad, cx, cy, R, out var pt, out var cosC) || cosC < 0.04)
                continue;
            var radius = Math.Min(4.5 + 2.0 * Math.Sqrt(Math.Max(1, row.Count)), 12.5) * (0.76 + 0.28 * cosC);
            var color = row.BestMs is not { } ms ? accent : ms < 300 ? good : ms < 800 ? warn : bad;
            rawPins.Add((row, pt, pt, radius, color, idx++, cosC));
        }

        for (var iter = 0; iter < 5; iter++)
        {
            for (var a = 0; a < rawPins.Count; a++)
            {
                for (var b = a + 1; b < rawPins.Count; b++)
                {
                    var pa = rawPins[a]; var pb = rawPins[b];
                    var delta = pb.At - pa.At;
                    var dist = delta.Length;
                    var minDist = pa.Radius + pb.Radius + 5.0;
                    if (dist < minDist)
                    {
                        var dir = dist > 0.01 ? delta / dist : new Vector(Math.Cos(a * 1.7), Math.Sin(a * 1.7));
                        var push = (minDist - dist) * 0.45;
                        pa.At -= dir * push;
                        pb.At += dir * push;
                        rawPins[a] = pa;
                        rawPins[b] = pb;
                    }
                }
            }
        }

        foreach (var rp in rawPins.OrderBy(p => p.Depth))
            _pins.Add(new Pin(rp.Row, rp.At, rp.TrueAt, rp.Radius, rp.Color, rp.Index));

        Point? home = HomeCode is { Length: 2 } hc && Centroids.TryGetValue(hc, out var hcc)
            && ProjectGlobe(hcc.Lat, hcc.Lon, sinPhi0, cosPhi0, lon0Rad, cx, cy, R, out var hp0, out var hCos) && hCos > 0.02
            ? hp0 : null;
        var active = _pins.FirstOrDefault(p => string.Equals(p.Row.Code, ActiveCode, StringComparison.OrdinalIgnoreCase));
        var selected = _pins.FirstOrDefault(p => string.Equals(p.Row.Code, SelectedCode, StringComparison.OrdinalIgnoreCase));
        var externalHover = _hover ?? (HoveredCode is { Length: 2 } hvc
            ? _pins.FirstOrDefault(p => string.Equals(p.Row.Code, hvc, StringComparison.OrdinalIgnoreCase))
            : null);

        // 5) 3D Orbital Routes & Mesh
        if (home is { } h0)
        {
            var meshPen = new Pen(new SolidColorBrush(A(subtle, 0.14)), 0.85) { DashStyle = new DashStyle([2, 4], 0) };
            meshPen.Freeze();
            foreach (var pin in _pins)
            {
                if (ReferenceEquals(pin, active) || ReferenceEquals(pin, selected) || ReferenceEquals(pin, externalHover)) continue;
                var d = (pin.At - h0).Length;
                if (d < 8) continue;
                var mid = new Point((h0.X + pin.At.X) / 2, (h0.Y + pin.At.Y) / 2 - Math.Min(d * 0.30, R * 0.42));
                var g = new StreamGeometry();
                using (var sg = g.Open()) { sg.BeginFigure(h0, false, false); sg.QuadraticBezierTo(mid, pin.At, true, false); }
                g.Freeze();
                dc.DrawGeometry(null, meshPen, g);
            }

            var previewPin = externalHover ?? selected;
            if (previewPin is not null && !ReferenceEquals(previewPin, active) && (previewPin.At - h0).Length > 4)
            {
                var prevBadge = previewPin.Row.Best.Length > 0
                    ? $"{HomeCode ?? "IR"} ➔ {previewPin.Row.Code} · {previewPin.Row.Best}"
                    : $"{HomeCode ?? "IR"} ➔ {previewPin.Row.Code}";
                DrawRoute(dc, h0, previewPin.At, previewPin.Color, live: false, anim, t, prevBadge, popup, border, text);
            }

            if (active is not null && (active.At - h0).Length > 4)
            {
                var pingStr = !string.IsNullOrWhiteSpace(PingText) && PingText != "—" ? PingText : active.Row.Best;
                var liveBadge = !string.IsNullOrWhiteSpace(pingStr)
                    ? $"{HomeCode ?? "IR"} ➔ {ActiveCode} · {pingStr}"
                    : $"{HomeCode ?? "IR"} ➔ {ActiveCode}";
                DrawRoute(dc, h0, active.At, active.Color, live: true, anim, t, liveBadge, popup, border, text);
            }
        }

        // 6) Draw 3D Country Pins & Home Marker
        var leaderPen = new Pen(new SolidColorBrush(A(subtle, 0.48)), 0.9) { DashStyle = new DashStyle([1.5, 2], 0) };
        leaderPen.Freeze();
        var anchorBrush = new SolidColorBrush(A(text, 0.55));
        anchorBrush.Freeze();

        foreach (var pin in _pins)
        {
            var pop = anim ? EaseOutBack((t - _introAt - pin.Index * 0.05) / 0.5) : 1;
            if (pop <= 0.01) continue;
            var r = pin.Radius * pop;
            var isActive = ReferenceEquals(pin, active);
            var isSel = ReferenceEquals(pin, selected);
            var isHover = ReferenceEquals(pin, externalHover);
            if (isHover) r *= 1.22;

            if ((pin.At - pin.TrueAt).Length > 3.5)
            {
                dc.DrawLine(leaderPen, pin.TrueAt, pin.At);
                dc.DrawEllipse(anchorBrush, null, pin.TrueAt, 1.6, 1.6);
            }

            var halo = new RadialGradientBrush(A(pin.Color, isActive ? 0.62 : isHover ? 0.50 : 0.34), A(pin.Color, 0));
            dc.DrawEllipse(halo, null, pin.At, r * 2.8, r * 2.8);

            if (anim)
            {
                var ph = Frac(t / (isActive ? 1.5 : 2.5) + pin.Index * 0.173);
                var pen = new Pen(new SolidColorBrush(A(pin.Color, (1 - ph) * (isActive ? 0.85 : 0.68))), isActive ? 1.5 : 1.2);
                dc.DrawEllipse(null, pen, pin.At, r + ph * r * 2.3, r + ph * r * 2.3);
            }

            if (isSel || isHover)
            {
                var orbit = new Pen(new SolidColorBrush(A(isHover ? accent : text, 0.92)), 1.4)
                {
                    DashStyle = new DashStyle([2.2, 2.2], anim ? -t * 4.5 : 0),
                    DashCap = PenLineCap.Round,
                };
                dc.DrawEllipse(null, orbit, pin.At, r + 4.5, r + 4.5);
            }

            var cLight = Color.FromArgb(0xFF, (byte)Math.Min(255, pin.Color.R + 75), (byte)Math.Min(255, pin.Color.G + 75), (byte)Math.Min(255, pin.Color.B + 75));
            var core = new RadialGradientBrush(cLight, pin.Color)
            { GradientOrigin = new Point(0.32, 0.28), Center = new Point(0.4, 0.38), RadiusX = 0.78, RadiusY = 0.78 };
            var pinRim = new Pen(new SolidColorBrush(A(text, isHover || isActive ? 0.98 : 0.62)), isActive || isHover ? 1.7 : 1.05);
            dc.DrawEllipse(core, pinRim, pin.At, r, r);

            if (pin.Row.Count > 1 && r >= 7.0)
            {
                var ft = Text(pin.Row.Count.ToString(CultureInfo.InvariantCulture), Math.Max(8, r * 0.92), Color.FromRgb(0x10, 0x10, 0x18), FontWeights.Bold);
                dc.DrawText(ft, new Point(pin.At.X - ft.Width / 2, pin.At.Y - ft.Height / 2));
            }
        }

        if (home is { } hp)
        {
            var d = 4.6;
            var g = new StreamGeometry();
            using (var sg = g.Open())
            {
                sg.BeginFigure(new Point(hp.X, hp.Y - d), true, true);
                sg.LineTo(new Point(hp.X + d, hp.Y), true, false);
                sg.LineTo(new Point(hp.X, hp.Y + d), true, false);
                sg.LineTo(new Point(hp.X - d, hp.Y), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(new SolidColorBrush(A(text, 0.96)), new Pen(new SolidColorBrush(accent), 1.4), g);
        }

        DrawHudAndLegend(dc, w, h, popup, border, text, subtle, accent, good, warn, bad);
        if (externalHover is { } hv)
        {
            DrawHoverCard(dc, hv, w, h, popup, border, text, subtle, accent, good);
        }
    }
}
