using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        /// <summary>v0.4.49.1 — the press started on the header row: the release toggles only then (click, not drag).</summary>
        public bool PressedOnHeader;
        /// <summary>v0.4.49.2 — pointer is inside the arrow's hit box (drives hover once per crossing, not per pixel).</summary>
        public bool ArrowHot;
    }

    /// <summary>v0.4.49.1 — cards that start FOLDED until the user opens them once (the choice then persists).
    /// Field request: the Advanced-Tunnels card and the Server Map must ship closed («تو حالت پیشفرض بسته باشه»).</summary>
    private static readonly HashSet<string> DefaultClosed = new(StringComparer.Ordinal) { "tunnels", "map" };

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement h || e.NewValue is not string key || key.Length == 0) return;
        var st = new FoldState { Key = key, Header = h };
        try
        {
            // saved choice wins; first run falls back to the per-key default (tunnels/map ship folded)
            st.Collapsed = AppHost.Get<SettingsRepository>().Get("ui.fold." + key) is { Length: > 0 } v ? v == "1" : DefaultClosed.Contains(key);
        }
        catch { /* design time */ }
        h.SetValue(StateProperty, st);
        h.Loaded += (_, _) => Attach(st);
        // NOTE: click handling lives on the HEADER ROW (wired in Attach), not here — Preview events only tunnel
        // through ANCESTORS of the hit element, so a handler on the title TextBlock would miss clicks on the row
        // beside the title (the exact «روی فلش نمی‌شه کلیک کنی» class of imprecision).
    }

    /// <summary>True when the click landed inside a control that owns its own mouse handling (buttons, combos…):
    /// such clicks must pass through untouched — never fold the card and never be swallowed by it.</summary>
    private static bool InInteractiveControl(DependencyObject? source, DependencyObject stop)
    {
        for (DependencyObject? d = source; d is not null && !ReferenceEquals(d, stop);
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase or ComboBox or TextBoxBase or Slider or Expander or Selector) return true;
        return false;
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

        // v0.4.49.1 — the WHOLE header row is the fold button (title, icon and the space beside them): the press
        // must begin AND end on the row, and clicks that landed on an interactive child (button/combo/switch)
        // pass straight through. This is what makes «بزنی روش بسته شه» work everywhere on the header.
        var row = HeaderRow(st);
        row.Cursor = Cursors.Hand;
        row.PreviewMouseLeftButtonDown += (_, ev) => st.PressedOnHeader = !InInteractiveControl(ev.OriginalSource as DependencyObject, row);
        row.PreviewMouseLeftButtonUp += (_, ev) =>
        {
            if (ev.Handled || !st.PressedOnHeader || InInteractiveControl(ev.OriginalSource as DependencyObject, row)) return;
            st.PressedOnHeader = false;
            ev.Handled = true;
            Toggle(st);
        };

        var layer = AdornerLayer.GetAdornerLayer(st.Card);
        if (layer is not null && (layer.GetAdorners(st.Card)?.OfType<ChevronAdorner>().Any() != true))
        {
            // v0.4.49.2 — the chevron is PURE DRAWING again (never hit-testable, never takes the mouse from the
            // card). Hover, cursor and the arrow click are driven by pointer POSITION on the card below.
            st.Chevron = new ChevronAdorner(st.Card) { Angle = st.Collapsed ? 90 : 0 };
            layer.Add(st.Chevron);
        }

        // v0.4.49.2 («روی فلش که میرم رگباری روشن خاموش میشه»): v0.4.49.1 made the adorner hit-testable while the
        // hover still ran on the card's MouseEnter/MouseLeave — crossing the chevron strokes flipped the hit
        // result card↔adorner, firing enter/leave storms = flicker. Hover is a pure function of the pointer
        // position now (distance to the arrow point), so there is no boundary left to thrash. The arrow CLICK
        // also lives here: a click inside the arrow's box toggles the card — whatever hit testing does with
        // OnRender strokes, the click lands on the card and bubbles up to this handler (rows/buttons that
        // handle the click themselves are skipped: handlers added this way ignore already-handled events).
        st.Card.MouseMove += (_, ev) => TrackArrow(st, ev.GetPosition(st.Card));
        st.Card.MouseLeave += (_, _) =>
        {
            if (!st.ArrowHot) return;
            st.ArrowHot = false;
            st.Chevron?.AnimateHover(0);
            st.Card.Cursor = null;
        };
        st.Card.MouseLeftButtonUp += (_, ev) =>
        {
            if (!ArrowHot(st, ev.GetPosition(st.Card))) return;
            ev.Handled = true;
            Toggle(st);
        };

        st.Card.ClipToBounds = true;
        // after layout: position the chevron on the header row and restore a remembered fold without animation
        Reposition(st);
        // the chevron must stay glued to the header row on every layout pass (a panel above grows, a tab strip
        // opens, font scale changes…) — never floating at its Loaded spot.
        st.Card.SizeChanged += (_, _) => { if (!st.Collapsed) Reposition(st); else st.Chevron?.InvalidateVisual(); };
    }

    private static void Reposition(FoldState st)
    {
        st.Card!.Dispatcher.BeginInvoke(() =>
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

    /// <summary>The chevron is drawn at (width−10, header-row middle); anything within ±14 px of that point is
    /// the arrow's button — hover, hand cursor and click all read from this one geometric fact.</summary>
    private static bool ArrowHot(FoldState st, Point p)
    {
        var card = st.Card!;
        return Math.Abs(p.X - (card.ActualWidth - 10)) <= 14 && Math.Abs(p.Y - HeaderMid(st)) <= 14;
    }

    /// <summary>Position-driven hover: flips ONCE per crossing of the arrow box — there is no enter/leave
    /// boundary to thrash, so the arrow cannot blink.</summary>
    private static void TrackArrow(FoldState st, Point p)
    {
        var hot = ArrowHot(st, p);
        if (hot == st.ArrowHot) return;
        st.ArrowHot = hot;
        st.Chevron?.AnimateHover(hot ? 1 : 0);
        st.Card!.Cursor = hot ? Cursors.Hand : null;
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

    /// <summary>Small chevron drawn in the card's end-side gutter, level with the header row; rotates when folded.
    /// v0.4.49: follows the mouse (accent color + slight grow on hover) so the fold affordance is discoverable.
    /// v0.4.49.2: PURE DRAWING — never hit-testable, never receives or steals mouse events (that was the
    /// flicker); hover, cursor and click are all driven by CardFold's position handlers on the card itself.</summary>
    private sealed class ChevronAdorner : Adorner
    {
        public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(ChevronAdorner),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
        public double CenterY { get; set; } = 28;
        /// <summary>hover feedback: 0 = idle, 1 = hovered (animated; drives color + scale).</summary>
        public double Hover { get; set; }

        public ChevronAdorner(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
        }

        /// <summary>Called by CardFold's position tracking when the pointer crosses the arrow's hit box.</summary>
        internal void AnimateHover(double to) =>
            BeginAnimation(HoverProxyProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.12)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        private static readonly DependencyProperty HoverProxyProperty = DependencyProperty.Register(
            "HoverProxy", typeof(double), typeof(ChevronAdorner), new PropertyMetadata(0.0, (d, _) => { var a = (ChevronAdorner)d; a.Hover = (double)d.GetValue(HoverProxyProperty); a.InvalidateVisual(); }));

        protected override void OnRender(DrawingContext dc)
        {
            if (AdornedElement is not FrameworkElement fe || fe.ActualWidth < 40) return;
            var hovered = Hover > 0.5;
            var brush = hovered
                ? (fe.TryFindResource("AccentBrush") as Brush ?? Brushes.White)
                : (fe.TryFindResource("SubtleTextBrush") as Brush ?? Brushes.Gray);
            var x = fe.ActualWidth - 10;
            var y = Math.Min(CenterY, fe.ActualHeight - 10);
            var pen = new Pen(brush, hovered ? 2.0 : 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x - 3.5, y - 1.8), false, false);
                ctx.LineTo(new Point(x, y + 1.8), true, true);
                ctx.LineTo(new Point(x + 3.5, y - 1.8), true, true);
            }
            g.Freeze();
            dc.PushTransform(new RotateTransform(Angle, x, y));
            dc.PushOpacity(hovered ? 1.0 : 0.75);
            dc.DrawGeometry(null, pen, g);
            dc.Pop();
            dc.Pop();
        }
    }
}
