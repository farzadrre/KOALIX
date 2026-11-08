using System.Windows;
using System.Windows.Media.Animation;
using Koalix.App.Controls;

namespace Koalix.App.Views;

/// <summary>v0.4.38 — the About page; the hero logo breathes, bobs and its halo turns while the page is on screen.</summary>
public partial class AboutView : System.Windows.Controls.UserControl
{
    private bool _running;

    public AboutView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Animate(IsVisible);
        Loaded += (_, _) => Animate(IsVisible);
        Unloaded += (_, _) => Animate(false);
    }

    private void Animate(bool on)
    {
        on &= Fx.Enabled;
        if (on == _running) return;
        _running = on;
        if (!on)
        {
            HaloSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            LogoBob.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            Glow.BeginAnimation(OpacityProperty, null);
            return;
        }
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Loop(double from, double to, double s, bool reverse = true) =>
            new(from, to, TimeSpan.FromSeconds(s)) { AutoReverse = reverse, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = reverse ? ease : null };

        var spin = Loop(0, 360, 9, false);
        Timeline.SetDesiredFrameRate(spin, 30);
        HaloSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
        var breathe = Loop(0.92, 1.1, 2.2);
        GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, breathe);
        GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, breathe);
        Glow.BeginAnimation(OpacityProperty, Loop(0.55, 1, 2.2));
        var pop = Loop(1, 1.05, 2.2);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
        LogoBob.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, Loop(2, -3, 3.1));
    }
}
