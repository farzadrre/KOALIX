using System.Collections.Concurrent;
using Koalix.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Koalix.Infrastructure;

public sealed record LogLine(DateTimeOffset At, LogEventLevel Level, string Category, string Message);

/// <summary>In-memory ring buffer for the Logs page (bounded → constant memory).</summary>
public sealed class RingSink(int capacity = 5000) : ILogEventSink
{
    private readonly ConcurrentQueue<LogLine> _q = new();
    public event Action<LogLine>? Emitted;
    public void Emit(LogEvent e)
    {
        var cat = e.Properties.TryGetValue("Category", out var c) ? c.ToString().Trim('"') : "Application";
        var line = new LogLine(e.Timestamp, e.Level, cat, Redactor.Redact(e.RenderMessage()));
        _q.Enqueue(line);
        while (_q.Count > capacity && _q.TryDequeue(out _)) { }
        Emitted?.Invoke(line);
    }
    public IReadOnlyList<LogLine> Snapshot() => _q.ToArray();
}

public static class KoalixLog
{
    /// <summary>
    /// Log files are HARD-CAPPED (v0.4.0): at most 6 files × 5 MB = 30 MB on disk, and anything older than
    /// 3 days is pruned at startup. A busy sing-box session logs every connection at Information level, which
    /// previously grew a single night's file past 10 MB; the in-app Logs page keeps its own bounded ring buffer.
    /// </summary>
    public static Logger Create(AppPaths paths, RingSink ring, bool verbose)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-3);
            foreach (var f in Directory.GetFiles(paths.LogsDir, "koalix-*.log"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { /* pruning is best-effort */ }
        return new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(Path.Combine(paths.LogsDir, "koalix-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 6,
                fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(ring)
            .CreateLogger();
    }

    /// <summary>All app logging goes through here so secrets are redacted before reaching any sink.</summary>
    public static void Write(ILogger log, LogCategory cat, LogEventLevel level, string message) =>
        log.ForContext("Category", cat.ToString()).Write(level, "{Msg:l}", Redactor.Redact(message));
}
