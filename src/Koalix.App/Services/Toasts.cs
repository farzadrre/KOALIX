using System.Collections.ObjectModel;

namespace Koalix.App.Services;

public enum ToastKind { Info, Success, Warning, Error }
public sealed record ToastMessage(string Title, string? Text, ToastKind Kind, string Glyph);

/// <summary>One entry of the notification-center history (v0.4.3).</summary>
public sealed record NotifEntry(DateTimeOffset At, ToastMessage Message);

/// <summary>
/// In-window notifications (MainWindow renders them; mirrored to the tray balloon when the window is hidden).
/// Since v0.4.3 every toast is also kept in a bounded, newest-first history so the notification center
/// (title-bar bell) can show what happened while the user was looking elsewhere.
/// </summary>
public static class Toasts
{
    private static readonly ObservableCollection<NotifEntry> HistoryCollection = new();

    /// <summary>Newest first, capped at 60 entries — the notification center's data source.</summary>
    public static ObservableCollection<NotifEntry> History => HistoryCollection;

    /// <summary>Entries added since the user last opened the notification center.</summary>
    public static int Unread { get; private set; }

    /// <summary>Raised on the UI thread whenever the history or the unread count changes.</summary>
    public static event Action? HistoryChanged;

    public static event Action<ToastMessage>? Raised;

    public static void Show(string title, string? text = null, ToastKind kind = ToastKind.Info)
    {
        var glyph = kind switch { ToastKind.Success => "\uE73E", ToastKind.Warning => "\uE7BA", ToastKind.Error => "\uEA39", _ => "\uE946" };
        var d = System.Windows.Application.Current?.Dispatcher;
        var m = new ToastMessage(title, text, kind, glyph);
        if (d is null || d.CheckAccess()) Publish(m); else d.BeginInvoke(() => Publish(m));
    }

    private static void Publish(ToastMessage m)
    {
        HistoryCollection.Insert(0, new NotifEntry(DateTimeOffset.Now, m));
        while (HistoryCollection.Count > 60) HistoryCollection.RemoveAt(HistoryCollection.Count - 1);
        Unread++;
        HistoryChanged?.Invoke();
        Raised?.Invoke(m);
    }

    public static void MarkAllRead()
    {
        if (Unread == 0) return;
        Unread = 0;
        HistoryChanged?.Invoke();
    }

    public static void ClearHistory()
    {
        HistoryCollection.Clear();
        Unread = 0;
        HistoryChanged?.Invoke();
    }
}
