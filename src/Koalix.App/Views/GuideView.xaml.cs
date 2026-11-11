using System.Windows.Controls;

namespace Koalix.App.Views;

/// <summary>v0.4.42 — the آموزش page; its cards slide in one after another like the other pages.</summary>
public partial class GuideView : UserControl
{
    private bool _entered;

    public GuideView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_entered) return;
            _entered = true;
            Controls.Fx.Stagger(Cards, 0.05);
        };
    }
}
