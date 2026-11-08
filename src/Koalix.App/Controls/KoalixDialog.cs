using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Koalix.App.Localization;

namespace Koalix.App.Controls;

public enum DialogKind { Question, Info, Success, Warning, Error }

/// <summary>
/// KOALIX-styled replacement for MessageBox: rounded glass card, coloured icon badge, accent strip, animated entrance.
/// Button order is fixed visually as [primary] [secondary] from the left in both languages.
/// </summary>
public sealed class KoalixDialog : Window
{
    private bool _result;

    private KoalixDialog(string title, string message, DialogKind kind, string primary, string? secondary, bool dangerous)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = Application.Current?.MainWindow is not { IsVisible: true };
        FlowDirection = Loc.I.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13.5;
        Title = title;

        var (brushKey, softKey, glyph) = kind switch
        {
            DialogKind.Error => ("DangerBrush", "DangerSoftBrush", "\uE783"),
            DialogKind.Warning => ("WarningBrush", "WarningSoftBrush", "\uE7BA"),
            DialogKind.Success => ("SuccessBrush", "SuccessSoftBrush", "\uE73E"),
            DialogKind.Info => ("AccentBrush", "AccentSoftBrush", "\uE946"),
            _ => ("AccentBrush", "AccentSoftBrush", "\uE9CE"),
        };

        var badge = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, softKey);
        var icon = new TextBlock { Text = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        badge.Child = icon;

        var titleText = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        titleText.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var bodyScroll = new ScrollViewer { Content = body, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var texts = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        texts.Children.Add(titleText); texts.Children.Add(bodyScroll);
        var head = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge); head.Children.Add(texts);

        var ok = new Button { Content = primary, MinWidth = 104, IsDefault = true, Margin = new Thickness(0) };
        ok.SetResourceReference(StyleProperty, dangerous ? "DangerButton" : "AccentButton");
        ok.Click += (_, _) => Close(true);
        // Visual order [primary][secondary] from the left regardless of language (user request).
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = Loc.I.IsRtl ? HorizontalAlignment.Left : HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(ok);
        if (secondary is not null)
        {
            var no = new Button { Content = secondary, MinWidth = 104, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
            no.Click += (_, _) => Close(false);
            buttons.Children.Add(no);
        }
        else ok.IsCancel = true;

        var strip = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(-26, -24, -26, 20), Opacity = 0.9 };
        strip.SetResourceReference(Border.BackgroundProperty, kind == DialogKind.Error ? "DangerBrush" : kind == DialogKind.Warning ? "WarningBrush" : "AccentGradientBrush");

        var content = new StackPanel();
        content.Children.Add(strip); content.Children.Add(head); content.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(26, 24, 26, 22),
            Margin = new Thickness(28), MinWidth = 380, MaxWidth = 520, Child = content, ClipToBounds = false,
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Opacity = 0.45, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.94, 0.94), Opacity = 0,
        };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        Content = card;

        Loaded += (_, _) =>
        {
            var d = TimeSpan.FromSeconds(0.22);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
            ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            ok.Focus();
        };
    }

    private void Close(bool result) { _result = result; DialogResult = result; }

    public static bool Show(string title, string message, DialogKind kind, string primary, string? secondary = null, bool dangerous = false)
    {
        var d = new KoalixDialog(title, message, kind, primary, secondary, dangerous);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && owner != d) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d._result;
    }
}
