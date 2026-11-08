using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Koalix.App.Controls;

/// <summary>Lightweight latency/traffic chart rendered with a single StreamGeometry (no chart library, low CPU).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double?>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>v0.4.19 — «پایش پینگ اون امواجش تکون بخوره»: when Flow is on, the line itself undulates — every
    /// point rides a slow sine wave (Phase, animated) so the chart reads as living water, not a static polyline.
    /// The data values stay untouched: the bob is a fixed ±2.5 px visual offset added at render time.</summary>
    public static readonly DependencyProperty FlowProperty = DependencyProperty.Register(nameof(Flow), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, (d, _) => ((Sparkline)d).OnFlowChanged()));
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double?>? Values { get => (IEnumerable<double?>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Flow { get => (bool)GetValue(FlowProperty); set => SetValue(FlowProperty, value); }
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    private Storyboard? _flow;

    public Sparkline()
    {
        FlowDirection = FlowDirection.LeftToRight; // time axis always left→right, even in RTL
        Loaded += (_, _) => StartFlow();
        Unloaded += (_, _) => StopFlow();
    }

    private void OnFlowChanged() { if (IsLoaded) { if (Flow) StartFlow(); else StopFlow(); } }

    private void StartFlow()
    {
        if (!Flow || !Fx.Enabled || _flow is not null) return;
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var a = new DoubleAnimation(0, Math.PI * 2, TimeSpan.FromSeconds(3.1)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(a, this);
        Storyboard.SetTargetProperty(a, new PropertyPath(nameof(Phase)));
        sb.Children.Add(a);
        Timeline.SetDesiredFrameRate(sb, 30);
        sb.Begin(this, true);
        _flow = sb;
    }

    private void StopFlow() { try { _flow?.Remove(this); } catch { } _flow = null; }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= s.Changed;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += s.Changed;
    }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        // Subtle horizontal grid
        var gridBrush = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray;
        var gridPen = new Pen(gridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) }; gridPen.Freeze();
        for (var i = 1; i <= 3; i++) dc.DrawLine(gridPen, new Point(0, Math.Round(h * i / 4) + 0.5), new Point(w, Math.Round(h * i / 4) + 0.5));

        var v = Values?.ToList();
        if (v is null || v.Count < 2) return;
        var valid = v.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (valid.Count == 0) return;
        double max = Math.Max(valid.Max() * 1.2, 1), step = w / (v.Count - 1);
        var pen = new Pen(Stroke, 2.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();
        var accent = TryFindResource("AccentColor") is Color c ? c : Colors.MediumPurple;
        var area = new LinearGradientBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90); area.Freeze();

        // v0.4.19: the flowing bob — each point drifts up/down on its own sine (phase-shifted by index) so the
        // whole line ripples while new samples keep arriving. Disabled bars get a flat zero bob.
        double Bob(int i) => Flow ? Math.Sin(Phase + i * 0.55) * 2.5 : 0;

        // Contiguous runs (gaps = packet loss) drawn as smoothed curves with a gradient area underneath.
        var line = new StreamGeometry(); var fill = new StreamGeometry();
        using (var g = line.Open())
        using (var f = fill.Open())
        {
            var run = new List<Point>();
            void Flush()
            {
                if (run.Count == 0) return;
                g.BeginFigure(run[0], false, false);
                f.BeginFigure(new Point(run[0].X, h), true, true); f.LineTo(run[0], false, false);
                for (var i = 1; i < run.Count; i++)
                {
                    var mid = new Point((run[i - 1].X + run[i].X) / 2, (run[i - 1].Y + run[i].Y) / 2);
                    g.QuadraticBezierTo(run[i - 1], mid, true, true); f.QuadraticBezierTo(run[i - 1], mid, false, true);
                }
                g.LineTo(run[^1], true, true); f.LineTo(run[^1], false, true); f.LineTo(new Point(run[^1].X, h), false, false);
                run.Clear();
            }
            for (var i = 0; i < v.Count; i++)
            {
                if (v[i] is not { } y) { Flush(); continue; }
                run.Add(new Point(i * step, h - y / max * (h - 6) - 3 + Bob(i)));
            }
            Flush();
        }
        line.Freeze(); fill.Freeze();
        dc.DrawGeometry(area, null, fill);
        dc.DrawGeometry(null, pen, line);
        if (v[^1] is { } last)
        {
            var p = new Point(w, h - last / max * (h - 6) - 3 + Bob(v.Count - 1));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B)), null, p, 7, 7);
            dc.DrawEllipse(Stroke, null, p, 3.5, 3.5);
        }
        var lossBrush = (Brush?)TryFindResource("DangerBrush") ?? Brushes.Red;
        for (int i = 0; i < v.Count; i++) if (v[i] is null) dc.DrawRectangle(lossBrush, null, new Rect(i * step - 1, h - 5, 2, 5));
    }
}
