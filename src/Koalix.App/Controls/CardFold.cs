using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Koalix.App.Services;
using Koalix.Infrastructure;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.32 — collapsible dashboard cards. Put <c>c:CardFold.Key="speed"</c> on a card's title element: clicking
/// the title folds the enclosing Card down to its header row (smooth MaxHeight + clip animation, chevron rotates)
/// and the choice is remembered per card ("ui.fold.&lt;key&gt;"). Nothing inside the card is touched — no local
/// Visibility values that would fight existing bindings — the card is simply clipped to its header.
/// </summary>
public static class CardFold
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(CardFold), new PropertyMetadata(null, OnKeyChanged));
    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? v) => d.SetValue(KeyProperty, v);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(FoldState), typeof(CardFold), new PropertyMetadata(null));

    private sealed class FoldState
    {
        public required string Key;
        public required FrameworkElement Header;
        public Border? Card;
        public ChevronAdorner? Chevron;
        public bool Collapsed;
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement h || e.NewValue is not string key || key.Length == 0) return;
        var st = new FoldState { Key = key, Header = h };
        try { st.Collapsed = AppHost.Get<SettingsRepository>().Get("ui.fold." + key) == "1"; } catch { /* design time */ }
        h.SetValue(StateProperty, st);
        h.Cursor = Cursors.Hand;
        h.Loaded += (_, _) => Attach(st);
        h.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; Toggle(st); };
    }

    private static Border? FindCard(DependencyObject from)
    {
        Style? card = null;
        try { card = (from as FrameworkElement)?.TryFindResource("Card") as Style; } catch { }
        for (var p = VisualTreeHelper.GetParent(from); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is Border b && (b.Style == card || (card is not null && b.Style?.BasedOn == card))) return b;
        return null;
    }

    private static void Attach(FoldState st)
    {
        st.Card ??= FindCard(st.Header);
        if (st.Card is null) return;
        var layer = AdornerLayer.GetAdornerLayer(st.Card);
        if (layer is not null && (layer.GetAdorners(st.Card)?.OfType<ChevronAdorner>().Any() != true))
        {
            st.Chevron = new ChevronAdorner(st.Card) { Angle = st.Collapsed ? 90 : 0 };
            layer.Add(st.Chevron);
        }
        st.Card.ClipToBounds = true;
        // after layout: position the chevron on the header row and restore a remembered fold without animation
        st.Card.Dispatcher.BeginInvoke(() =>
        {
            if (st.Chevron is not null) st.Chevron.CenterY = HeaderMid(st);
            if (st.Collapsed) { st.Card.BeginAnimation(FrameworkElement.MaxHeightProperty, null); st.Card.MaxHeight = CollapsedHeight(st); }
            st.Chevron?.InvalidateVisual();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The top-level row of the card that contains the header (so buttons beside the title stay whole).</summary>
    private static FrameworkElement HeaderRow(FoldState st)
    {
        FrameworkElement row = st.Header;
        for (var p = VisualTreeHelper.GetParent(row); p is FrameworkElement fe && p != st.Card && p != st.Card!.Child; p = VisualTreeHelper.GetParent(p))
            row = fe;
        return row;
    }

    private static double CollapsedHeight(FoldState st)
    {
        var card = st.Card!;
        try
        {
            var row = HeaderRow(st);
            var bottom = row.TransformToAncestor(card).Transform(new Point(0, row.ActualHeight)).Y;
            return Math.Max(24, bottom + card.Padding.Bottom * 0.7 + card.BorderThickness.Bottom);
        }
        catch { return 56; }
    }

    private static double HeaderMid(FoldState st)
    {
        try
        {
            var row = HeaderRow(st);
            return row.TransformToAncestor(st.Card!).Transform(new Point(0, row.ActualHeight / 2)).Y;
        }
        catch { return 28; }
    }

    private static void Toggle(FoldState st)
    {
        st.Card ??= FindCard(st.Header);
        if (st.Card is null) return;
        var card = st.Card;
        st.Collapsed = !st.Collapsed;
        try { AppHost.Get<SettingsRepository>().Set("ui.fold." + st.Key, st.Collapsed ? "1" : "0"); } catch { }

        var dur = TimeSpan.FromSeconds(Fx.Enabled ? 0.32 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var small = CollapsedHeight(st);
        if (st.Collapsed)
        {
            var from = card.ActualHeight;
            card.BeginAnimation(FrameworkElement.MaxHeightProperty, new DoubleAnimation(from, small, dur) { EasingFunction = ease });
        }
        else
        {
            // measure the natural height, then grow to it and release the cap so live content can resize freely
            card.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            card.MaxHeight = double.PositiveInfinity;
            card.Measure(new Size(card.ActualWidth + card.Margin.Left + card.Margin.Right, double.PositiveInfinity));
            var full = Math.Max(small, card.DesiredSize.Height - card.Margin.Top - card.Margin.Bottom);
            var grow = new DoubleAnimation(small, full, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            grow.Completed += (_, _) => { if (!st.Collapsed) card.MaxHeight = double.PositiveInfinity; };
            card.MaxHeight = full;
            card.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);
        }
        if (st.Chevron is not null)
        {
            st.Chevron.CenterY = HeaderMid(st);
            st.Chevron.BeginAnimation(ChevronAdorner.AngleProperty, new DoubleAnimation(st.Collapsed ? 90 : 0, dur) { EasingFunction = ease });
        }
    }

    /// <summary>Small chevron drawn in the card's end-side gutter, level with the header row; rotates when folded.</summary>
    private sealed class ChevronAdorner : Adorner
    {
        public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(ChevronAdorner),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
        public double CenterY { get; set; } = 28;

        public ChevronAdorner(UIElement adorned) : base(adorned) { IsHitTestVisible = false; }

        protected override void OnRender(DrawingContext dc)
        {
            if (AdornedElement is not FrameworkElement fe || fe.ActualWidth < 40) return;
            var brush = fe.TryFindResource("SubtleTextBrush") as Brush ?? Brushes.Gray;
            var x = fe.ActualWidth - 10;
            var y = Math.Min(CenterY, fe.ActualHeight - 10);
            var pen = new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x - 3.5, y - 1.8), false, false);
                ctx.LineTo(new Point(x, y + 1.8), true, true);
                ctx.LineTo(new Point(x + 3.5, y - 1.8), true, true);
            }
            g.Freeze();
            dc.PushTransform(new RotateTransform(Angle, x, y));
            dc.PushOpacity(0.75);
            dc.DrawGeometry(null, pen, g);
            dc.Pop();
            dc.Pop();
        }
    }
}
