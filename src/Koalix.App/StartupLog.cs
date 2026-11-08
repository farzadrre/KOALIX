using System.Text;
using System.Windows;

namespace Koalix.App;

/// <summary>
/// Startup diagnostics that work even before logging/DI exist: every step is appended to
/// %LOCALAPPDATA%\KOALIX\startup.log (and %TEMP%\koalix-startup.log as a fallback), and any fatal error is shown
/// instead of leaving an invisible process behind.
/// </summary>
public static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly string[] Files =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KOALIX", "startup.log"),
        Path.Combine(Path.GetTempPath(), "koalix-startup.log"),
    ];
    public static bool WindowShown { get; set; }

    public static void Trace(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {msg}{Environment.NewLine}";
        lock (Gate)
            foreach (var f in Files)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                    if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
                    File.AppendAllText(f, line, Encoding.UTF8);
                    return;
                }
                catch { /* try next location */ }
    }

    public static void Fatal(string where, Exception ex)
    {
        Trace($"FATAL in {where}: {ex}");
        var text = $"{ex.GetType().Name}: {ex.Message}\n\nDetails: {Files[0]}";
        try { Services.Dialogs.Error("KOALIX could not start", text); }
        catch
        {
            try { MessageBox.Show("KOALIX could not start (" + where + ").\n\n" + text, "KOALIX", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        }
    }
}
