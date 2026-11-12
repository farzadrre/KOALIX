using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Koalix.App.ViewModels;

namespace Koalix.App.Views;

public partial class GamingView : System.Windows.Controls.UserControl
{
    /// <summary>How long the header gamepad keeps playing after the page opens (two cycles of its loop) before it
    /// rests — a loop that never stops would keep the page redrawing for nothing.</summary>
    private static readonly TimeSpan HeroPlay = TimeSpan.FromSeconds(4.8);

    private readonly DispatcherTimer _heroRest = new() { Interval = HeroPlay };
    private readonly List<BoostItem> _watched = [];
    private readonly List<Border> _segments = [];
    private bool _entered;
    private int _shown = -1;

    public GamingView()
    {
        InitializeComponent();
        Boosts.ItemContainerGenerator.StatusChanged += (_, _) => TryStagger();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => { if (IsLoaded) Watch(); };

        // The header gamepad: plays when the page opens, rests, and plays again while the strip is hovered.
        _heroRest.Tick += (_, _) => { _heroRest.Stop(); if (!Hero.IsMouseOver) HeroGlyph.IsActive = false; };
        Hero.MouseEnter += (_, _) => { _heroRest.Stop(); HeroGlyph.IsActive = true; };
        Hero.MouseLeave += (_, _) => HeroGlyph.IsActive = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Watch();
        TryStagger();
        HeroGlyph.IsActive = true;
        _heroRest.Start();
        if (!Controls.Fx.Enabled) return;
        // the header tile lands with a small spring (FillBehavior.Stop: it ends on its own, un-animated scale)
        var pop = new ScaleTransform(1, 1);
        HeroTile.RenderTransform = pop;
        var land = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(460))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            FillBehavior = FillBehavior.Stop,
        };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, land);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, land);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _heroRest.Stop();
        HeroGlyph.IsActive = false;
        Unwatch();
    }

    /// <summary>The boost cards rise in one after another when the page opens (the shared entrance motion, a little
    /// slower per card so the twelve read as a wave).</summary>
    private void TryStagger()
    {
        if (_entered || Boosts.Items.Count == 0) return;
        if (Boosts.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement) return;
        _entered = true;
        var cards = new List<FrameworkElement>();
        for (var i = 0; i < Boosts.Items.Count; i++)
            if (Boosts.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement el) cards.Add(el);
        try { Controls.Motion.Enter(cards, step: 0.035, fromY: 16, cap: 12); } catch { /* motion must never break the page */ }
    }

    /* ───── header counter + meter: how many boosts are on right now ───── */

    /// <summary>Follows every boost card's switch. The cards live in the view model (which outlives this view), so
    /// the handlers are removed again when the page is left.</summary>
    private void Watch()
    {
        Unwatch();
        foreach (var item in Boosts.Items)
            if (item is BoostItem b) { b.PropertyChanged += OnBoostChanged; _watched.Add(b); }
        BuildMeter(_watched.Count);
        _shown = -1;
        ShowActive();
    }

    private void Unwatch()
    {
        foreach (var b in _watched) b.PropertyChanged -= OnBoostChanged;
        _watched.Clear();
    }

    private void OnBoostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BoostItem.IsOn)) return;
        if (Dispatcher.CheckAccess()) ShowActive();
        else Dispatcher.BeginInvoke(new Action(ShowActive));
    }

    private void BuildMeter(int total)
    {
        ActiveTotal.Text = "/" + total;
        if (_segments.Count == total) return;
        Meter.Children.Clear();
        _segments.Clear();
        for (var i = 0; i < total; i++)
        {
            var track = new Border { CornerRadius = new CornerRadius(2) };
            track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            var fill = new Border { CornerRadius = new CornerRadius(2), Opacity = 0 };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Meter.Children.Add(new Grid { Width = 11, Height = 6, Margin = new Thickness(0, 0, i == total - 1 ? 0 : 3, 0), Children = { track, fill } });
            _segments.Add(fill);
        }
    }

    /// <summary>Writes the count and lights that many segments; the segments that change fade one after another,
    /// so "Apply all" fills the meter as a sweep and "Revert all" drains it.</summary>
    private void ShowActive()
    {
        var on = _watched.Count(b => b.IsOn);
        if (on == _shown) return;
        var first = _shown < 0;
        var from = Math.Max(0, Math.Min(_shown, on));
        _shown = on;
        ActiveCount.Text = on.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var animate = Controls.Fx.Enabled && IsLoaded;
        for (var i = 0; i < _segments.Count; i++)
        {
            var to = i < on ? 1.0 : 0.0;
            if (!animate) { _segments[i].BeginAnimation(OpacityProperty, null); _segments[i].Opacity = to; continue; }
            var delay = TimeSpan.FromMilliseconds(40 * Math.Max(0, i - from) + (first ? 220 : 0));
            _segments[i].BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { BeginTime = delay });
        }
        if (!animate || first) return;
        // the number ticks: a quick pop that settles back on its own scale
        var tick = new ScaleTransform(1, 1);
        ActiveCount.RenderTransform = tick;
        var pop = new DoubleAnimation(1.28, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        tick.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        tick.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow w) w.ShowFocus();
    }
}
