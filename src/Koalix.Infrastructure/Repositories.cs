using Microsoft.Data.Sqlite;
using Koalix.Core;
using Koalix.Dns;
using Koalix.Tunneling;

namespace Koalix.Infrastructure;

public sealed class SettingsRepository(Database db)
{
    public string? Get(string key)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k"; cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
    public void Set(string key, string value)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery();
    }
}

public sealed class TunnelProfileRepository(Database db, ISecretProtector protector)
{
    public long Add(TunnelProfile p)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        // v0.4.19: new profiles append at the END of the manual order (never jump above dragged rows).
        cmd.CommandText = """
            INSERT INTO tunnel_profiles(name,engine,source,content_protected,subscription_id,tag,enabled,sort_order)
            VALUES($n,$e,$s,$c,$sub,$tag,$en,(SELECT COALESCE(MAX(sort_order),0)+10 FROM tunnel_profiles)); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", p.Name); cmd.Parameters.AddWithValue("$e", p.Engine.ToString());
        cmd.Parameters.AddWithValue("$s", p.Source.ToString()); cmd.Parameters.AddWithValue("$c", protector.Protect(p.Content));
        cmd.Parameters.AddWithValue("$sub", (object?)p.SubscriptionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)p.Tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$en", p.Enabled ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<TunnelProfile> All()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        // v0.4.19: drag-and-drop position first (sort_order), name breaks ties — the manual order survives restarts.
        cmd.CommandText = "SELECT id,name,engine,source,content_protected,subscription_id,tag,enabled,last_connected,last_latency,sort_order FROM tunnel_profiles ORDER BY sort_order, name";
        using var r = cmd.ExecuteReader(); var list = new List<TunnelProfile>(); var fix = new List<(long, string)>();
        while (r.Read())
        {
            // One unreadable row (e.g. encrypted by another Windows user/PC) must never take the whole app down: skip it.
            try
            {
                var stored = r.GetString(4);
                var content = protector.Unprotect(stored);
                if (OperatingSystem.IsWindows() && protector is DpapiSecretProtector d && d.IsLegacy(stored)) fix.Add((r.GetInt64(0), protector.Protect(content)));
                list.Add(new TunnelProfile
                {
                    Id = r.GetInt64(0), Name = r.GetString(1), Engine = Enum.Parse<EngineKind>(r.GetString(2)),
                    Source = Enum.Parse<ProfileSource>(r.GetString(3)), Content = content,
                    SubscriptionId = r.IsDBNull(5) ? null : r.GetInt64(5), Tag = r.IsDBNull(6) ? null : r.GetString(6),
                    Enabled = r.GetInt64(7) == 1,
                    LastConnected = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)), LastLatencyMs = r.IsDBNull(9) ? null : r.GetDouble(9),
                    SortOrder = r.IsDBNull(10) ? 0 : (int)r.GetInt64(10),
                });
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
            {
                Unreadable++;
            }
        }
        r.Close();
        foreach (var (id, val) in fix)
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE tunnel_profiles SET content_protected=$c WHERE id=$i";
            u.Parameters.AddWithValue("$c", val); u.Parameters.AddWithValue("$i", id); u.ExecuteNonQuery();
        }
        return list;
    }

    public void SetLatency(long id, double? ms)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tunnel_profiles SET last_latency=$l WHERE id=$i";
        cmd.Parameters.AddWithValue("$l", (object?)ms ?? DBNull.Value); cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    public void SetEngine(long id, EngineKind engine)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tunnel_profiles SET engine=$e WHERE id=$i";
        cmd.Parameters.AddWithValue("$e", engine.ToString()); cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>Engine switch for many profiles at once (v0.4.20 bulk action — multi-select on the Tunnels page).
    /// The caller already filtered which rows MAY switch (share links only); this persists it in one transaction.</summary>
    public void SetEngineMany(IEnumerable<long> ids, EngineKind engine)
    {
        using var c = db.Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE tunnel_profiles SET engine=$e WHERE id=$i";
        var e = cmd.Parameters.Add("$e", SqliteType.Text); var i = cmd.Parameters.Add("$i", SqliteType.Integer);
        e.Value = engine.ToString();
        foreach (var id in ids) { i.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Deletes many profiles in one transaction (v0.4.20 bulk delete — also used by dead-server cleanup).</summary>
    public void DeleteMany(IEnumerable<long> ids)
    {
        using var c = db.Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM tunnel_profiles WHERE id=$i";
        var i = cmd.Parameters.Add("$i", SqliteType.Integer);
        foreach (var id in ids) { i.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Profiles belonging to a subscription (their previous nodes are replaced on refresh, v0.4.11).</summary>
    public IReadOnlyList<TunnelProfile> BySubscription(long subscriptionId)
    {
        return All().Where(p => p.SubscriptionId == subscriptionId).ToList();
    }

    /// <summary>Persists the manual drag-and-drop order (v0.4.19): the given ids are written as 10, 20, 30 …
    /// in sequence. Profiles absent from the list (hidden behind the active filter) keep their relative order
    /// but move AFTER the reordered block, so the visible order is always exactly what the user arranged.</summary>
    public void Reorder(IEnumerable<long> orderedIds)
    {
        using var c = db.Open(); using var tx = c.BeginTransaction();
        using (var push = c.CreateCommand())
        {
            push.Transaction = tx;
            push.CommandText = "UPDATE tunnel_profiles SET sort_order = sort_order + 1000000 WHERE sort_order < 1000000";
            push.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE tunnel_profiles SET sort_order=$o WHERE id=$i";
        var o = cmd.Parameters.Add("$o", SqliteType.Integer); var i = cmd.Parameters.Add("$i", SqliteType.Integer);
        var n = 0;
        foreach (var id in orderedIds)
        {
            o.Value = (n + 1) * 10; i.Value = id; cmd.ExecuteNonQuery(); n++;
        }
        tx.Commit();
    }

    /// <summary>Sets/clears the group tag of a profile (v0.4.12).</summary>
    public void SetTag(long id, string? tag)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tunnel_profiles SET tag=$t WHERE id=$i";
        cmd.Parameters.AddWithValue("$t", (object?)(string.IsNullOrWhiteSpace(tag) ? null : tag.Trim()));
        cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>Applies one group tag to many profiles at once (v0.4.12 — grouping after import).</summary>
    public void SetTagMany(IEnumerable<long> ids, string? tag)
    {
        using var c = db.Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE tunnel_profiles SET tag=$t WHERE id=$i";
        var t = cmd.Parameters.Add("$t", SqliteType.Text); var i = cmd.Parameters.Add("$i", SqliteType.Integer);
        foreach (var id in ids)
        {
            t.Value = (object?)(string.IsNullOrWhiteSpace(tag) ? DBNull.Value : tag.Trim());
            i.Value = id; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Number of profiles that could not be decrypted (cumulative; never fatal).</summary>
    public int Unreadable { get; private set; }

    public void Delete(long id) { using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM tunnel_profiles WHERE id=$i"; cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery(); }
}

public sealed class AppRuleRepository(Database db)
{
    public void Upsert(AppRule r)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_rules(display_name,exe_path,action,include_folder,grp) VALUES($d,$p,$a,$f,$g)
            ON CONFLICT(exe_path) DO UPDATE SET display_name=$d, action=$a, include_folder=$f, grp=$g
            """;
        cmd.Parameters.AddWithValue("$d", r.DisplayName); cmd.Parameters.AddWithValue("$p", r.ExecutablePath);
        cmd.Parameters.AddWithValue("$a", r.Action.ToString()); cmd.Parameters.AddWithValue("$f", r.IncludeFolder ? 1 : 0);
        cmd.Parameters.AddWithValue("$g", (object?)r.Group ?? DBNull.Value); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<AppRule> All()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,display_name,exe_path,action,include_folder,grp FROM app_rules ORDER BY display_name";
        using var r = cmd.ExecuteReader(); var list = new List<AppRule>();
        while (r.Read())
            list.Add(new AppRule { Id = r.GetInt64(0), DisplayName = r.GetString(1), ExecutablePath = r.GetString(2), Action = Enum.Parse<AppAction>(r.GetString(3)), IncludeFolder = r.GetInt64(4) == 1, Group = r.IsDBNull(5) ? null : r.GetString(5) });
        return list;
    }
    public void Delete(long id) { using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM app_rules WHERE id=$i"; cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery(); }
}

