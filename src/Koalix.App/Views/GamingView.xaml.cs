using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Koalix.App.Views;

public partial class GamingView : System.Windows.Controls.UserControl
{
    private bool _entered;
    public GamingView()
    {
        InitializeComponent();
        Boosts.ItemContainerGenerator.StatusChanged += (_, _) => TryStagger();
        Loaded += (_, _) => TryStagger();
    }

    /// <summary>The 12 boost cards fade/slide in one after another (45 ms apart) every time the page opens.</summary>
    private void TryStagger()
    {
        if (_entered || Boosts.Items.Count == 0) return;
        if (Boosts.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement) return;
        _entered = true;
        for (var i = 0; i < Boosts.Items.Count; i++)
            if (Boosts.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement el)
            {
                var t = new TranslateTransform(0, 16);
                el.RenderTransform = t;
                var begin = TimeSpan.FromMilliseconds(45 * i);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                el.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = begin });
                t.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
            }
    }

    /// <summary>v0.4.23 (suggestion #7): opens the fullscreen minimal focus surface (status + live ping + disconnect).</summary>
    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow w) w.ShowFocus();
    }
}
