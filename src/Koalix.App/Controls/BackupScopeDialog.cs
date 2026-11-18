using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Koalix.App.Localization;
using Koalix.App.Services;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.52 — «موقع گرفتن بکاپ ازم بپرسه چه چیزایی تو فایل ذخیره شه»: the selective-backup picker. Five
/// checkboxes — settings ON by default; profiles, subscriptions, split-tunnel rules and DNS OFF, because they
/// carry the user's servers, personal URLs and app paths. Logs are NOT a choice anywhere: the backup format has
/// no log section at all. Returns null when cancelled.
/// </summary>
public sealed class BackupScopeDialog : Window
{
    private readonly CheckBox _settings = new() { IsChecked = true };
    private readonly CheckBox _profiles = new() { IsChecked = false };
    private readonly CheckBox _subs = new() { IsChecked = false };
    private readonly CheckBox _rules = new() { IsChecked = false };
    private readonly CheckBox _dns = new() { IsChecked = false };
    private bool _ok;

    private BackupScopeDialog()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = Application.Current?.MainWindow is not { IsVisible: true };
        FlowDirection = Loc.I.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13.5;
        Title = Loc.I["Backup.Scope.Title"];

        var titleText = new TextBlock { Text = Loc.I["Backup.Scope.Title"], FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var content = new StackPanel();
        content.Children.Add(titleText);
        content.Children.Add(Row(_settings, "Backup.Scope.Settings", "Backup.Scope.Settings.Sub", "AccentBrush"));
        content.Children.Add(Row(_profiles, "Backup.Scope.Profiles", "Backup.Scope.Profiles.Sub", "SubtleTextBrush"));
        content.Children.Add(Row(_subs, "Backup.Scope.Subs", "Backup.Scope.Subs.Sub", "SubtleTextBrush"));
        content.Children.Add(Row(_rules, "Backup.Scope.Rules", "Backup.Scope.Rules.Sub", "WarningBrush"));
        content.Children.Add(Row(_dns, "Backup.Scope.Dns", "Backup.Scope.Dns.Sub", "SubtleTextBrush"));

        var note = new TextBlock { Text = Loc.I["Backup.Scope.Note"], FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        content.Children.Add(note);

        var go = new Button { Content = Loc.I["Backup.Scope.Continue"], MinWidth = 116, IsDefault = true, Margin = new Thickness(0) };
        go.SetResourceReference(StyleProperty, "AccentButton");
        go.Click += (_, _) => { _ok = true; Close(); };
        var cancel = new Button { Content = Loc.I["Common.Cancel"], MinWidth = 104, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(26, 22, 26, 20),
            Margin = new Thickness(28), MinWidth = 430, MaxWidth = 470, Child = new ScrollViewer
            {
                MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content,
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Opacity = 0.45, Color = Colors.Black },
        };
        card.SetResourceReference(BackgroundProperty, "PopupBrush");
        card.SetResourceReference(BorderBrushProperty, "StrongBorderBrush");
        Content = card;
    }

    private UIElement Row(CheckBox box, string nameKey, string subKey, string nameBrush)
    {
        var name = new TextBlock { Text = Loc.I[nameKey], FontWeight = FontWeights.SemiBold, FontSize = 13 };
        name.SetResourceReference(TextBlock.ForegroundProperty, nameBrush);
        var sub = new TextBlock { Text = Loc.I[subKey], FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(sub);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        box.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(box);
        row.Children.Add(texts);
        var tile = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 8, 11, 8), Background = Brushes.Transparent, Child = row };
        tile.SetResourceReference(BackgroundProperty, "ControlBrush");
        return tile;
    }

    /// <summary>Shows the picker; null = cancelled. Profiles force subscriptions along (profiles reference them).</summary>
    public static Koalix.Infrastructure.BackupOptions? Show()
    {
        var d = new BackupScopeDialog();
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d._ok
            ? new Koalix.Infrastructure.BackupOptions(
                d._settings.IsChecked == true, d._profiles.IsChecked == true,
                d._profiles.IsChecked == true || d._subs.IsChecked == true,
                d._rules.IsChecked == true, d._dns.IsChecked == true)
            : null;
    }
}
