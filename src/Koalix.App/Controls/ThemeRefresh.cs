using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Koalix.App.Services;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.42 — fixes «موقع عوض کردن تم یک بخش از اپ همچنان روی دارک مود میمونه و تا موسم و روش نیارم لایت نمیشه».
/// <para>Most of the UI follows the theme by itself (DynamicResource), but a few things read the colours only when
/// they draw: the server map / globe, the speed and ping charts, the live backdrop, the section emblems, and the
/// hover-glow on cards (its composed brush keeps a snapshot of the card colour). Those used to repaint only when the
/// mouse touched them. After every real restyle this walks every open window once and forces them to repaint.</para>
/// </summary>
public static class ThemeRefresh
{
    private static bool _installed, _queued;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ThemeService.Restyled += Queue;
    }

    private static void Queue()
    {
        if (_queued || Application.Current is not { } app) return;
        _queued = true;
        // after the resource swap has propagated (Loaded runs after layout/render of the new values)
        app.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _queued = false;
            RefreshAll();
        });
    }

    public static void RefreshAll()
    {
        if (Application.Current is not { } app) return;
        foreach (Window w in app.Windows)
        {
            try { Walk(w); } catch { /* cosmetic only */ }
        }
    }

    private static void Walk(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            switch (d)
            {
                case SectionEmblem se:
                    se.Restyle();
                    break;
                case Border b:
                    Fx.ResetGlow(b);
                    break;
            }
            // custom-drawn controls (OnRender) re-read their colours on the next render pass
            if (d is UIElement ui) ui.InvalidateVisual();
            if (d is not Visual and not System.Windows.Media.Media3D.Visual3D) continue;
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }
}
