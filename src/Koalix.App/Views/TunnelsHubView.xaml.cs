using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Koalix.App.Controls;
using Koalix.App.Services;

namespace Koalix.App.Views;

public partial class TunnelsHubView : UserControl
{
    public TunnelsHubView()
    {
        InitializeComponent();
        // the panels keep their Dashboard bindings (same singletons, same commands) — the hub only changes WHERE they live
        DataContext = AppHost.Get<ViewModels.DashboardViewModel>();
        Loaded += (_, _) => StartFx();
    }

    /// <summary>Each tunnel has its own motion (v0.4.50: «تو هر قسمت هم گرافیک و انیمیشن و موشن و سبک گرافیکی مختص
    /// خودشو داشته باشه»): WARP's icon breathes blue, the Aether sheen sweeps violet — both gated by the effects switch.</summary>
    private void StartFx()
    {
        if (!Fx.Enabled) return;
        if (FindName("WarpGlow") is Border { Effect: DropShadowEffect glow })
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.18, 0.7, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        if (FindName("AetherShine") is FrameworkElement shine)
        {
            var t = new TranslateTransform();
            shine.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-80, 1120, TimeSpan.FromSeconds(2.8))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }
}
