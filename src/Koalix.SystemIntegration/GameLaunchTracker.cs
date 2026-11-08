using System.Diagnostics;
using System.IO;

namespace Koalix.SystemIntegration;

/// <summary>v0.4.37 — what changed between two scans: games that just started and games that just exited.</summary>
public sealed record GameLaunchDelta(IReadOnlyList<GameProfile> Started, IReadOnlyList<GameProfile> Stopped)
{
    public bool IsEmpty => Started.Count == 0 && Stopped.Count == 0;
}

/// <summary>
/// v0.4.37 — the pure decision core of per-game auto-activation (user: «پروفایل‌های بازی … فعال شدن خودکار با
/// اجرای بازی»). Fed the set of running executable paths on every scan, it reports start/stop EDGES only, so a
/// game that keeps running never re-triggers. It also owns the "who connected the tunnel" bookkeeping: KOALIX
/// disconnects on game exit ONLY if it connected for a game itself and no other auto game is still running.
/// </summary>
public sealed class GameLaunchTracker
{
    private readonly Dictionary<string, GameProfile> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a connection that KOALIX opened because a game launched is still ours to close.</summary>
    public bool ConnectedForGame { get; private set; }

    public IReadOnlyCollection<GameProfile> Running => _running.Values;

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); } catch { return path.Trim(); }
    }

    /// <summary>Image names (no extension) worth resolving full paths for — keeps a scan to a handful of lookups.</summary>
    public static HashSet<string> CandidateNames(IEnumerable<GameProfile> auto) =>
        auto.Select(g => Path.GetFileNameWithoutExtension(g.ExecutablePath)).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public GameLaunchDelta Update(IEnumerable<GameProfile> autoProfiles, IReadOnlySet<string> runningPaths)
    {
        var running = new HashSet<string>(runningPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var auto = autoProfiles.Where(g => g.AutoActivate && !string.IsNullOrWhiteSpace(g.ExecutablePath))
            .GroupBy(g => Normalize(g.ExecutablePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var started = new List<GameProfile>();
        var stopped = new List<GameProfile>();
        foreach (var (path, g) in auto)
            if (running.Contains(path) && _running.TryAdd(path, g)) started.Add(g);
        // A game that exited — or whose auto toggle was switched off while it ran — counts as stopped.
        foreach (var path in _running.Keys.ToList())
            if (!running.Contains(path) || !auto.ContainsKey(path)) { stopped.Add(_running[path]); _running.Remove(path); }
        return new GameLaunchDelta(started, stopped);
    }

    /// <summary>A game started: connect only when the tunnel is down — and remember that WE opened it.</summary>
    public bool ShouldConnect(bool tunnelUp, bool autoConnectEnabled)
    {
        if (!autoConnectEnabled || tunnelUp) return false;
        ConnectedForGame = true;
        return true;
    }

    /// <summary>A game stopped: disconnect only a tunnel this tracker opened, once the last auto game is gone.</summary>
    public bool ShouldDisconnect(bool tunnelUp)
    {
        if (_running.Count > 0 || !ConnectedForGame) return false;
        ConnectedForGame = false;
        return tunnelUp;
    }

    /// <summary>The user took the wheel (manual disconnect / connect while no game asked): the tunnel is theirs now.</summary>
    public void ReleaseOwnership() => ConnectedForGame = false;

    /// <summary>Full paths of running processes whose image name is in <paramref name="names"/>.</summary>
    public static HashSet<string> ScanRunning(IReadOnlySet<string> names)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return found;
        foreach (var p in Process.GetProcesses())
            using (p)
                try
                {
                    if (!names.Contains(p.ProcessName)) continue;
                    if (ProcessScanner.GetProcessPath(p.Id) is { Length: > 0 } path) found.Add(Normalize(path));
                }
                catch { /* exited mid-scan / access denied */ }
        return found;
    }
}
