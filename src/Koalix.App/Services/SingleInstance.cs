using System.Threading;

namespace Koalix.App.Services;

/// <summary>Second launch brings the running window to front instead of silently exiting.</summary>
public static class SingleInstance
{
    private const string EventName = @"Local\KOALIX-App-Show";

    public static void SignalExisting()
    {
        try { if (EventWaitHandle.TryOpenExisting(EventName, out var h)) using (h) h.Set(); } catch { }
    }

    public static void Listen(Action onShow)
    {
        var h = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var t = new Thread(() => { while (h.WaitOne()) onShow(); }) { IsBackground = true, Name = "single-instance" };
        t.Start();
    }
}