public sealed record HistoryEntry(long Id, string? Profile, string? Engine, DateTimeOffset Started, DateTimeOffset? Ended, string? Result, double? AvgLatency);

public sealed record SubscriptionInfo(long Id, string Name, string Url, DateTimeOffset? LastUpdate, int ProfileCount,
    EngineKind? Engine = null, long? UploadBytes = null, long? DownloadBytes = null, long? TotalBytes = null, long? ExpireUnix = null)
{
    /// <summary>The panel-reported account state (v0.4.20) — used to explain a dead subscription honestly.</summary>
    public bool VolumeExhausted => TotalBytes is > 0 && (UploadBytes ?? 0) + (DownloadBytes ?? 0) >= TotalBytes;
    public bool Expired => ExpireUnix is { } e && DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime <= DateTimeOffset.UtcNow.UtcDateTime;
}

/// <summary>
/// Real subscription management (v0.4.11) — the subscriptions table existed in the schema since v1 but was never used:
/// the link is stored (protected at rest, like profile contents), its servers carry subscription_id, and a refresh
/// replaces exactly that subscription's nodes while leaving everything else untouched.
/// </summary>
public sealed class SubscriptionRepository(Database db, ISecretProtector protector)
{
    public long Add(string name, string url, EngineKind? engine = null)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO subscriptions(name,url_protected,last_update,engine) VALUES($n,$u,$t,$e); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$u", protector.Protect(url));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$e", (object?)engine?.ToString() ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<SubscriptionInfo> All()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT s.id,s.name,s.url_protected,s.last_update,(SELECT COUNT(*) FROM tunnel_profiles p WHERE p.subscription_id=s.id),s.engine,s.upload_bytes,s.download_bytes,s.total_bytes,s.expire_unix FROM subscriptions s ORDER BY s.name";
        using var r = cmd.ExecuteReader(); var list = new List<SubscriptionInfo>();
        while (r.Read())
        {
            string url;
            try { url = protector.Unprotect(r.GetString(2)); } catch { url = ""; } // row from another Windows user — hide, never crash
            list.Add(new(r.GetInt64(0), r.GetString(1), url, r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), (int)r.GetInt64(4),
                r.IsDBNull(5) ? null : Enum.TryParse<EngineKind>(r.GetString(5), out var en) ? en : null,
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9)));
        }
        return list;
    }

    /// <summary>Id of the stored subscription with this exact URL, if any (DPAPI output is not deterministic, so URLs are compared unprotected).</summary>
    public long? FindByUrl(string url) =>
        All().FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>True while the subscription row still exists (v0.4.16 — a background refresh that started before the
    /// user deleted the subscription must never resurrect its servers afterwards).</summary>
    public bool Exists(long id) => All().Any(s => s.Id == id);

    public void SetLastUpdate(long id)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET last_update=$t WHERE id=$i";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O")); cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>Renames a subscription (v0.4.12 — user-chosen names instead of the bare host name).</summary>
    public void Rename(long id, string name)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET name=$n WHERE id=$i";
        cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>The sub's preferred engine (v0.4.20): every share-link node imported from this subscription runs on it.
    /// Null = auto-detect per node (sing-box when natively supported, Xray otherwise).</summary>
    public void SetEngine(long id, EngineKind? engine)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET engine=$e WHERE id=$i";
        cmd.Parameters.AddWithValue("$e", (object?)engine?.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>Stores the panel's subscription-userinfo (v0.4.20): volume counters + account expiry, shown on the
    /// subscription row and the dashboard group panel.</summary>
    public void SetInfo(long id, SubUserInfo info)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET upload_bytes=$u, download_bytes=$d, total_bytes=$t, expire_unix=$x WHERE id=$i";
        cmd.Parameters.AddWithValue("$u", (object?)info.UploadBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)info.DownloadBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)info.TotalBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$x", (object?)info.ExpireUnix ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes the subscription AND its server profiles (the caller refuses while one of them is connected).</summary>
    public void Delete(long id)
    {
        using var c = db.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM tunnel_profiles WHERE subscription_id=$i";
            cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM subscriptions WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
        }
    }
}

