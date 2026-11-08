using System.Windows;
using Koalix.App.Localization;

namespace Koalix.App.Services;

public static class Dialogs
{
    /// <summary>Confirmation dialog in the current language (KOALIX styled, RTL aware). Returns true for "Yes".</summary>
    public static bool Confirm(string title, string message, bool dangerous = false) =>
        Run(() => Controls.KoalixDialog.Show(title, message, Controls.DialogKind.Question, Loc.I["Common.Yes"], Loc.I["Common.No"], dangerous));
    public static void Info(string title, string message) =>
        Run(() => Controls.KoalixDialog.Show(title, message, Controls.DialogKind.Info, Loc.I["Common.OK"]));
    public static void Warning(string title, string message) =>
        Run(() => Controls.KoalixDialog.Show(title, message, Controls.DialogKind.Warning, Loc.I["Common.OK"]));
    public static void Error(string title, string message) =>
        Run(() => Controls.KoalixDialog.Show(title, message, Controls.DialogKind.Error, Loc.I["Common.OK"]));

    private static bool Run(Func<bool> show)
    {
        var d = Application.Current?.Dispatcher;
        return d is null || d.CheckAccess() ? show() : d.Invoke(show);
    }

    /// <summary>
    /// Themed single-line input (v0.4.12): subscription names, group tags, backup passwords.
    /// password=true masks the text (PasswordBox) — used by the backup/restore dialog.
    /// </summary>
    public static string? Prompt(string title, string message, string initial = "", bool password = false)
    {
        string? result = null;
        Run(() =>
        {
            var w = BuildShell(title);
            var stack = new System.Windows.Controls.StackPanel();
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
                Foreground = (System.Windows.Media.Brush)w.FindResource("TextBrush"),
            });
            System.Windows.Controls.TextBox? box = null;
            System.Windows.Controls.PasswordBox? pb = null;
            if (password)
            {
                pb = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 0, 0, 4), FontSize = 14 };
                pb.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "InputBrush");
                pb.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextBrush");
                pb.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "BorderBrush");
                stack.Children.Add(pb);
            }
            else
            {
                box = new System.Windows.Controls.TextBox { Text = initial, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) };
                box.SelectAll();
                stack.Children.Add(box);
            }
            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok = new System.Windows.Controls.Button { Content = Loc.I["Common.OK"], IsDefault = true, Style = (Style)w.FindResource("AccentButton"), MinWidth = 96 };
            var cancel = new System.Windows.Controls.Button { Content = Loc.I["Common.Cancel"], IsCancel = true, MinWidth = 96 };
            ok.Click += (_, _) => { result = password ? pb!.Password : box!.Text; w.DialogResult = true; w.Close(); };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            w.Content = new System.Windows.Controls.Border { Padding = new Thickness(24), Child = stack };
            w.Activated += (_, _) => (box ?? (System.Windows.Controls.Control?)pb)?.Focus();
            return w.ShowDialog() == true;
        });
        return result;
    }

    /// <summary>Small KOALIX-styled modal window shell (dark, RTL aware, no chrome buttons needed for a prompt).</summary>
    private static Window BuildShell(string title)
    {
        var w = new Window
        {
            Title = title,
            Width = 430, SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            WindowStyle = System.Windows.WindowStyle.ToolWindow,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            FlowDirection = Loc.I.Flow,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"],
            Background = (System.Windows.Media.Brush)Application.Current.Resources["PopupBrush"],
            Topmost = true,
        };
        w.Loaded += (_, _) => { try { w.Owner = Application.Current.MainWindow; } catch { } };
        return w;
    }

    public static string? OpenFile(string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }
    public static string? OpenFolder(string title)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return d.ShowDialog() == true ? d.FolderName : null;
    }
    public static string? SaveFile(string filter, string name)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = name };
        return d.ShowDialog() == true ? d.FileName : null;
    }
}
