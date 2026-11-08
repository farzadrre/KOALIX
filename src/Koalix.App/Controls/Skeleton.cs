using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.32 — skeleton placeholder: a soft rounded block with a light sweep running across it, shown where
/// content is still loading (ping being measured, a subscription being imported) instead of "…" or a blank.
/// The sweep only runs while the block is visible and on screen; with animations off it stays a static block.
/// </summary>
public sealed class Skeleton : Border
{
    private readonly TranslateTransform _sweep = new();
    private bool _running;

    public Skeleton()
    {
        CornerRadius = new CornerRadius(6);
        Height = 12;
        // theme hook only while loaded: the static event must not keep recycled list items alive
        Loaded += (_, _) => { Services.ThemeService.Changed += OnTheme; Restyle(); };
        Unloaded += (_, _) => { Services.ThemeService.Changed -= OnTheme; Stop(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Restyle(); else Stop(); };
    }

    private void OnTheme() { if (IsLoaded) { Stop(); Restyle(); } }

    private void Restyle()
    {
        var baseColor = (TryFindResource("TextBrush") as SolidColorBrush)?.Color ?? Colors.Gray;
        Color A(byte a) => Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = _sweep,
            GradientStops =
            {
                new GradientStop(A(18), 0.0),
                new GradientStop(A(18), 0.35),
                new GradientStop(A(46), 0.5),
                new GradientStop(A(18), 0.65),
                new GradientStop(A(18), 1.0),
            },
        };
        Background = brush;
        Start();
    }

    private void Start()
    {
        if (_running || !Fx.Enabled || !IsVisible) return;
        _running = true;
        _sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.25))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void Stop()
    {
        _running = false;
        _sweep.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