public sealed class HistoryRepository(Database db)
{
    public long Start(string profile, EngineKind engine)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO connection_history(profile_name,engine,started) VALUES($p,$e,$s); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", profile); cmd.Parameters.AddWithValue("$e", engine.ToString()); cmd.Parameters.AddWithValue("$s", DateTimeOffset.Now.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }
    public void End(long id, string result, double? avgLatency, long bytesUp = 0, long bytesDown = 0)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE connection_history SET ended=$e,result=$r,avg_latency=$l,bytes_up=$u,bytes_down=$d WHERE id=$i";
        cmd.Parameters.AddWithValue("$e", DateTimeOffset.Now.ToString("O")); cmd.Parameters.AddWithValue("$r", result);
        cmd.Parameters.AddWithValue("$l", (object?)avgLatency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", bytesUp); cmd.Parameters.AddWithValue("$d", bytesDown);
        cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
    }

    /// <summary>One session's recorded usage (connection_history has carried the byte columns since v1 — first used in v0.4.11).</summary>
    public sealed record UsageRow(string? Profile, DateTimeOffset Started, long Up, long Down);

    public IReadOnlyList<UsageRow> UsageSince(DateTimeOffset since)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT profile_name,started,bytes_up,bytes_down FROM connection_history WHERE started>=$s AND (bytes_up>0 OR bytes_down>0) ORDER BY started DESC";
        cmd.Parameters.AddWithValue("$s", since.ToString("O"));
        using var r = cmd.ExecuteReader(); var list = new List<UsageRow>();
        while (r.Read())
            list.Add(new(r.IsDBNull(0) ? null : r.GetString(0), DateTimeOffset.Parse(r.GetString(1)), r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3)));
        return list;
    }
    public IReadOnlyList<HistoryEntry> Recent(int n = 100)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,profile_name,engine,started,ended,result,avg_latency FROM connection_history ORDER BY started DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader(); var list = new List<HistoryEntry>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), DateTimeOffset.Parse(r.GetString(3)),
            r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDouble(6)));
        return list;
    }
}

