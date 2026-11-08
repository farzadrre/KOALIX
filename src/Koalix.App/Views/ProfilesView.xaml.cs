using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Koalix.App.ViewModels;

namespace Koalix.App.Views;

public partial class ProfilesView : UserControl
{
    private bool _entered;
    // v0.4.19 drag-and-drop state: the row being dragged + the press point (a small threshold separates
    // a click/select from a drag so both keep working naturally).
    private ProfileRow? _dragRow;
    private Point _dragStart;
    private bool _importOpen;
    // v0.4.23 (user: «بخش تونل ها هم اون پایینش که تست و این چیزاس خیلی شلوغ شده»): the tools panel state —
    // collapsed by default so small windows stop spending ~120 px of fixed chrome on rarely used buttons.
    private bool _toolsOpen;

    public ProfilesView()
    {
        InitializeComponent();
        // v0.4.12: the import card, the subscriptions section and the list slide in one after another —
        // same motion language as the dashboard.
        Loaded += (_, _) =>
        {
            if (_entered) return;
            _entered = true;
            if (FindName("Cards") is Panel p) Controls.Fx.Stagger(p, 0.05);
        };
    }

    /// <summary>v0.4.19: expand/collapse the import box (collapsed by default so the list gets the space).</summary>
    private void ImportHeader_Click(object sender, RoutedEventArgs e)
    {
        _importOpen = !_importOpen;
        ImportBody.Visibility = _importOpen ? Visibility.Visible : Visibility.Collapsed;
        if (FindName("Chev") is System.Windows.Media.RotateTransform chev)
            chev.Angle = _importOpen ? 180 : 0;
        if (FindName("ImportBody") is FrameworkElement body && _importOpen)
            Controls.Fx.SlideIn(body, 10, 0.25);
    }

    /// <summary>v0.4.23: expand/collapse the tunnels tools panel (collapsed by default so the server list
    /// breathes in resized windows). The chevron turns with a 0.18 s animation — same motion language as
    /// the import header; the «ابزارها» label itself stays constant.</summary>
    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        _toolsOpen = !_toolsOpen;
        ToolsPanel.Visibility = _toolsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (FindName("ToolsChevron") is System.Windows.Media.RotateTransform chevron)
            chevron.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(_toolsOpen ? 180 : 0, TimeSpan.FromMilliseconds(180)));
        if (_toolsOpen)
            Controls.Fx.SlideIn(ToolsPanel, 10, 0.25);
    }

    /* ═══════════ drag-and-drop reordering (v0.4.19 — «میخوام قابلیت drag کردن هم بیاری که کانفیگ بتونن drag کنن») ═══════════ */

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragRow = null;
        if (e.OriginalSource is not DependencyObject src) return;
        // v0.4.20: never start a drag from the multi-select tick — a click that wiggles a little stays a click.
        if (Ancestor<System.Windows.Controls.Primitives.ToggleButton>(src) is not null) return;
        var item = Ancestor<ListBoxItem>(src);
        if (item?.Content is ProfileRow row)
        {
            _dragRow = row;
            _dragStart = e.GetPosition(List);
        }
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(List);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        try { DragDrop.DoDragDrop(List, new DataObject(typeof(ProfileRow), row), DragDropEffects.Move); }
        catch { HideDropLine(); }
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ProfileRow)) || DataContext is not ProfilesViewModel vm)
        { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowDropLine(InsertIndex(e.GetPosition(List), vm));
    }

    private void List_DragLeave(object sender, DragEventArgs e) => HideDropLine();

    private void List_Drop(object sender, DragEventArgs e)
    {
        HideDropLine();
        if (e.Data.GetData(typeof(ProfileRow)) is not ProfileRow dragged || DataContext is not ProfilesViewModel vm) return;
        var to = InsertIndex(e.GetPosition(List), vm);
        var from = vm.Rows.IndexOf(dragged);
        if (from < 0) return;
        if (to > from) to--;                       // removing the dragged row shifts later indices down
        if (to == from) return;
        vm.Rows.Move(from, to);
        vm.SelectedRow = dragged;
        vm.PersistOrder();                         // survives restarts (sort_order, migration v5)
    }

    /// <summary>The list index a drop at <paramref name="p"/> inserts BEFORE (above the item whose upper half
    /// the cursor is in; after the last item below everything).</summary>
    private int InsertIndex(Point p, ProfilesViewModel vm)
    {
        for (var i = 0; i < vm.Rows.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem it) continue;
            var top = it.TranslatePoint(new Point(0, 0), List);   // works while scrolled too
            if (p.Y < top.Y + it.ActualHeight / 2) return i;
        }
        return vm.Rows.Count;
    }

    private void ShowDropLine(int index)
    {
        if (DataContext is not ProfilesViewModel vm) return;
        double y = 0;
        if (index < vm.Rows.Count && List.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem it)
            y = it.TranslatePoint(new Point(0, 0), List).Y;
        else if (vm.Rows.Count > 0 && List.ItemContainerGenerator.ContainerFromIndex(vm.Rows.Count - 1) is ListBoxItem last)
            y = last.TranslatePoint(new Point(0, 0), List).Y + last.ActualHeight;
        DropLine.Margin = new Thickness(6, Math.Max(-1, y - 1.5), 16, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private void HideDropLine() => DropLine.Visibility = Visibility.Collapsed;

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
