using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Koalix.App.Localization;

namespace Koalix.App.Controls;

/// <summary>
/// v0.4.52 — «هر وقت اپدیت جدید میدیم اخرین چیزی که اومده وقتی اولین بار اپدیت میکنیم اپو بیاد رو تصویر و
/// ببینیم چیه»: after every update, the FIRST launch pops this card with the newest version's notes. The notes
/// live in the embedded WhatsNew.json (per version, per language); the seen-marker is app.seenVersion, so each
/// version greets the user exactly once. Fresh installs skip it — the starter tour covers them.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version, List<string> notes)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = Application.Current?.MainWindow is not { IsVisible: true };
        FlowDirection = Loc.I.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13.5;
        Title = Loc.I["WhatsNew.Title"];

        var badge = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        badge.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
        var badgeGlyph = new TextBlock { Text = "\uE789", Style = (Style)Application.Current.Resources["Icon"], FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        badgeGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        badge.Child = badgeGlyph;

        var title = new TextBlock { Text = Loc.I["WhatsNew.Title"], FontSize = 17, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var ver = new TextBlock { Text = "v" + version, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
        ver.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var texts = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        texts.Children.Add(title);
        texts.Children.Add(ver);

        var head = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        head.Children.Add(texts);

        var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var n in notes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            var tile = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2.2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) };
            tile.SetResourceReference(BackgroundProperty, "AccentBrush");
            var t = new TextBlock { Text = n, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Top };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            row.Children.Add(tile);
            row.Children.Add(t);
            list.Children.Add(row);
        }

        var ok = new Button { Content = Loc.I["WhatsNew.Btn"], MinWidth = 128, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        ok.SetResourceReference(StyleProperty, "AccentButton");
        ok.Click += (_, _) => Close();

        var content = new StackPanel();
        content.Children.Add(head);
        content.Children.Add(list);
        content.Children.Add(ok);
        var card = new Border
        {
            CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(26, 24, 26, 22),
            Margin = new Thickness(28), MinWidth = 440, MaxWidth = 500, Child = content,
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Opacity = 0.45, Color = Colors.Black },
        };
        card.SetResourceReference(BackgroundProperty, "PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        Content = card;
    }

    /// <summary>Reads the embedded WhatsNew.json; returns this version's notes in the active language (fa falls
    /// back to en). Null when the version has no entry — nothing pops up.</summary>
    public static List<string>? LoadNotes(string version, string lang)
    {
        try
        {
            using var s = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream("Koalix.App.WhatsNew.json");
            if (s is null) return null;
            using var doc = JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty(version, out var entry)) return null;
            var arr = entry.TryGetProperty(lang, out var _) ? entry.GetProperty(lang)
                    : entry.TryGetProperty("en", out var en) ? en : (JsonElement?)null;
            return arr?.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        }
        catch { return null; }
    }
}