public sealed class DnsProfileRepository(Database db)
{
    /// <summary>Inserts a profile; returns false when an identical server pair already exists (duplicate).</summary>
    public bool TryAdd(DnsProfile p, out long id)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO dns_profiles(name,primary_v4,secondary_v4,primary_v6,secondary_v6,favorite) VALUES($n,$a,$b,$c,$d,$f); SELECT changes(), last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", p.Name); cmd.Parameters.AddWithValue("$a", (object?)p.PrimaryV4 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)p.SecondaryV4 ?? DBNull.Value); cmd.Parameters.AddWithValue("$c", (object?)p.PrimaryV6 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)p.SecondaryV6 ?? DBNull.Value); cmd.Parameters.AddWithValue("$f", p.IsFavorite ? 1 : 0);
        using var r = cmd.ExecuteReader(); r.Read();
        var inserted = r.GetInt64(0) > 0; id = inserted ? r.GetInt64(1) : 0;
        return inserted;
    }
    public IReadOnlyList<DnsProfile> All()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,primary_v4,secondary_v4,primary_v6,secondary_v6,favorite FROM dns_profiles ORDER BY favorite DESC, name";
        using var r = cmd.ExecuteReader(); var list = new List<DnsProfile>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read()) list.Add(new DnsProfile { Id = r.GetInt64(0), Name = r.GetString(1), PrimaryV4 = S(2), SecondaryV4 = S(3), PrimaryV6 = S(4), SecondaryV6 = S(5), IsFavorite = r.GetInt64(6) == 1 });
        return list;
    }
    public void SaveScore(long profileId, LatencyStatsRow row)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO dns_test_results(profile_id,at,median_ms,jitter_ms,loss,dns_success,score) VALUES($p,$t,$m,$j,$l,$d,$s); UPDATE dns_profiles SET last_score=$s,last_tested=$t WHERE id=$p;";
        cmd.Parameters.AddWithValue("$p", profileId); cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$m", (object?)row.MedianMs ?? DBNull.Value); cmd.Parameters.AddWithValue("$j", (object?)row.JitterMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", row.Loss); cmd.Parameters.AddWithValue("$d", row.DnsSuccess); cmd.Parameters.AddWithValue("$s", row.Score);
        cmd.ExecuteNonQuery();
    }
    public void Delete(long id) { using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM dns_profiles WHERE id=$i"; cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery(); }
}

