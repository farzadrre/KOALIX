using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Koalix.App.Controls;

/// <summary>v0.4.45 — a wrap panel that only builds the cards you can see («تو داشبورد که میخوام برم یکم کند شده، شاید
/// بخاطر تعداد کانفیگاس؟»). The dashboard's server list used a plain WrapPanel inside a 270 px ScrollViewer: with 148
/// configs that is ~4,900 live elements for about 8 visible cards, all rebuilt on every visit and every re-sort.
/// This panel lays the items out on a uniform grid (cell = the largest card measured so far), reports the full
/// height so the surrounding ScrollViewer keeps scrolling by pixel, and realizes only the rows inside that
/// ScrollViewer's viewport plus one row either side.
/// Assumes it is the top of the scrolled content (true for an ItemsControl placed directly in a ScrollViewer).
/// With no ScrollViewer above it, every item is realized — a plain uniform wrap panel.</summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel
{
    private const int BufferRows = 1;
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>One grid cell (card + its margin). Only ever grows: rows of slightly different height (flag vs glyph
    /// cards) would otherwise change the total height while scrolling and make the list jump.</summary>
    private Size _slot;
    private int _columns = 1;
    private int _count;
    private int _firstRow, _lastRow = -1;
    private ScrollViewer? _scroll;

    public VirtualizingWrapPanel()
    {
        Loaded += (_, _) => { HookScroll(); InvalidateMeasure(); };
        Unloaded += (_, _) => UnhookScroll();
    }

    /// <summary>Cards per visual row in the current layout (the WASD navigation steps by this).</summary>
    public int Columns => _columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;                 // must be read before the generator is used
        _count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (_count == 0) { _firstRow = 0; _lastRow = -1; return default; }
        if (_scroll is null) HookScroll();

        if (_slot.Width <= 0 || _slot.Height <= 0)       // first layout: one card tells us the cell size
        {
            Realize(children, 0, 0);
            MeasureRealized(children);
            if (_slot.Width <= 0 || _slot.Height <= 0) return default;
        }

        for (var pass = 0; pass < 3; pass++)
        {
            _columns = double.IsInfinity(availableSize.Width)
                ? _count
                : Math.Max(1, (int)Math.Floor((availableSize.Width + 0.5) / _slot.Width));
            (_firstRow, _lastRow) = VisibleRows();
            Realize(children, _firstRow * _columns, Math.Min(_count, (_lastRow + 1) * _columns) - 1);
            if (!MeasureRealized(children)) break;       // the cell grew → fewer rows fit, trim and re-check
        }

        var rows = (_count + _columns - 1) / _columns;
        return new Size(Math.Min(_count, _columns) * _slot.Width, rows * _slot.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var gen = ItemContainerGenerator;
        for (var i = 0; i < children.Count; i++)
        {
            var index = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            children[i].Arrange(new Rect(index % _columns * _slot.Width, index / _columns * _slot.Height, _slot.Width, _slot.Height));
        }
        return finalSize;
    }

    /// <summary>Measures every realized card; true when the cell had to grow.</summary>
    private bool MeasureRealized(UIElementCollection children)
    {
        double w = _slot.Width, h = _slot.Height;
        foreach (UIElement child in children)
        {
            child.Measure(Unbounded);
            w = Math.Max(w, child.DesiredSize.Width);
            h = Math.Max(h, child.DesiredSize.Height);
        }
        if (w == _slot.Width && h == _slot.Height) return false;
        _slot = new Size(w, h);
        return true;
    }

    /// <summary>Rows inside the ScrollViewer's viewport, plus the buffer.</summary>
    private (int First, int Last) VisibleRows()
    {
        var rows = (_count + _columns - 1) / _columns;
        if (rows <= 0) return (0, -1);
        if (_scroll is null || _slot.Height <= 0) return (0, rows - 1);
        var view = _scroll.ViewportHeight;
        // the very first measure runs before the ScrollViewer knows its viewport — its MaxHeight is the upper bound
        if (view <= 0) view = double.IsInfinity(_scroll.MaxHeight) ? _scroll.ActualHeight : _scroll.MaxHeight;
        if (view <= 0 || double.IsInfinity(view) || double.IsNaN(view)) view = SystemParameters.PrimaryScreenHeight;
        var top = Math.Max(0, _scroll.VerticalOffset);
        var first = Math.Clamp((int)Math.Floor(top / _slot.Height) - BufferRows, 0, rows - 1);
        var last = Math.Clamp((int)Math.Floor((top + view) / _slot.Height) + BufferRows, first, rows - 1);
        return (first, last);
    }

    /// <summary>Makes the realized set exactly the items [first..last] (plus a card that holds the keyboard focus).</summary>
    private void Realize(UIElementCollection children, int first, int last)
    {
        var gen = ItemContainerGenerator;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var index = gen.IndexFromGeneratorPosition(pos);
            if (index >= first && index <= last) continue;
            // never throw away the card the keyboard is on — focus would fall out of the page and WASD would stop
            if (index >= 0 && children[i].IsKeyboardFocusWithin) continue;
            gen.Remove(pos, 1);
            RemoveInternalChildRange(i, 1);
        }
        if (last < first) return;

        var start = gen.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (gen.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                if (gen.GenerateNext(out var isNew) is not UIElement child) break;
                if (!isNew) continue;
                if (childIndex >= children.Count) AddInternalChild(child);
                else InsertInternalChild(childIndex, child);
                gen.PrepareItemContainer(child);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // Reset is already handled by the base class (it empties the panel); Add needs nothing — measure realizes it.
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Replace:
                // Only raised when the generator could not reuse the container (the item is its own container): it has
                // already put the new one in the realized slot, so swap the child in place — same as Panel does.
                if (args.ItemUICount <= 0) break;
                using (ItemContainerGenerator.StartAt(args.Position, GeneratorDirection.Forward, true))
                {
                    if (ItemContainerGenerator.GenerateNext(out _) is not UIElement fresh) break;
                    RemoveInternalChildRange(args.Position.Index, 1);
                    InsertInternalChild(args.Position.Index, fresh);
                    ItemContainerGenerator.PrepareItemContainer(fresh);
                }
                break;
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }
    }

    protected override void BringIndexIntoView(int index) => ScrollToIndex(index);

    /// <summary>Scrolls the list just far enough to show the row of <paramref name="index"/>; the card itself is
    /// realized by the layout pass that follows.</summary>
    public void ScrollToIndex(int index)
    {
        if (_scroll is null || _slot.Height <= 0 || index < 0 || index >= _count) return;
        var top = index / _columns * _slot.Height;
        var bottom = top + _slot.Height;
        if (top < _scroll.VerticalOffset) _scroll.ScrollToVerticalOffset(top);
        else if (bottom > _scroll.VerticalOffset + _scroll.ViewportHeight) _scroll.ScrollToVerticalOffset(bottom - _scroll.ViewportHeight);
    }

    private void HookScroll()
    {
        if (_scroll is not null) return;
        for (var d = VisualTreeHelper.GetParent(this); d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not ScrollViewer sv) continue;
            _scroll = sv;
            sv.ScrollChanged += OnScrollChanged;
            return;
        }
    }

    private void UnhookScroll()
    {
        if (_scroll is null) return;
        _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _scroll)) return;   // the event bubbles — ignore nested scrollers
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;
        if (VisibleRows() != (_firstRow, _lastRow)) InvalidateMeasure();
    }
}
