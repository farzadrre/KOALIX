using Koalix.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Koalix.SystemIntegration;

public sealed record RunningApp(int Pid, string Name, string? ExecutablePath, DateTime? StartTime);
public sealed record InstalledApp(string DisplayName, string? InstallLocation, string? DisplayIcon, string? Publisher);

/// <summary>Enumerates running processes (full image path via QueryFullProcessImageName) and installed applications.</summary>
public static class ProcessScanner
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    public static string? GetProcessPath(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    public static IReadOnlyList<RunningApp> GetRunning()
    {
        var list = new List<RunningApp>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                DateTime? start = null; try { start = p.StartTime; } catch { }
                list.Add(new RunningApp(p.Id, p.ProcessName, GetProcessPath(p.Id), start));
            }
        }
        return list;
    }

    /// <summary>Returns processes matching a rule path that started before the tunnel — they need a restart so existing sockets re-route.</summary>
    /// <summary>Running processes matched by the rules (exact paths and folder rules) that started before the tunnel.</summary>
    public static IReadOnlyList<RunningApp> NeedsRestart(IEnumerable<AppRule> rules, DateTime tunnelStartedLocal)
    {
        var list = rules.ToList();
        return GetRunning().Where(a => a.ExecutablePath is not null && a.StartTime < tunnelStartedLocal && list.Any(r => r.Matches(a.ExecutablePath))).ToList();
    }

    public static IReadOnlyList<RunningApp> NeedsRestart(IEnumerable<string> exePaths, DateTime tunnelStartedLocal)
    {
        var set = new HashSet<string>(exePaths, StringComparer.OrdinalIgnoreCase);
        return GetRunning().Where(a => a.ExecutablePath is not null && set.Contains(a.ExecutablePath) && a.StartTime < tunnelStartedLocal).ToList();
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<InstalledApp> GetInstalled()
    {
        var result = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        string[] keys = [@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"];
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var k in keys)
            {
                using var root = hive.OpenSubKey(k);
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var s = root.OpenSubKey(sub);
                    if (s?.GetValue("DisplayName") is not string name || s.GetValue("SystemComponent") is 1) continue;
                    result.TryAdd(name, new InstalledApp(name, s.GetValue("InstallLocation") as string,
                        (s.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"'), s.GetValue("Publisher") as string));
                }
            }
        return result.Values.OrderBy(a => a.DisplayName).ToList();
    }
}
