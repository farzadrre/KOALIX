using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.43 — the app-wide motion layer that sits next to <see cref="Fx"/>:
/// <list type="bullet">
/// <item><c>Motion.Entrance</c> — the children of a page's root panel rise in one after another (every page, not just the dashboard).</item>
/// <item><c>Motion.PopIn</c> — a <see cref="Popup"/> (dropdown, notification center, palette) opens with a small scale + slide.</item>
/// <item><see cref="Install"/> — right-click menus and tooltips get the same soft opening, app-wide.</item>
/// <item><c>Motion.TabSlide</c> — one accent pill glides between the tabs of a TabControl.</item>
/// </list>
/// Everything is a no-op while <see cref="Fx.Enabled"/> is false (effects switch off / safe mode); the entrance
/// animations use <see cref="FillBehavior.Stop"/>, so an element always ends on its own, un-animated values.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /* ───── Entrance: staggered rise of a panel's children ───── */

    public static readonly DependencyProperty EntranceProperty =
        DependencyProperty.RegisterAttached("Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceChanged));
    public static bool GetEntrance(DependencyObject d) => (bool)d.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject d, bool v) => d.SetValue(EntranceProperty, v);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Panel p && (bool)e.NewValue) p.Loaded += EntranceLoaded;
    }

    private static void EntranceLoaded(object sender, RoutedEventArgs e)
    {
        var p = (Panel)sender;
        p.Loaded -= EntranceLoaded; // once per view instance — a re-attach must not replay it
        try { Enter(Targets(p)); } catch { /* motion must never break a page */ }
    }

    /// <summary>The elements that take part: the panel's children, with a docked header stack opened up
    /// (title, subtitle and the cards under it each get their own step).</summary>
    private static IEnumerable<FrameworkElement> Targets(Panel p)
    {
        foreach (UIElement c in p.Children)
        {
            if (p is DockPanel && c is StackPanel { Orientation: Orientation.Vertical } header)
            {
                foreach (UIElement h in header.Children)
                    if (h is FrameworkElement hf) yield return hf;
            }
            else if (c is FrameworkElement fe) yield return fe;
        }
    }

    /// <summary>Fades and lifts each element in turn. <paramref name="cap"/> bounds the delay so a long page
    /// never keeps its lower cards waiting. Kept short on purpose (the last card has landed within ~0.45 s):
    /// every frame of it redraws the cards with their soft shadows, and a page that takes a second to settle
    /// reads as a slow app.</summary>
    public static void Enter(IEnumerable<FrameworkElement> items, double step = 0.03, double fromY = 12, int cap = 6)
    {
        if (!Fx.Enabled) return;
        var i = 0;
        foreach (var fe in items)
        {
            if (fe.Visibility != Visibility.Visible) continue;
            var delay = TimeSpan.FromSeconds(step * Math.Min(i++, cap));
            var t = new TranslateTransform();
            // keep whatever transform is already there (Fx.HoverScale's ScaleTransform, a rotate …) — wrap, don't replace
            fe.RenderTransform = fe.RenderTransform switch
            {
                TransformGroup g when !g.IsFrozen => Prepend(g, t),
                Transform existing when !existing.IsFrozen => new TransformGroup { Children = { t, existing } },
                _ => new TransformGroup { Children = { t } },
            };

            var to = fe.Opacity; // the element's own opacity is the target, and what it returns to
            if (to > 0)
            {
                var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                if (delay > TimeSpan.Zero) fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromSeconds(0.18))));
                fe.BeginAnimation(UIElement.OpacityProperty, fade);
            }

            var rise = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (delay > TimeSpan.Zero) rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromY, KeyTime.FromTimeSpan(delay)));
            rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + TimeSpan.FromSeconds(0.26)), Out));
            t.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    private static TransformGroup Prepend(TransformGroup g, Transform t)
    {
        g.Children.Insert(0, t);
        return g;
    }

    /// <summary>Staggers the first realized rows of a list (the notification center when it opens).</summary>
    public static void EnterRows(ItemsControl list, int max = 8)
    {
        if (!Fx.Enabled) return;
        var rows = new List<FrameworkElement>();
        for (var i = 0; i < list.Items.Count && rows.Count < max; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row) rows.Add(row);
        Enter(rows, 0.025, 8, max);
    }

    /* ───── Popups: dropdowns, menus, tooltips open with a small scale + slide ───── */

    public static readonly DependencyProperty PopInProperty =
        DependencyProperty.RegisterAttached("PopIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPopInChanged));
    public static bool GetPopIn(DependencyObject d) => (bool)d.GetValue(PopInProperty);
    public static void SetPopIn(DependencyObject d, bool v) => d.SetValue(PopInProperty, v);

    private static void OnPopInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup p) return;
        if ((bool)e.NewValue) p.Opened += PopupOpened; else p.Opened -= PopupOpened;
    }

    private static void PopupOpened(object? sender, EventArgs e)
    {
        try { if (sender is Popup { Child: FrameworkElement child }) Pop(child, new Point(0.5, 0), 0.96, -6, 0.2); } catch { }
    }

    private static bool _installed;

    /// <summary>App-wide: every right-click menu and tooltip opens softly. Called once at startup.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnMenuOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnTipOpened));
    }

    private static void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        try { if (sender is ContextMenu m && TemplateRoot(m) is { } root) Pop(root, new Point(0, 0), 0.94, -4, 0.18); } catch { }
    }

    private static void OnTipOpened(object sender, RoutedEventArgs e)
    {
        try { if (sender is ToolTip t && TemplateRoot(t) is { } root) Pop(root, new Point(0.5, 1), 0.96, 4, 0.14); } catch { }
    }

    private static FrameworkElement? TemplateRoot(Visual v) =>
        VisualTreeHelper.GetChildrenCount(v) > 0 ? VisualTreeHelper.GetChild(v, 0) as FrameworkElement : null;

    /// <summary>Scale-from-<paramref name="fromScale"/> + slide + fade. Never overshoots: a popup window is sized
    /// to its content, so growing past 1.0 would be clipped.</summary>
    private static void Pop(FrameworkElement el, Point origin, double fromScale, double fromY, double seconds)
    {
        if (!Fx.Enabled) return;
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        el.RenderTransformOrigin = origin;
        el.RenderTransform = new TransformGroup { Children = { scale, move } };
        var d = TimeSpan.FromSeconds(seconds);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = Out });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, d) { EasingFunction = Out });
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds * 0.7)) { FillBehavior = FillBehavior.Stop });
    }

    /* ───── Tabs: one accent pill that glides to the selected tab ───── */

    public static readonly DependencyProperty TabSlideProperty =
        DependencyProperty.RegisterAttached("TabSlide", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTabSlideChanged));
    public static bool GetTabSlide(DependencyObject d) => (bool)d.GetValue(TabSlideProperty);
    public static void SetTabSlide(DependencyObject d, bool v) => d.SetValue(TabSlideProperty, v);

    /// <summary>True once the pill is in place under the selected tab — the TabItem template then drops its own
    /// selected background, so the pill is the only accent. While false the classic per-tab background shows.
    /// Inherited, so each TabItem sees its TabControl's value directly and the template can use a plain
    /// property trigger (a binding to an attached-property path does not resolve inside a template trigger).</summary>
    public static readonly DependencyProperty TabSlideActiveProperty =
        DependencyProperty.RegisterAttached("TabSlideActive", typeof(bool), typeof(Motion),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetTabSlideActive(DependencyObject d) => (bool)d.GetValue(TabSlideActiveProperty);
    public static void SetTabSlideActive(DependencyObject d, bool v) => d.SetValue(TabSlideActiveProperty, v);

    private static void OnTabSlideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabControl tc || !(bool)e.NewValue) return;
        var hooked = false;
        tc.Loaded += (_, _) =>
        {
            // header sizes change with the language; follow the strip itself, not just the control
            if (!hooked && Slider(tc)?.Parent is FrameworkElement strip)
            {
                hooked = true;
                strip.SizeChanged += (_, _) => PlaceTab(tc, false);
            }
            PlaceTab(tc, false);
        };
        tc.SelectionChanged += (_, ev) =>
        {
            // SelectionChanged bubbles up from the grids and lists inside the tabs — only our own counts
            if (!ReferenceEquals(ev.OriginalSource, tc)) return;
            PlaceTab(tc, true);
            try { if (tc.Template?.FindName("PART_SelectedContentHost", tc) is FrameworkElement body) Fx.SlideIn(body, 6, 0.18); } catch { }
        };
    }

    private static Border? Slider(TabControl tc) => tc.Template?.FindName("PART_TabSlider", tc) as Border;

    private static void PlaceTab(TabControl tc, bool animate)
    {
        try
        {
            if (Slider(tc) is not { Parent: FrameworkElement strip } slider) return;
            var tab = tc.SelectedItem as TabItem ?? (tc.SelectedItem is null ? null : tc.ItemContainerGenerator.ContainerFromItem(tc.SelectedItem) as TabItem);
            if (tab is null || !tab.IsVisible || tab.ActualWidth < 1 || !tab.IsDescendantOf(strip))
            {
                slider.Opacity = 0;
                SetTabSlideActive(tc, false);
                return;
            }
            var p = tab.TransformToAncestor(strip).Transform(new Point(0, 0));
            if (slider.RenderTransform is not TranslateTransform move || move.IsFrozen) slider.RenderTransform = move = new TranslateTransform();
            var placed = GetTabSlideActive(tc);
            slider.Height = tab.ActualHeight;
            if (animate && placed && Fx.Enabled)
            {
                // explicit From (the current, possibly mid-flight value) so a quick second click continues smoothly
                var d = TimeSpan.FromSeconds(0.2);
                var fromX = move.X; var fromY = move.Y; var fromW = slider.Width;
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, p.X, d) { EasingFunction = Out });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, p.Y, d) { EasingFunction = Out });
                slider.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(double.IsNaN(fromW) ? tab.ActualWidth : fromW, tab.ActualWidth, d) { EasingFunction = Out });
            }
            else
            {
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                slider.BeginAnimation(FrameworkElement.WidthProperty, null);
                move.X = p.X; move.Y = p.Y;
                slider.Width = tab.ActualWidth;
            }
            slider.Opacity = 1;
            SetTabSlideActive(tc, true);
        }
        catch { /* layout not ready — the per-tab background stays as the fallback */ }
    }
}
