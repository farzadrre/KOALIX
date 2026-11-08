using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Koalix.Core;

namespace Koalix.SystemIntegration;

[Flags]
public enum GamingTweak
{
    None = 0,
    HighPerformancePower = 1,
    GameMode = 2,
    MultimediaNetworkThrottling = 4,
    HighProcessPriority = 8,
    /// <summary>Nagle off + immediate ACK on every network interface (v0.4.3 system boost).</summary>
    NagleOff = 16,
    /// <summary>Keyboard repeat delay 0 / speed max (v0.4.3).</summary>
    KeyboardRepeat = 32,
    /// <summary>Disable Perflib performance counters (v0.4.3).</summary>
    PerfCountersOff = 64,
    /// <summary>Maximum multimedia scheduling priority for games (v0.4.3).</summary>
    GameScheduling = 128,
    /// <summary>Win32PrioritySeparation foreground boost (v0.4.3).</summary>
    ForegroundBoost = 256,
    /// <summary>Game Bar / GameDVR background recording off (v0.4.3).</summary>
    GameDvrOff = 512,
    /// <summary>Disable CPU core parking (v0.4.3).</summary>
    CoreParkingOff = 1024,
    /// <summary>NTFS last-access timestamps off (v0.4.3).</summary>
    NtfsLastAccessOff = 2048,
    /// <summary>Ultimate Performance power scheme (v0.4.3).</summary>
    UltimatePower = 4096,
    /// <summary>Stop the Windows Search indexing service while enabled (v0.4.3).</summary>
    SearchIndexingOff = 8192,
    /// <summary>Stop the SysMain/SuperFetch service while enabled (v0.4.3).</summary>
    SysMainOff = 16384,
}

public enum TweakOutcome { Applied, AlreadySet, NotSet, Skipped, Failed }

/// <summary>Verified per-tweak result: what the value was, what it is now, and whether the change really took effect.</summary>
public sealed record TweakResult(GamingTweak Tweak, TweakOutcome Outcome, string? Before, string? After, string? Note);

public sealed record GameProfile
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required string ExecutablePath { get; init; }
    public long? DnsProfileId { get; init; }
    public long? TunnelProfileId { get; init; }
    public SplitMode SplitMode { get; init; } = SplitMode.Include;
    public GamingTweak Tweaks { get; init; } = GamingTweak.HighPerformancePower | GamingTweak.GameMode;
    public string? PingTarget { get; init; }
    /// <summary>v0.4.37 — activate automatically when this game's executable starts (see GameLaunchTracker).</summary>
    public bool AutoActivate { get; init; }
}

