using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace Koalix.App.Controls;

/// <summary>
/// Live download/upload chart fed by the sing-box Clash /traffic stream: two smoothed series with a soft area under the
/// download line, drawn with StreamGeometry like Sparkline (no chart library, low CPU). The time axis is always
/// left→right, even in RTL. (v0.4.3)
/// </summary>
public sealed class SpeedChart : FrameworkElement
{
    public static readonly DependencyProperty DownProperty = DependencyProperty.Register(nameof(Down), typeof(IEnumerable<double?>), typeof(SpeedChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));
    public static readonly DependencyProperty UpProperty = DependencyProperty.Register(nameof(Up), typeof(IEnumerable<double?>), typeof(SpeedChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));
    public static readonly DependencyProperty DownStrokeProperty = DependencyProperty.Register(nameof(DownStroke), typeof(Brush), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Brushes.MediumSpringGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpStrokeProperty = DependencyProperty.Register(nameof(UpStroke), typeof(Brush), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double?>? Down { get => (IEnumerable<double?>?)GetValue(DownProperty); set => SetValue(DownProperty, value); }
    public IEnumerable<double?>? Up { get => (IEnumerable<double?>?)GetValue(UpProperty); set => SetValue(UpProperty, value); }
    public Brush DownStroke { get => (Brush)GetValue(DownStrokeProperty); set => SetValue(DownStrokeProperty, value); }
    public Brush UpStroke { get => (Brush)GetValue(UpStrokeProperty); set => SetValue(UpStrokeProperty, value); }

    public SpeedChart() { FlowDirection = FlowDirection.LeftToRight; }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (SpeedChart)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= s.Changed;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += s.Changed;
    }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        // Subtle horizontal grid, same look as the latency sparkline.
        var gridBrush = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray;
        var gridPen = new Pen(gridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) }; gridPen.Freeze();
        for (var i = 1; i <= 3; i++) dc.DrawLine(gridPen, new Point(0, Math.Round(h * i / 4) + 0.5), new Point(w, Math.Round(h * i / 4) + 0.5));

        var down = Down?.Where(x => x.HasValue).Select(x => x!.Value).ToList() ?? [];
        var up = Up?.Where(x => x.HasValue).Select(x => x!.Value).ToList() ?? [];
        if (down.Count == 0 && up.Count == 0) return;
        double max = Math.Max(Math.Max(down.DefaultIfEmpty(0).Max(), up.DefaultIfEmpty(0).Max()) * 1.15, 1);

        DrawSeries(dc, down, max, w, h, DownStroke, area: true);
        DrawSeries(dc, up, max, w, h, UpStroke, area: false);
    }

    private void DrawSeries(DrawingContext dc, List<double> v, double max, double w, double h, Brush stroke, bool area)
    {
        if (v.Count < 2) return;
        double step = w / (v.Count - 1);
        var accent = stroke is SolidColorBrush sc ? sc.Color : Colors.Gray;
        var fill = new LinearGradientBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90); fill.Freeze();
        var pen = new Pen(stroke, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();

        var pts = new Point[v.Count];
        for (var i = 0; i < v.Count; i++) pts[i] = new Point(i * step, h - v[i] / max * (h - 10) - 4);

        var line = new StreamGeometry(); var geoFill = new StreamGeometry();
        using (var g = line.Open())
        using (var f = geoFill.Open())
        {
            g.BeginFigure(pts[0], false, false);
            f.BeginFigure(new Point(pts[0].X, h), true, true); f.LineTo(pts[0], false, false);
            for (var i = 1; i < pts.Length; i++)
            {
                var mid = new Point((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2);
                g.QuadraticBezierTo(pts[i - 1], mid, true, true); f.QuadraticBezierTo(pts[i - 1], mid, false, true);
            }
            g.LineTo(pts[^1], true, true);
            f.LineTo(pts[^1], false, true); f.LineTo(new Point(pts[^1].X, h), false, false);
        }
        line.Freeze(); geoFill.Freeze();
        if (area) dc.DrawGeometry(fill, null, geoFill);
        dc.DrawGeometry(null, pen, line);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B)), null, pts[^1], 6, 6);
        dc.DrawEllipse(stroke, null, pts[^1], 3, 3);
    }
}
