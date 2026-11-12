using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Koalix.App.Controls;
using Koalix.App.Services;
using Koalix.App.ViewModels;
using Koalix.Core;
using Koalix.Infrastructure;

namespace Koalix.App;

/// <summary>
/// v0.4.43 — the window's motion: direction-aware page transitions, the gliding menu rail, the toast stack and
/// the bell. All of it stands still when <see cref="Fx.Enabled"/> is false (pages swap, the rail jumps).
/// </summary>
public partial class MainWindow
{
    private static readonly IEasingFunction MotionOut = FrozenEase(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction MotionIn = FrozenEase(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction MotionSpring = FrozenEase(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 });
    private static T FrozenEase<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private ToastStack? _toasts;

    private void InitMotion()
    {
        _pageFront = PageHost;
        _pageBack = PageHostB;
        _toasts = new ToastStack(ToastHost);

        NavList.SelectionChanged += (_, _) => QueueRail(true);
        NavList.Loaded += (_, _) => QueueRail(false);
        NavHost.SizeChanged += (_, _) => QueueRail(false);
        NavList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => PlaceRail(false)));
        // Beginner/Advanced switch re-filters the menu: the selected row gets a new container at a new position
        NavList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (NavList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) QueueRail(false);
        };

        NotifPopup.Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            try { Motion.EnterRows(NotifList); } catch { }
        });
    }

    /* ───── Page transition: the old page leaves, the new one arrives from its side of the menu ───── */

    private ContentControl _pageFront = null!;
    private ContentControl _pageBack = null!;
    private int _pageIndex = -1;

    private void ShowPage(MainViewModel vm, bool animate)
    {
        try
        {
            var item = vm.Selected;
            var page = item?.Page;
            if (page is null || ReferenceEquals(_pageFront.Content, page)) return;
            var index = vm.Nav.IndexOf(item!);
            var dir = index >= _pageIndex ? 1 : -1; // further down the menu → comes up from below
            _pageIndex = index;

            if (!animate || !Fx.Enabled || !IsLoaded || _pageFront.Content is null)
            {
                Settle(_pageBack, null, front: false);
                Settle(_pageFront, page, front: true);
                return;
            }

            var leaving = _pageFront;
            var entering = _pageBack;
            _pageFront = entering;
            _pageBack = leaving;

            Settle(entering, page, front: true);
            Panel.SetZIndex(entering, 1);
            Panel.SetZIndex(leaving, 0);
            leaving.IsHitTestVisible = false;

            // The page on its way out is drawn once into a bitmap and that bitmap is what fades and moves — otherwise
            // two full pages (every card with its soft shadow) are redrawn on each frame of the swap.
            leaving.CacheMode = new BitmapCache();
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.09));
            // a fast second click may have made this host the front one again — only an idle back host is emptied
            fadeOut.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_pageBack, leaving)) return;
                leaving.Content = null;
                leaving.CacheMode = null;
            };
            leaving.BeginAnimation(OpacityProperty, fadeOut);
            Slide(leaving).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-dir * 8, TimeSpan.FromSeconds(0.09)) { EasingFunction = MotionIn });

            // short on purpose: the page must feel like it is simply there, the motion is only a hint of direction
            entering.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15)) { FillBehavior = FillBehavior.Stop });
            Slide(entering).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(dir * 14, 0, TimeSpan.FromSeconds(0.22)) { EasingFunction = MotionOut, FillBehavior = FillBehavior.Stop });
        }
        catch (Exception ex)
        {
            StartupLog.Trace("page transition: " + ex.Message);
            try { Settle(_pageBack, null, front: false); Settle(_pageFront, vm.Selected?.Page, front: true); } catch { }
        }
    }

    private static TranslateTransform Slide(ContentControl host)
    {
        if (host.RenderTransform is not TranslateTransform t || t.IsFrozen) host.RenderTransform = t = new TranslateTransform();
        return t;
    }

    /// <summary>Drops every running animation on a host and puts it in its resting state with the given content.</summary>
    private static void Settle(ContentControl host, object? content, bool front)
    {
        host.BeginAnimation(OpacityProperty, null);
        var t = Slide(host);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        t.Y = 0;
        host.Opacity = 1;
        host.CacheMode = null; // the leave-bitmap is only for the fade-out; a live page is drawn directly (crisp text)
        host.IsHitTestVisible = front;
        host.Content = content;
    }

    /* ───── Menu rail: one accent bar that glides to the selected section ───── */

    private bool _railPlaced;

    private void QueueRail(bool animate) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceRail(animate));

    private void PlaceRail(bool animate)
    {
        try
        {
            var row = NavList.SelectedItem is null ? null : NavList.ItemContainerGenerator.ContainerFromItem(NavList.SelectedItem) as FrameworkElement;
            if (row is null || !row.IsVisible || row.ActualHeight < 1 || !row.IsDescendantOf(NavHost))
            {
                NavRail.Opacity = 0;
                _railPlaced = false;
                return;
            }
            // same spot the per-item pill used to sit: 8 px in from the row's leading edge, vertically centred
            var p = row.TransformToAncestor(NavHost).Transform(new Point(0, row.ActualHeight / 2));
            var y = p.Y - NavRail.Height / 2;
            NavRailMove.X = p.X + 8;

            var from = NavRailMove.Y; // the value on screen right now, mid-glide included
            NavRailMove.Y = y;        // resting value — the glide below is FillBehavior.Stop and lands on it
            if (animate && _railPlaced && Fx.Enabled && Math.Abs(from - y) > 0.5)
            {
                var d = TimeSpan.FromSeconds(0.26);
                NavRailMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, y, d) { EasingFunction = MotionSpring, FillBehavior = FillBehavior.Stop });
                // the bar stretches while it travels and snaps back to size as it lands
                var stretch = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.09)), MotionOut));
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(d), MotionOut));
                NavRailStretch.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
            }
            else if (!animate || !_railPlaced || !Fx.Enabled)
            {
                NavRailMove.BeginAnimation(TranslateTransform.YProperty, null);
                NavRailStretch.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            if (!_railPlaced && Fx.Enabled)
                NavRail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)) { FillBehavior = FillBehavior.Stop });
            NavRail.Opacity = 1;
            _railPlaced = true;
        }
        catch { /* layout not ready — the next size/selection change places it */ }
    }

    /* ───── Toasts ───── */

    private void ShowToast(ToastMessage m)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            AppHost.Get<TrayService>().Notify(m.Title, m.Text ?? "", m.Kind);
            return;
        }
        try { _toasts?.Show(m); } catch (Exception ex) { StartupLog.Trace("toast: " + ex.Message); }
    }

    /* ───── Bell: rings on a new notification, the unread dot pops in ───── */

    private int _lastUnread;

    private void UpdateNotifBadge()
    {
        var unread = Toasts.Unread;
        NotifBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        var isNew = unread > _lastUnread;
        _lastUnread = unread;
        if (!isNew || !Fx.Enabled || !IsVisible || NotifPopup.IsOpen) return;
        try
        {
            // swing from the top of the bell, like one hanging from its hook
            if (BellButton.RenderTransform is not RotateTransform swing || swing.IsFrozen) BellButton.RenderTransform = swing = new RotateTransform();
            BellButton.RenderTransformOrigin = new Point(0.5, 0.2);
            var ring = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            double[] angles = [0, 16, -13, 9, -6, 3, 0];
            for (var i = 0; i < angles.Length; i++)
                ring.KeyFrames.Add(new EasingDoubleKeyFrame(angles[i], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.11 * i)), MotionOut));
            swing.BeginAnimation(RotateTransform.AngleProperty, ring);

            if (NotifBadge.RenderTransform is not ScaleTransform dot || dot.IsFrozen) NotifBadge.RenderTransform = dot = new ScaleTransform(1, 1);
            NotifBadge.RenderTransformOrigin = new Point(0.5, 0.5);
            var pop = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.38)) { EasingFunction = MotionSpring, FillBehavior = FillBehavior.Stop };
            dot.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            dot.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        catch { }
    }
}
