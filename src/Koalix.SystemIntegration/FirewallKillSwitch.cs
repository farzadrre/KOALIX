using Koalix.Core;

namespace Koalix.SystemIntegration;

/// <summary>
/// Kill switch via Windows Defender Firewall outbound block rules (prefix KOALIX-KillSwitch-). Per-app mode blocks only
/// tunneled applications. A true system-wide block-all (allowing only engine traffic) requires WFP and is PENDING;
/// blockAll therefore behaves like per-app mode. Rules are removed on deactivate and on crash recovery.
/// </summary>
public sealed class FirewallKillSwitch(IProcessRunner runner, NetworkStateJournal? journal = null) : IKillSwitch
{
    public const string Prefix = "KOALIX-KillSwitch-";
    /// <summary>Rules created by versions released under the old name (cleaned up during recovery).</summary>
    public const string LegacyPrefix = "NEXUS-KillSwitch-";
    private readonly List<string> _rules = [];
    private readonly Dictionary<string, Guid> _entries = [];
    public bool IsActive => _rules.Count > 0;

    public async Task ActivateAsync(IReadOnlyList<AppRule> tunneledApps, bool blockAll, CancellationToken ct)
    {
        var netsh = ProcessRunner.System32("netsh.exe");
        // Folder rules are expanded to the executables currently inside the folder (bounded) — netsh rules are per-program.
        var targets = tunneledApps.Where(PathValidator.IsValidRulePath)
            .SelectMany(a => a.IsFolderRule ? ExpandFolder(a.ExecutablePath).Select(p => (a.Id, Path: p)) : [(a.Id, Path: a.ExecutablePath)])
            .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var (id, path) in targets)
        {
            var name = Prefix + id + "-" + Path.GetFileName(path);
            var r = await runner.RunAsync(netsh, ["advfirewall", "firewall", "add", "rule", $"name={name}", "dir=out", "action=block", $"program={path}", "enable=yes"], true, null, ct);
            if (r.Success) { _rules.Add(name); if (journal is not null) _entries[name] = journal.Record(JournalKind.KillSwitch, name, null, "block").Id; }
        }
    }

    public async Task DeactivateAsync(CancellationToken ct)
    {
        var netsh = ProcessRunner.System32("netsh.exe");
        foreach (var name in _rules.ToList())
        {
            await runner.RunAsync(netsh, ["advfirewall", "firewall", "delete", "rule", $"name={name}"], true, null, ct);
            _rules.Remove(name);
            if (_entries.Remove(name, out var id)) journal?.Complete(id);
        }
    }

    /// <summary>Crash recovery: remove all KOALIX kill-switch rules left behind.</summary>
    public async Task RemoveAllLeftoversAsync(IReadOnlyList<string> knownRuleNames, CancellationToken ct)
    {
        var netsh = ProcessRunner.System32("netsh.exe");
        foreach (var n in knownRuleNames.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) || n.StartsWith(LegacyPrefix, StringComparison.Ordinal)))
            await runner.RunAsync(netsh, ["advfirewall", "firewall", "delete", "rule", $"name={n}"], true, null, ct);
    }

    internal static IEnumerable<string> ExpandFolder(string folder, int max = 256)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
                .Where(PathValidator.IsValidExecutablePath).Take(max).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
