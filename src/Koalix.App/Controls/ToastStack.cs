using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Koalix.App.Services;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.43 — the in-window toast stack. Up to <see cref="MaxVisible"/> cards sit above each other (newest at the
/// bottom); a new card pushes the older ones up smoothly, an expired one slides down and the gap closes.
/// Each card carries its own timer and a thin countdown line; hovering pauses it, a click dismisses it.
/// With effects off the cards simply appear and disappear.
/// </summary>
public sealed class ToastStack
{
    private const int MaxVisible = 3;
    private readonly StackPanel _host;

    public ToastStack(StackPanel host) => _host = host;

    public void Show(ToastMessage m)
    {
        var live = _host.Children.OfType<ToastCard>().Where(c => !c.Leaving).ToList();
        for (var i = 0; live.Count - i >= MaxVisible; i++) live[i].Dismiss(); // oldest first

        var card = new ToastCard(m, _host);
        _host.Children.Add(card);
        if (!Fx.Enabled) return;

        // the stack is bottom-anchored: adding a card moves every older one up by its height in one layout
        // pass — animate that jump away so they glide instead.
        _host.UpdateLayout();
        var push = card.ActualHeight + card.Margin.Top + card.Margin.Bottom;
        foreach (var older in _host.Children.OfType<ToastCard>())
            if (!ReferenceEquals(older, card)) older.GlideUp(push);
        card.Enter();
    }
}

internal sealed class ToastCard : Border
{
    private static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private readonly Panel _host;
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _life;
    private readonly TranslateTransform _move = new();   // own entrance / exit
    private readonly TranslateTransform _shift = new();  // being pushed up by a newer card
    private readonly ScaleTransform _iconPop = new(1, 1);
    private readonly ScaleTransform _countdown = new(1, 1);

    public bool Leaving { get; private set; }

    public ToastCard(ToastMessage m, Panel host)
    {
        _host = host;
        _life = TimeSpan.FromSeconds(m.Kind == ToastKind.Error ? 8 : 4.5);
        var kindBrush = m.Kind switch
        {
            ToastKind.Success => "SuccessBrush", ToastKind.Warning => "WarningBrush", ToastKind.Error => "DangerBrush", _ => "AccentGradientBrush",
        };

        Margin = new Thickness(0, 8, 0, 0);
        MinWidth = 320; MaxWidth = 560;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        SetResourceReference(BackgroundProperty, "PopupBrush");
        SetResourceReference(BorderBrushProperty, "StrongBorderBrush");
        Effect = new DropShadowEffect
        {
            BlurRadius = 28, ShadowDepth = 6, Opacity = 0.4,
            Color = TryFindResource("ShadowColor") is Color sc ? sc : Application.Current?.TryFindResource("ShadowColor") is Color ac ? ac : Colors.Black,
        };
        RenderTransform = new TransformGroup { Children = { _move, _shift } };

        var icon = new TextBlock { Text = m.Glyph, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(StyleProperty, "Icon");
        var iconBg = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _iconPop, Child = icon,
        };
        iconBg.SetResourceReference(BackgroundProperty, kindBrush);

        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = m.Title, FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(m.Text))
        {
            var body = new TextBlock { Text = m.Text, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            body.SetResourceReference(StyleProperty, "Subtle");
            texts.Children.Add(body);
        }
        Grid.SetColumn(texts, 1);

        var row = new Grid { Margin = new Thickness(16, 12, 16, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(iconBg);
        row.Children.Add(texts);

        // countdown line: drains toward the leading edge while the card's time runs out
        var line = new Border
        {
            Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(16, 0, 16, 6), Opacity = 0.55,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _countdown,
            Visibility = Fx.Enabled ? Visibility.Visible : Visibility.Collapsed,
        };
        line.SetResourceReference(BackgroundProperty, kindBrush);

        var body2 = new StackPanel();
        body2.Children.Add(row);
        body2.Children.Add(line);
        Child = body2;

        _timer = new DispatcherTimer { Interval = _life };
        _timer.Tick += (_, _) => Dismiss();
        MouseLeftButtonUp += (_, _) => Dismiss();
        MouseEnter += (_, _) => { if (!Leaving) { _timer.Stop(); _countdown.BeginAnimation(ScaleTransform.ScaleXProperty, null); } };
        MouseLeave += (_, _) => { if (!Leaving) StartClock(); };
        Unloaded += (_, _) => _timer.Stop();
        StartClock();
    }

    private void StartClock()
    {
        _timer.Stop();
        _timer.Start();
        if (Fx.Enabled) _countdown.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, _life));
    }

    /// <summary>Rise + fade in, and the icon tile lands with a small bounce.</summary>
    public void Enter()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.16)) { FillBehavior = FillBehavior.Stop });
        _move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, 0, TimeSpan.FromSeconds(0.26)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop });
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(0.32)) { BeginTime = TimeSpan.FromSeconds(0.04), EasingFunction = Spring, FillBehavior = FillBehavior.Stop };
        _iconPop.ScaleX = _iconPop.ScaleY = 1;
        _iconPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _iconPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Layout has already moved this card up by <paramref name="by"/>; play that move instead of jumping.</summary>
    public void GlideUp(double by)
    {
        var from = _shift.Y + by; // continue from wherever a previous glide currently is
        _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromSeconds(0.24)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop });
    }

    public void Dismiss()
    {
        if (Leaving) return;
        Leaving = true;
        _timer.Stop();
        IsHitTestVisible = false;
        if (!Fx.Enabled || !IsLoaded) { Remove(); return; }

        _countdown.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.2));
        fade.Completed += (_, _) => Collapse();
        BeginAnimation(OpacityProperty, fade);
        _move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, TimeSpan.FromSeconds(0.22)) { EasingFunction = Out });
    }

    /// <summary>Invisible now — shrink the slot so the cards above settle down smoothly, then leave the tree.</summary>
    private void Collapse()
    {
        var d = TimeSpan.FromSeconds(0.2);
        var shrink = new DoubleAnimation(ActualHeight, 0, d) { EasingFunction = Out };
        shrink.Completed += (_, _) => Remove();
        BeginAnimation(MarginProperty, new ThicknessAnimation(Margin, new Thickness(0), d) { EasingFunction = Out });
        BeginAnimation(HeightProperty, shrink);
    }

    private void Remove()
    {
        _timer.Stop();
        _host.Children.Remove(this);
    }
}