/// <summary>
/// Reversible gaming tweaks. Every change is journaled with its previous value first and can be reverted
/// (also after a crash). v0.4.3: the optimizer grew from 3 tweaks to a full "system boost" grid
/// (input latency, scheduling, GameDVR, core parking, NTFS, Ultimate Performance, services) —
/// each tweak is individually applicable/revertible via ApplyOneAsync/RevertOneAsync.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GamingOptimizer(IProcessRunner runner, NetworkStateJournal journal)
{
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Balanced = "381b4226-f694-41f0-9685-ff5bb260df2e";
    private const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string MmKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesTaskKey = MmKey + @"\Tasks\Games";
    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string PerfKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib";
    /// <summary>Canonical value name (with spaces — the one Windows tools/docs use). Some guides write the no-space variant; both are honoured.</summary>
    private const string PerfValueName = "Disable Performance Counters";
    private readonly List<Guid> _applied = [];

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRx();

    private static string Pw() => ProcessRunner.System32("powercfg.exe");
    private static string Sc() => ProcessRunner.System32("sc.exe");

    // ======================= single-tweak apply / revert =======================

    /// <summary>Applies exactly one tweak and verifies it by re-reading the value.</summary>
    public async Task<TweakResult> ApplyOneAsync(GamingTweak t, CancellationToken ct) => t switch
    {
        GamingTweak.HighPerformancePower => await ApplySchemeJournaledAsync(t, HighPerf, ct),
        GamingTweak.GameMode => await SetRegJournaledAsync(t, RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord, v => v == "1"),
        GamingTweak.MultimediaNetworkThrottling => await SetRegJournaledAsync(t, RegistryHive.LocalMachine, MmKey, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord, IsThrottleDisabled),
        GamingTweak.NagleOff => await ApplyNagleAsync(t),
        GamingTweak.KeyboardRepeat => await ApplyKeyboardAsync(t),
        GamingTweak.PerfCountersOff => await ApplyPerfCountersAsync(t),
        GamingTweak.GameScheduling => await ApplyGameSchedulingAsync(t),
        GamingTweak.ForegroundBoost => await SetRegJournaledAsync(t, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord, v => v == "38"),
        GamingTweak.GameDvrOff => await ApplyGameDvrAsync(t),
        GamingTweak.CoreParkingOff => await ApplyCoreParkingAsync(t, ct),
        GamingTweak.NtfsLastAccessOff => await SetRegJournaledAsync(t, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 1, RegistryValueKind.DWord, v => v is "1" or "2147483650"),
        GamingTweak.UltimatePower => await ApplyUltimateAsync(t, ct),
        GamingTweak.SearchIndexingOff => await StopServiceAsync(t, "WSearch", "boost-svc-wsearch", ct),
        GamingTweak.SysMainOff => await StopServiceAsync(t, "SysMain", "boost-svc-sysmain", ct),
        _ => new TweakResult(t, TweakOutcome.Skipped, null, null, "not applicable"),
    };

    /// <summary>Reverts one tweak: restores the journaled previous value (or the Windows default when nothing is journaled). Never journals.</summary>
    public async Task<TweakResult> RevertOneAsync(GamingTweak t, CancellationToken ct)
    {
        var target = JournalTarget(t);
        var pending = journal.Pending().Where(e => e.Target == target).OrderBy(e => e.At).ToList();
        var before = pending.Count > 0 ? pending[0].Before : null;
        var r = await RevertCoreAsync(t, before, ct);
        foreach (var e in pending) journal.Complete(e.Id);
        _applied.Clear();
        return r;
    }

    private async Task<TweakResult> RevertCoreAsync(GamingTweak t, string? before, CancellationToken ct) => t switch
    {
        GamingTweak.HighPerformancePower => await SetSchemeAsync(t, before ?? Balanced, ct),
        GamingTweak.GameMode => await SetRegRawAsync(t, RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled",
            before is null ? null : int.Parse(before), RegistryValueKind.DWord, v => before is not null && v == before),
        GamingTweak.MultimediaNetworkThrottling => await SetRegRawAsync(t, RegistryHive.LocalMachine, MmKey, "NetworkThrottlingIndex",
            before is null ? null : unchecked((int)uint.Parse(before)), RegistryValueKind.DWord, null),
        GamingTweak.NagleOff => await RevertNagleAsync(t, before),
        GamingTweak.KeyboardRepeat => await RevertKeyboardAsync(t, before),
        GamingTweak.PerfCountersOff => await RevertPerfCountersAsync(t),
        GamingTweak.GameScheduling => await RevertGameSchedulingAsync(t, before),
        GamingTweak.ForegroundBoost => await SetRegRawAsync(t, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2, RegistryValueKind.DWord, v => v == "2"),
        GamingTweak.GameDvrOff => await RevertGameDvrAsync(t, before),
        GamingTweak.CoreParkingOff => await SetCoreParkingRawAsync(t, ParseOr(before, 100), ct),
        GamingTweak.NtfsLastAccessOff => await SetRegRawAsync(t, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 2, RegistryValueKind.DWord, v => v == "2"),
        GamingTweak.UltimatePower => await SetSchemeAsync(t, before ?? Balanced, ct),
        GamingTweak.SearchIndexingOff => await StartServiceAsync(t, "WSearch", ct),
        GamingTweak.SysMainOff => await StartServiceAsync(t, "SysMain", ct),
        _ => new TweakResult(t, TweakOutcome.Skipped, null, null, "not applicable"),
    };

    internal static string JournalTarget(GamingTweak t) => t switch
    {
        GamingTweak.HighPerformancePower => "power-scheme",
        GamingTweak.GameMode => "gamemode",
        GamingTweak.MultimediaNetworkThrottling => "net-throttling",
        GamingTweak.NagleOff => "boost-nagle",
        GamingTweak.KeyboardRepeat => "boost-keyboard",
        GamingTweak.PerfCountersOff => "boost-perfcounters",
        GamingTweak.GameScheduling => "boost-scheduling",
        GamingTweak.ForegroundBoost => "boost-foreground",
        GamingTweak.GameDvrOff => "boost-gamedvr",
        GamingTweak.CoreParkingOff => "boost-coreparking",
        GamingTweak.NtfsLastAccessOff => "boost-ntfs",
        GamingTweak.UltimatePower => "boost-ultimate",
        GamingTweak.SearchIndexingOff => "boost-svc-wsearch",
        GamingTweak.SysMainOff => "boost-svc-sysmain",
        _ => "boost-other",
    };

    // ======================= registry helpers =======================

    private static RegistryKey Root(RegistryHive hive) => hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
    private static TweakResult NeedAdmin(GamingTweak t) => new(t, TweakOutcome.Skipped, null, null, "needs administrator");

    /// <summary>REG_DWORD reads come back SIGNED (0xFFFFFFFF → "-1"); accept both spellings when verifying the network-throttling kill.</summary>
    private static bool IsThrottleDisabled(string? v) => v is "-1" or "4294967295";

    /// <summary>Journaled verified registry write (apply direction only).</summary>
    private async Task<TweakResult> SetRegJournaledAsync(GamingTweak t, RegistryHive hive, string path, string name, object desired, RegistryValueKind kind, Func<string?, bool> active)
    {
        if (hive == RegistryHive.LocalMachine && !ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            using var k = Root(hive).CreateSubKey(path);
            var before = k.GetValue(name)?.ToString();
            if (active(before)) return new TweakResult(t, TweakOutcome.AlreadySet, before, before, null);
            var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), before, desired.ToString());
            k.SetValue(name, desired, kind);
            var after = k.GetValue(name)?.ToString();
            if (active(after)) { _applied.Add(e.Id); return new TweakResult(t, TweakOutcome.Applied, before, after, null); }
            journal.Complete(e.Id);
            return new TweakResult(t, TweakOutcome.Failed, before, after, "registry write did not stick");
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    /// <summary>Raw registry write for the revert direction (never journals). value = null deletes the value.</summary>
    private async Task<TweakResult> SetRegRawAsync(GamingTweak t, RegistryHive hive, string path, string name, object? value, RegistryValueKind kind, Func<string?, bool>? active)
    {
        if (hive == RegistryHive.LocalMachine && !ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            using var k = Root(hive).CreateSubKey(path);
            var before = k.GetValue(name)?.ToString();
            if (value is null) k.DeleteValue(name, false);
            else k.SetValue(name, value, kind);
            var after = k.GetValue(name)?.ToString();
            var ok = active is null ? !string.Equals(after, before, StringComparison.Ordinal) || value is null && after is null : active(after);
            return new TweakResult(t, TweakOutcome.Applied, before, after, null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    // ======================= power schemes =======================

    /// <summary>Sets the active power scheme WITHOUT journaling (used by both apply-with-journal and revert).</summary>
    private async Task<TweakResult> SetSchemeAsync(GamingTweak t, string guid, CancellationToken ct)
    {
        var before = await ActiveSchemeAsync(ct);
        if (string.Equals(before, guid, StringComparison.OrdinalIgnoreCase)) return new TweakResult(t, TweakOutcome.AlreadySet, before, before, null);
        var ok = (await runner.RunAsync(Pw(), ["/setactive", guid], false, null, ct)).Success;
        var after = ok ? await ActiveSchemeAsync(ct) : before;
        return string.Equals(after, guid, StringComparison.OrdinalIgnoreCase)
            ? new TweakResult(t, TweakOutcome.Applied, before, after, null)
            : new TweakResult(t, TweakOutcome.Failed, before, after, "setactive did not take effect");
    }

    private async Task<TweakResult> ApplySchemeJournaledAsync(GamingTweak t, string guid, CancellationToken ct)
    {
        var before = await ActiveSchemeAsync(ct);
        if (before.Length == 0) return new TweakResult(t, TweakOutcome.Failed, null, null, "powercfg unreadable");
        if (string.Equals(before, guid, StringComparison.OrdinalIgnoreCase)) return new TweakResult(t, TweakOutcome.AlreadySet, before, before, null);
        var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), before, guid);
        var r = await SetSchemeAsync(t, guid, ct);
        if (r.Outcome is TweakOutcome.Applied or TweakOutcome.AlreadySet) { if (r.Outcome == TweakOutcome.Applied) _applied.Add(e.Id); return r; }
        journal.Complete(e.Id);
        return r;
    }

    // ======================= tweak implementations =======================

    /// <summary>Nagle off: TcpAckFrequency=1 + TCPNoDelay=1 on every TCP interface (like console boosters do).</summary>
    private async Task<TweakResult> ApplyNagleAsync(GamingTweak t)
    {
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            using var parent = Registry.LocalMachine.OpenSubKey(InterfacesKey, writable: true);
            if (parent is null) return new TweakResult(t, TweakOutcome.Failed, null, null, "interfaces key missing");
            var before = new Dictionary<string, string?>();
            foreach (var sub in parent.GetSubKeyNames())
            {
                using var k = parent.OpenSubKey(sub, writable: true);
                if (k is null) continue;
                before[$"{sub}\\TcpAckFrequency"] = k.GetValue("TcpAckFrequency")?.ToString();
                before[$"{sub}\\TCPNoDelay"] = k.GetValue("TCPNoDelay")?.ToString();
                k.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                k.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
            }
            var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), JsonSerializer.Serialize(before), "nagle-off");
            _applied.Add(e.Id);
            return new TweakResult(t, TweakOutcome.Applied, null, "1/1", null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    private async Task<TweakResult> RevertNagleAsync(GamingTweak t, string? beforeJson)
    {
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            var before = NetworkStateJournal.Deserialize<Dictionary<string, string?>>(beforeJson);
            using var parent = Registry.LocalMachine.OpenSubKey(InterfacesKey, writable: true);
            if (parent is null) return new TweakResult(t, TweakOutcome.Failed, null, null, "interfaces key missing");
            foreach (var sub in parent.GetSubKeyNames())
            {
                using var k = parent.OpenSubKey(sub, writable: true);
                if (k is null) continue;
                Restore(k, before, $"{sub}\\TcpAckFrequency", "TcpAckFrequency");
                Restore(k, before, $"{sub}\\TCPNoDelay", "TCPNoDelay");
            }
            return new TweakResult(t, TweakOutcome.Applied, beforeJson, null, null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }

        static void Restore(RegistryKey k, Dictionary<string, string?>? before, string full, string value)
        {
            var old = before is not null && before.TryGetValue(full, out var b) ? b : null;
            if (old is null) k.DeleteValue(value, false);
            else k.SetValue(value, int.Parse(old), RegistryValueKind.DWord);
        }
    }

    private async Task<TweakResult> ApplyKeyboardAsync(GamingTweak t)
    {
        await Task.Yield();
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard");
            var d = k.GetValue("KeyboardDelay")?.ToString();
            var s = k.GetValue("KeyboardSpeed")?.ToString();
            if (d == "0" && s == "31") return new TweakResult(t, TweakOutcome.AlreadySet, d, s, null);
            var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), JsonSerializer.Serialize(new { d, s }), "0/31");
            k.SetValue("KeyboardDelay", "0", RegistryValueKind.String);
            k.SetValue("KeyboardSpeed", "31", RegistryValueKind.String);
            _applied.Add(e.Id);
            return new TweakResult(t, TweakOutcome.Applied, $"{d}/{s}", "0/31", null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    private async Task<TweakResult> RevertKeyboardAsync(GamingTweak t, string? beforeJson)
    {
        await Task.Yield();
        try
        {
            var d = "1";
            var s = "31";
            try
            {
                if (beforeJson is not null)
                {
                    using var doc = JsonDocument.Parse(beforeJson);
                    if (doc.RootElement.TryGetProperty("d", out var jd) && jd.GetString() is { } bd) d = bd;
                    if (doc.RootElement.TryGetProperty("s", out var js) && js.GetString() is { } bs) s = bs;
                }
            }
            catch { }
            using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard");
            k.SetValue("KeyboardDelay", d, RegistryValueKind.String);
            k.SetValue("KeyboardSpeed", s, RegistryValueKind.String);
            return new TweakResult(t, TweakOutcome.Applied, beforeJson, $"{d}/{s}", null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    /// <summary>Perflib kill switch: writes the canonical "Disable Performance Counters" value and cleans up the dead no-space name older builds wrote.</summary>
    private async Task<TweakResult> ApplyPerfCountersAsync(GamingTweak t)
    {
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(PerfKey);
            var before = k.GetValue(PerfValueName)?.ToString() ?? k.GetValue("DisablePerformanceCounters")?.ToString();
            if (before == "1") return new TweakResult(t, TweakOutcome.AlreadySet, before, before, null);
            var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), before, "1");
            k.SetValue(PerfValueName, 1, RegistryValueKind.DWord);
            k.DeleteValue("DisablePerformanceCounters", false);
            var after = k.GetValue(PerfValueName)?.ToString();
            if (after == "1") { _applied.Add(e.Id); return new TweakResult(t, TweakOutcome.Applied, before, after, null); }
            journal.Complete(e.Id);
            return new TweakResult(t, TweakOutcome.Failed, before, after, "registry write did not stick");
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    private async Task<TweakResult> RevertPerfCountersAsync(GamingTweak t)
    {
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(PerfKey);
            k.DeleteValue(PerfValueName, false);
            k.DeleteValue("DisablePerformanceCounters", false);
            return new TweakResult(t, TweakOutcome.Applied, "1", null, null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    /// <summary>Multimedia scheduling for games: Priority 6, GPU Priority 8, High scheduling, High SFIO (the classic console-booster tweak).</summary>
    private async Task<TweakResult> ApplyGameSchedulingAsync(GamingTweak t)
    {
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(GamesTaskKey);
            var before = new { gp = k.GetValue("GPU Priority")?.ToString(), p = k.GetValue("Priority")?.ToString(), sc = k.GetValue("Scheduling Category") as string, sf = k.GetValue("SFIO Priority") as string };
            if (before.p == "6" && before.sc == "High") return new TweakResult(t, TweakOutcome.AlreadySet, before.p, before.p, null);
            var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), JsonSerializer.Serialize(before), "games-max");
            k.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
            k.SetValue("Priority", 6, RegistryValueKind.DWord);
            k.SetValue("Scheduling Category", "High", RegistryValueKind.String);
            k.SetValue("SFIO Priority", "High", RegistryValueKind.String);
            k.SetValue("Background Only", "False", RegistryValueKind.String);
            k.SetValue("Clock Rate", 10000, RegistryValueKind.DWord);
            k.SetValue("Affinity", 0, RegistryValueKind.DWord);
            var after = k.GetValue("Priority")?.ToString();
            if (after == "6") { _applied.Add(e.Id); return new TweakResult(t, TweakOutcome.Applied, before.p, after, null); }
            journal.Complete(e.Id);
            return new TweakResult(t, TweakOutcome.Failed, before.p, after, "registry write did not stick");
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    private async Task<TweakResult> RevertGameSchedulingAsync(GamingTweak t, string? beforeJson)
    {
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        await Task.Yield();
        try
        {
            var gp = "8"; var p = "2"; var sc = "Medium"; var sf = "Normal";
            try
            {
                if (beforeJson is not null)
                {
                    using var doc = JsonDocument.Parse(beforeJson);
                    if (doc.RootElement.TryGetProperty("gp", out var g) && g.GetString() is { } v1) gp = v1;
                    if (doc.RootElement.TryGetProperty("p", out var q) && q.GetString() is { } v2) p = v2;
                    if (doc.RootElement.TryGetProperty("sc", out var c) && c.GetString() is { } v3) sc = v3;
                    if (doc.RootElement.TryGetProperty("sf", out var f) && f.GetString() is { } v4) sf = v4;
                }
            }
            catch { }
            using var k = Registry.LocalMachine.CreateSubKey(GamesTaskKey);
            k.SetValue("GPU Priority", int.Parse(gp), RegistryValueKind.DWord);
            k.SetValue("Priority", int.Parse(p), RegistryValueKind.DWord);
            k.SetValue("Scheduling Category", sc, RegistryValueKind.String);
            k.SetValue("SFIO Priority", sf, RegistryValueKind.String);
            return new TweakResult(t, TweakOutcome.Applied, beforeJson, p, null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    /// <summary>Game Bar background recording off (HKCU — no admin needed).</summary>
    private async Task<TweakResult> ApplyGameDvrAsync(GamingTweak t)
    {
        await Task.Yield();
        try
        {
            using var a = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
            using var b = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            var ged = a.GetValue("GameDVR_Enabled")?.ToString();
            var ace = b.GetValue("AppCaptureEnabled")?.ToString();
            if (ged == "0" && ace == "0") return new TweakResult(t, TweakOutcome.AlreadySet, ged, ace, null);
            var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), JsonSerializer.Serialize(new { ged, ace }), "0/0");
            a.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
            b.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
            if (a.GetValue("GameDVR_Enabled")?.ToString() == "0") { _applied.Add(e.Id); return new TweakResult(t, TweakOutcome.Applied, ged, "0/0", null); }
            journal.Complete(e.Id);
            return new TweakResult(t, TweakOutcome.Failed, ged, null, "registry write did not stick");
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    private async Task<TweakResult> RevertGameDvrAsync(GamingTweak t, string? beforeJson)
    {
        await Task.Yield();
        try
        {
            var ged = "1"; var ace = "1";
            try
            {
                if (beforeJson is not null)
                {
                    using var doc = JsonDocument.Parse(beforeJson);
                    if (doc.RootElement.TryGetProperty("ged", out var g) && g.GetString() is { } v1) ged = v1;
                    if (doc.RootElement.TryGetProperty("ace", out var c) && c.GetString() is { } v2) ace = v2;
                }
            }
            catch { }
            using var a = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
            using var b = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            a.SetValue("GameDVR_Enabled", int.Parse(ged), RegistryValueKind.DWord);
            b.SetValue("AppCaptureEnabled", int.Parse(ace), RegistryValueKind.DWord);
            return new TweakResult(t, TweakOutcome.Applied, beforeJson, $"{ged}/{ace}", null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    private async Task<TweakResult> ApplyCoreParkingAsync(GamingTweak t, CancellationToken ct)
    {
        var cur = await ReadCoreParkingAsync(ct);
        if (cur is null) return new TweakResult(t, TweakOutcome.Skipped, null, null, "unavailable on this system");
        if (cur == 100) return new TweakResult(t, TweakOutcome.AlreadySet, "100", "100", null);
        var e = journal.Record(JournalKind.GamingTweak, JournalTarget(t), cur.Value.ToString(), "100");
        var r = await SetCoreParkingRawAsync(t, 100, ct);
        if (r.Outcome != TweakOutcome.Applied) { journal.Complete(e.Id); return r; }
        var check = await ReadCoreParkingAsync(ct);
        if (check == 100) { _applied.Add(e.Id); return r; }
        journal.Complete(e.Id);
        return new TweakResult(t, TweakOutcome.Failed, cur.Value.ToString(), check?.ToString(), "powercfg write did not take effect");
    }

    private async Task<int?> ReadCoreParkingAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync(Pw(), ["/getacvalueindex", "SCHEME_CURRENT", SubProcessor, CpMinCores], false, null, ct);
        var txt = r.StdOut.Trim();
        var hx = Regex.Match(txt, @"0x([0-9A-Fa-f]{1,8})\s*$");
        if (hx.Success && int.TryParse(hx.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        var dec = Regex.Match(txt, @"(\d{1,3})\s*$");
        return dec.Success ? int.Parse(dec.Groups[1].Value) : null;
    }

    /// <summary>Sets the core-parking minimum WITHOUT journaling (both directions use it).</summary>
    private async Task<TweakResult> SetCoreParkingRawAsync(GamingTweak t, int value, CancellationToken ct)
    {
        try
        {
            await runner.RunAsync(Pw(), ["/setacvalueindex", "SCHEME_CURRENT", SubProcessor, CpMinCores, value.ToString()], false, null, ct);
            await runner.RunAsync(Pw(), ["/setactive", "SCHEME_CURRENT"], false, null, ct);
            return new TweakResult(t, TweakOutcome.Applied, null, value.ToString(), null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    /// <summary>Ultimate Performance: duplicate the well-known scheme (or reuse the existing copy) and activate it.</summary>
    private async Task<TweakResult> ApplyUltimateAsync(GamingTweak t, CancellationToken ct)
    {
        var list = await runner.RunAsync(Pw(), ["/list"], false, null, ct);
        var guid = GuidRx().Matches(list.StdOut).Select(m => m.Value)
            .FirstOrDefault(g => list.StdOut.Contains(g, StringComparison.Ordinal) && list.StdOut.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        if (guid is null)
        {
            var dup = await runner.RunAsync(Pw(), ["/duplicatescheme", Ultimate], false, null, ct);
            guid = GuidRx().Match(dup.StdOut).Value;
        }
        if (guid.Length == 0) return new TweakResult(t, TweakOutcome.Failed, null, null, "powercfg could not create the scheme");
        return await ApplySchemeJournaledAsync(t, guid, ct);
    }

    private async Task<TweakResult> StopServiceAsync(GamingTweak t, string service, string target, CancellationToken ct)
    {
        var q = await runner.RunAsync(Sc(), ["query", service], false, null, ct);
        if (!q.Success) return new TweakResult(t, TweakOutcome.Failed, null, null, "sc query failed");
        if (q.StdOut.Contains("STOPPED")) return new TweakResult(t, TweakOutcome.AlreadySet, "STOPPED", "STOPPED", null);
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        var e = journal.Record(JournalKind.GamingTweak, target, "RUNNING", "STOPPED");
        var r = await runner.RunAsync(Sc(), ["stop", service], false, null, ct);
        if (!r.Success) { journal.Complete(e.Id); return new TweakResult(t, TweakOutcome.Failed, "RUNNING", null, "sc stop failed"); }
        _applied.Add(e.Id);
        return new TweakResult(t, TweakOutcome.Applied, "RUNNING", "STOP_PENDING", "stop requested");
    }

    private async Task<TweakResult> StartServiceAsync(GamingTweak t, string service, CancellationToken ct)
    {
        var q = await runner.RunAsync(Sc(), ["query", service], false, null, ct);
        if (!q.Success) return new TweakResult(t, TweakOutcome.Failed, null, null, "sc query failed");
        if (q.StdOut.Contains("RUNNING")) return new TweakResult(t, TweakOutcome.AlreadySet, "RUNNING", "RUNNING", null);
        if (!ProcessRunner.IsElevated) return NeedAdmin(t);
        var r = await runner.RunAsync(Sc(), ["start", service], false, null, ct);
        return r.Success
            ? new TweakResult(t, TweakOutcome.Applied, "STOPPED", "START_PENDING", "start requested")
            : new TweakResult(t, TweakOutcome.Failed, "STOPPED", null, "sc start failed");
    }

    private static int ParseOr(string? s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

    // ======================= batch API (kept for compatibility) =======================

    /// <summary>Applies every selected tweak and verifies each one (used by the old batch UI and tests).</summary>
    public async Task<IReadOnlyList<TweakResult>> ApplyAsync(GamingTweak tweaks, CancellationToken ct)
    {
        var results = new List<TweakResult>();
        foreach (GamingTweak t in Enum.GetValues<GamingTweak>())
            if (t != GamingTweak.None && tweaks.HasFlag(t))
                results.Add(await ApplyOneAsync(t, ct));
        return results;
    }

    /// <summary>Reads the CURRENT states of the full boost grid without changing anything — the UI shows these on open, so the user sees what is really in effect instead of trusting a checkbox.</summary>
    public async Task<IReadOnlyList<TweakResult>> InspectAsync(CancellationToken ct)
    {
        var list = new List<TweakResult>();
        var cur = await runner.RunAsync(Pw(), ["/getactivescheme"], false, null, ct);
        var scheme = GuidRx().Match(cur.StdOut).Value;
        list.Add(new(GamingTweak.UltimatePower, string.Equals(scheme, Ultimate, StringComparison.OrdinalIgnoreCase) ? TweakOutcome.AlreadySet : TweakOutcome.NotSet, null, scheme.Length > 0 ? scheme : null, null));

        list.Add(InspectReg(RegistryHive.LocalMachine, MmKey, "NetworkThrottlingIndex", IsThrottleDisabled, GamingTweak.MultimediaNetworkThrottling));
        list.Add(InspectReg(RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardDelay", v => v == "0", GamingTweak.KeyboardRepeat));
        try
        {
            using var pk = Registry.LocalMachine.OpenSubKey(PerfKey);
            var pv = pk?.GetValue(PerfValueName)?.ToString() ?? pk?.GetValue("DisablePerformanceCounters")?.ToString();
            list.Add(new(GamingTweak.PerfCountersOff, pv == "1" ? TweakOutcome.AlreadySet : TweakOutcome.NotSet, null, pv, ProcessRunner.IsElevated ? null : "needs administrator"));
        }
        catch (Exception ex) { list.Add(new(GamingTweak.PerfCountersOff, TweakOutcome.Failed, null, null, ex.GetType().Name)); }
        list.Add(InspectReg(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", v => v == "38", GamingTweak.ForegroundBoost));
        list.Add(InspectReg(RegistryHive.LocalMachine, GamesTaskKey, "Priority", v => v == "6", GamingTweak.GameScheduling));
        list.Add(InspectReg(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", v => v == "0", GamingTweak.GameDvrOff));
        list.Add(InspectReg(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", v => v is "1" or "2147483650", GamingTweak.NtfsLastAccessOff));
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(InterfacesKey);
            var on = k?.GetSubKeyNames().Any(sub => { using var s = k!.OpenSubKey(sub); return s?.GetValue("TcpAckFrequency")?.ToString() == "1"; }) == true;
            list.Add(new(GamingTweak.NagleOff, on ? TweakOutcome.AlreadySet : TweakOutcome.NotSet, null, on ? "1" : null, ProcessRunner.IsElevated ? null : "needs administrator"));
        }
        catch (Exception ex) { list.Add(new(GamingTweak.NagleOff, TweakOutcome.Failed, null, null, ex.GetType().Name)); }

        var cores = await ReadCoreParkingAsync(ct);
        list.Add(new(GamingTweak.CoreParkingOff, cores == 100 ? TweakOutcome.AlreadySet : TweakOutcome.NotSet, null, cores?.ToString(), cores is null ? "unavailable on this system" : null));

        foreach (var (tweak, svc) in new[] { (GamingTweak.SearchIndexingOff, "WSearch"), (GamingTweak.SysMainOff, "SysMain") })
        {
            var q = await runner.RunAsync(Sc(), ["query", svc], false, null, ct);
            if (!q.Success) list.Add(new(tweak, TweakOutcome.Failed, null, null, "sc query failed"));
            else list.Add(new(tweak, q.StdOut.Contains("STOPPED") ? TweakOutcome.AlreadySet : TweakOutcome.NotSet, null, null, ProcessRunner.IsElevated ? null : "needs administrator"));
        }
        return list;
    }

    private static TweakResult InspectReg(RegistryHive hive, string path, string name, Func<string?, bool> active, GamingTweak t)
    {
        try
        {
            using var k = Root(hive).OpenSubKey(path);
            var v = k?.GetValue(name)?.ToString();
            return new TweakResult(t, active(v) ? TweakOutcome.AlreadySet : TweakOutcome.NotSet, null, v, hive == RegistryHive.LocalMachine && !ProcessRunner.IsElevated ? "needs administrator" : null);
        }
        catch (Exception ex) { return new TweakResult(t, TweakOutcome.Failed, null, null, ex.GetType().Name); }
    }

    /// <summary>Raises a running game's priority to High (never Realtime).</summary>
    public static int BoostProcesses(string exePath)
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
            using (p)
                try { if (string.Equals(ProcessScanner.GetProcessPath(p.Id), exePath, StringComparison.OrdinalIgnoreCase)) { p.PriorityClass = ProcessPriorityClass.High; n++; } } catch { }
        return n;
    }

    private async Task<string> ActiveSchemeAsync(CancellationToken ct)
    {
        var cur = await runner.RunAsync(Pw(), ["/getactivescheme"], false, null, ct);
        return GuidRx().Match(cur.StdOut).Value;
    }

    /// <summary>Reverts everything this optimizer ever applied (journal entries + legacy v0.4.2 targets).</summary>
    public async Task RevertAsync(CancellationToken ct)
    {
        var byTarget = Enum.GetValues<GamingTweak>().Where(t => t != GamingTweak.None && JournalTarget(t) != "boost-other")
            .ToDictionary(t => JournalTarget(t));
        var targets = journal.Pending().Select(e => e.Target).Distinct().ToList();
        foreach (var target in targets)
        {
            if (byTarget.TryGetValue(target, out var tweak)) { await RevertOneAsync(tweak, ct); continue; }
            // Legacy v0.4.2 entries (pre-boost targets).
            var e = journal.Pending().FirstOrDefault(x => x.Target == target);
            if (e is null) continue;
            switch (target)
            {
                case "power-scheme" when e.Before is not null:
                    await runner.RunAsync(Pw(), ["/setactive", e.Before], false, null, ct); break;
                case "gamemode":
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar"))
                        if (e.Before is null) k.DeleteValue("AutoGameModeEnabled", false); else k.SetValue("AutoGameModeEnabled", int.Parse(e.Before), RegistryValueKind.DWord);
                    break;
                case "net-throttling" when ProcessRunner.IsElevated:
                    using (var k = Registry.LocalMachine.CreateSubKey(MmKey))
                        if (e.Before is null) k.DeleteValue("NetworkThrottlingIndex", false); else k.SetValue("NetworkThrottlingIndex", unchecked((int)uint.Parse(e.Before)), RegistryValueKind.DWord);
                    break;
                default: continue;
            }
            journal.Complete(e.Id);
        }
        _applied.Clear();
    }
}