public sealed record LatencyStatsRow(double? MedianMs, double? JitterMs, double Loss, double DnsSuccess, double Score);

/// <summary>
/// Persistent usage statistics (v0.4.15). Two layers:
///  • connection_history (existing) — one row per session with its totals → powers the period totals
///    (today / 3 days / this week / this month / all time).
///  • app_usage (new table) — daily byte totals PER APPLICATION, persisted live while a sing-box tunnel runs,
///    so "which app used how much" survives restarts and covers whole periods, not just the live session.
/// </summary>
public sealed class UsageRepository(Database db)
{
    public sealed record AppUsageRow(string App, long Up, long Down);

    /// <summary>Day key (local time) — one row per app per day.</summary>
    private static string Day(DateTimeOffset at) => at.LocalDateTime.ToString("yyyy-MM-dd");

    /// <summary>ADDS bytes to today's row of an app (live persist every couple of seconds; tiny UPSERTs).</summary>
    public void AddBytes(string app, long up, long down)
    {
        if (up <= 0 && down <= 0 || string.IsNullOrWhiteSpace(app)) return;
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_usage(app, day, bytes_up, bytes_down) VALUES($a, $d, $u, $n)
            ON CONFLICT(app, day) DO UPDATE SET bytes_up = bytes_up + $u, bytes_down = bytes_down + $n
            """;
        cmd.Parameters.AddWithValue("$a", app); cmd.Parameters.AddWithValue("$d", Day(DateTimeOffset.Now));
        cmd.Parameters.AddWithValue("$u", Math.Max(0, up)); cmd.Parameters.AddWithValue("$n", Math.Max(0, down));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Per-app totals since the given moment, heaviest first (top N).</summary>
    public IReadOnlyList<AppUsageRow> AppUsageSince(DateTimeOffset since, int top = 10)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT app, SUM(bytes_up), SUM(bytes_down) FROM app_usage WHERE day >= $d
            GROUP BY app ORDER BY SUM(bytes_up) + SUM(bytes_down) DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$d", Day(since)); cmd.Parameters.AddWithValue("$n", top);
        using var r = cmd.ExecuteReader(); var list = new List<AppUsageRow>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    /// <summary>True once at least one per-app row exists — the UI uses it to say "per-app stats start today".</summary>
    public bool HasAnyAppUsage()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM app_usage)";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Clears all stored per-app usage (Settings action, irreversible).</summary>
    public void Clear()
    {
        using var c = db.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM app_usage"; cmd.ExecuteNonQuery();
    }
}
