using System.Text.Json;

namespace Koalix.SystemIntegration;

public enum JournalKind { DnsChange, SystemProxy, KillSwitch, EngineProcess, WireGuardService, GamingTweak }

/// <summary>One reversible change KOALIX made to the system: what it was before, what KOALIX set, how to undo.</summary>
public sealed record JournalEntry(Guid Id, JournalKind Kind, string Target, string? Before, string? Applied, DateTimeOffset At);

/// <summary>
/// Persistent write-ahead journal of system changes. Entries are written BEFORE a change is applied and removed after
/// it is reverted. If the app crashes, pending entries are presented on next start for recovery.
/// </summary>
public sealed class NetworkStateJournal
{
    private readonly string _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public NetworkStateJournal(string file) { _file = file; Directory.CreateDirectory(Path.GetDirectoryName(file)!); }

    public IReadOnlyList<JournalEntry> Pending()
    {
        lock (_gate)
        {
            if (!File.Exists(_file)) return [];
            try { return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(_file)) ?? []; }
            catch (JsonException) { File.Move(_file, _file + ".corrupt", true); return []; }
        }
    }

    public JournalEntry Record(JournalKind kind, string target, string? before, string? applied)
    {
        var e = new JournalEntry(Guid.NewGuid(), kind, target, before, applied, DateTimeOffset.Now);
        Mutate(l => l.Add(e));
        return e;
    }

    public void Complete(Guid id) => Mutate(l => l.RemoveAll(e => e.Id == id));

    private void Mutate(Action<List<JournalEntry>> change)
    {
        lock (_gate)
        {
            var list = Pending().ToList();
            change(list);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, _file, true); // atomic replace
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    public static T? Deserialize<T>(string? json) => json is null ? default : JsonSerializer.Deserialize<T>(json);
}
