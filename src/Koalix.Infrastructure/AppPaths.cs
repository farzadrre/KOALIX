namespace Koalix.Infrastructure;

/// <summary>All data lives under %LOCALAPPDATA%\KOALIX (portable mode: next to the exe when "portable.flag" exists).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        var exeDir = AppContext.BaseDirectory;
        Root = root ?? (File.Exists(Path.Combine(exeDir, "portable.flag")) ? Path.Combine(exeDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KOALIX"));
        if (root is null) MigrateLegacy(Root);
        foreach (var d in new[] { Root, LogsDir, BackupsDir }) Directory.CreateDirectory(d);
    }
    public string Root { get; }

    /// <summary>One-time migration from the pre-rename data folder (%LOCALAPPDATA%\NEXUS, nexus.db).</summary>
    private static void MigrateLegacy(string root)
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(root)!, "NEXUS");
            if (Directory.Exists(root) || !Directory.Exists(legacy) || root.EndsWith("data", StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(root);
            foreach (var f in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(legacy, f);
                if (rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || rel.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.Equals("nexus.db", StringComparison.OrdinalIgnoreCase)) rel = "koalix.db";
                var dst = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, overwrite: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public string Database => Path.Combine(Root, "koalix.db");
    public string LogsDir => Path.Combine(Root, "logs");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string Journal => Path.Combine(Root, "network-journal.json");
    public string CrashMarker => Path.Combine(Root, "session.lock");
}
