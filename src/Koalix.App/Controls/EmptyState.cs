using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.43 — an empty list's placeholder: an icon on a soft tile that floats gently over a breathing halo, with a
/// line of text under it. Replaces the bare grey sentence that used to sit in empty lists.
/// Bind <see cref="Count"/> to the list's item count and it shows itself only at zero; leave it unset to drive
/// <see cref="UIElement.Visibility"/> yourself. The motion plays briefly each time it becomes visible, and not at
/// all when <see cref="Fx.Enabled"/> is false.
/// </summary>
public sealed class EmptyState : StackPanel
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, e) => ((EmptyState)d)._icon.Text = e.NewValue as string ?? ""));
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, e) => ((EmptyState)d)._text.Text = e.NewValue as string ?? ""));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Item count of the list this stands in for: visible at 0, collapsed otherwise. -1 (default) = not used.</summary>
    public static readonly DependencyProperty CountProperty =
        DependencyProperty.Register(nameof(Count), typeof(int), typeof(EmptyState), new PropertyMetadata(-1, (d, e) =>
        {
            if ((int)e.NewValue >= 0) ((EmptyState)d).Visibility = (int)e.NewValue == 0 ? Visibility.Visible : Visibility.Collapsed;
        }));
    public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }

    private readonly TextBlock _icon = new() { FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _halo;
    private readonly ScaleTransform _haloScale = new(1, 1);
    private readonly TranslateTransform _float = new();
    private bool _running;

    public EmptyState()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        _halo = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(20), Opacity = 0.55,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _haloScale,
        };
        _halo.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");

        _icon.SetResourceReference(StyleProperty, "Icon");
        _icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var tile = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            RenderTransform = _float, Child = _icon,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var art = new Grid { Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Center };
        art.Children.Add(_halo);
        art.Children.Add(tile);

        _text.SetResourceReference(StyleProperty, "Subtle");
        Children.Add(art);
        Children.Add(_text);

        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Two gentle breaths each time the placeholder appears, then it rests. It used to loop forever, but an
    /// endless animation inside a card makes WPF redraw that whole card — soft shadow included — on every frame for
    /// as long as the page is open. Every animation starts and ends on the element's own value, so nothing jumps.</summary>
    private void Start()
    {
        if (_running || !Fx.Enabled || !IsVisible) return;
        _running = true;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Breath(double rest, double peak) => new(rest, peak, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), EasingFunction = ease, FillBehavior = FillBehavior.Stop,
        };
        var lift = Breath(0, -5);
        lift.Completed += (_, _) => _running = false;
        _float.BeginAnimation(TranslateTransform.YProperty, lift);
        _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, Breath(1, 1.16));
        _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, Breath(1, 1.16));
        _halo.BeginAnimation(OpacityProperty, Breath(0.55, 0.22));
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        _float.BeginAnimation(TranslateTransform.YProperty, null);
        _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _halo.BeginAnimation(OpacityProperty, null);
    }
}
