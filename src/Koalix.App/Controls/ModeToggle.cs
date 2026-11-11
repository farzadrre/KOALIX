using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Koalix.App.Localization;
using Koalix.App.Services;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.42 — the title-bar «مبتدی | پیشرفته» segmented switch. Two equal segments over a sliding accent thumb;
/// clicking a segment flips <see cref="UiMode"/> (persisted, applied app-wide at once). RTL-safe: the thumb's
/// TranslateTransform is mirrored together with the layout, so «مبتدی» sits on the start side in both languages.
/// </summary>
public sealed class ModeToggle : Border
{
    private readonly Border _thumb;
    private readonly TranslateTransform _tt = new();
    private readonly UniformGrid _segments;
    private readonly (Border Hit, TextBlock Icon, TextBlock Label)[] _segs = new (Border, TextBlock, TextBlock)[2];
    private bool _hooked;

    public ModeToggle()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(3);
        BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, "ControlBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);

        _thumb = new Border
        {
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _tt,
            IsHitTestVisible = false,
        };
        _thumb.SetResourceReference(Border.BackgroundProperty, "AccentGradientBrush");

        _segments = new UniformGrid { Rows = 1, Columns = 2 };
        for (var i = 0; i < 2; i++)
        {
            var idx = i;
            var icon = new TextBlock
            {
                Text = i == 0 ? "\uE7BE" : "\uE9E9",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var label = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(icon);
            row.Children.Add(label);
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(11, 4, 11, 4), Cursor = Cursors.Hand, Child = row };
            hit.MouseLeftButtonUp += (_, e) => { UiMode.I.IsSimple = idx == 0; e.Handled = true; };
            AutomationProperties_SetName(hit, label);
            _segs[i] = (hit, icon, label);
            _segments.Children.Add(hit);
        }

        var root = new Grid();
        root.Children.Add(_thumb);
        root.Children.Add(_segments);
        Child = root;

        _segments.SizeChanged += (_, _) => Place(animate: false);
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        Relabel();
        Paint();
    }

    private static void AutomationProperties_SetName(Border hit, TextBlock label) =>
        hit.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,
            new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = label });

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on)
        {
            UiMode.I.PropertyChanged += OnModeChanged;
            Loc.I.LanguageChanged += OnLanguage;
            Relabel();
            Paint();
            Place(animate: false);
        }
        else
        {
            UiMode.I.PropertyChanged -= OnModeChanged;
            Loc.I.LanguageChanged -= OnLanguage;
        }
    }

    private void OnLanguage() => Dispatcher.BeginInvoke(() => { Relabel(); Place(animate: false); });

    private void OnModeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UiMode.IsSimple)) return;
        Dispatcher.BeginInvoke(() => { Paint(); Place(animate: true); });
    }

    private void Relabel()
    {
        _segs[0].Label.Text = Loc.I["Mode.Simple"];
        _segs[1].Label.Text = Loc.I["Mode.Advanced"];
        _segs[0].Hit.ToolTip = Loc.I["Mode.SimpleTip"];
        _segs[1].Hit.ToolTip = Loc.I["Mode.AdvancedTip"];
    }

    private void Paint()
    {
        var active = UiMode.I.IsSimple ? 0 : 1;
        for (var i = 0; i < 2; i++)
        {
            var (_, icon, label) = _segs[i];
            if (i == active)
            {
                icon.Foreground = Brushes.White;
                label.Foreground = Brushes.White;
            }
            else
            {
                icon.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
                label.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            }
        }
    }

    private void Place(bool animate)
    {
        var w = _segments.ActualWidth / 2;
        if (w <= 0) return;
        _thumb.Width = w;
        _thumb.Height = _segments.ActualHeight;
        var x = UiMode.I.IsSimple ? 0 : w;
        if (animate && Fx.Enabled)
            _tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            _tt.BeginAnimation(TranslateTransform.XProperty, null);
            _tt.X = x;
        }
    }
}
