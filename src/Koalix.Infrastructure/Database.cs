using Microsoft.Data.Sqlite;

namespace Koalix.Infrastructure;

/// <summary>SQLite with versioned, transactional migrations (table schema_version). A backup is taken before migrating.</summary>
public sealed class Database
{
    private readonly string _cs;
    private readonly string _file;
    public Database(string file) { _file = file; _cs = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString(); }

    public static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE dns_profiles(id INTEGER PRIMARY KEY, name TEXT NOT NULL, primary_v4 TEXT, secondary_v4 TEXT,
            primary_v6 TEXT, secondary_v6 TEXT, category TEXT, favorite INTEGER NOT NULL DEFAULT 0, notes TEXT,
            last_score REAL, last_tested TEXT, UNIQUE(primary_v4, secondary_v4));
        CREATE TABLE tunnel_profiles(id INTEGER PRIMARY KEY, name TEXT NOT NULL, engine TEXT NOT NULL, source TEXT NOT NULL,
            content_protected TEXT NOT NULL, subscription_id INTEGER, enabled INTEGER NOT NULL DEFAULT 1,
            last_connected TEXT, last_latency REAL);
        CREATE TABLE subscriptions(id INTEGER PRIMARY KEY, name TEXT NOT NULL, url_protected TEXT NOT NULL, last_update TEXT);
        CREATE TABLE app_rules(id INTEGER PRIMARY KEY, display_name TEXT NOT NULL, exe_path TEXT NOT NULL, action TEXT NOT NULL,
            include_folder INTEGER NOT NULL DEFAULT 0, grp TEXT, UNIQUE(exe_path));
        CREATE TABLE gaming_profiles(id INTEGER PRIMARY KEY, name TEXT NOT NULL, exe_path TEXT, json TEXT NOT NULL);
        CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE connection_history(id INTEGER PRIMARY KEY, profile_name TEXT, engine TEXT, started TEXT NOT NULL,
            ended TEXT, result TEXT, avg_latency REAL, bytes_up INTEGER, bytes_down INTEGER);
        CREATE TABLE repair_history(id INTEGER PRIMARY KEY, action TEXT NOT NULL, success INTEGER NOT NULL, at TEXT NOT NULL, output TEXT);
        """,
        // v2
        """
        CREATE TABLE dns_test_results(id INTEGER PRIMARY KEY, profile_id INTEGER NOT NULL REFERENCES dns_profiles(id) ON DELETE CASCADE,
            at TEXT NOT NULL, median_ms REAL, jitter_ms REAL, loss REAL, dns_success REAL, score REAL);
        CREATE INDEX ix_history_started ON connection_history(started);
        """,
        // v3 (v0.4.12) — profile groups/tags: a free-form label per profile, shown as a chip and filterable on the Tunnels page.
        """
        ALTER TABLE tunnel_profiles ADD COLUMN tag TEXT;
        CREATE INDEX ix_profiles_tag ON tunnel_profiles(tag);
        """,
        // v4 (v0.4.15) — persistent per-app usage: daily byte totals per application so the usage card can answer
        // "how much did this app consume today / in 3 days / this week / this month / ever" across sessions.
        """
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY, app TEXT NOT NULL, day TEXT NOT NULL,
            bytes_up INTEGER NOT NULL DEFAULT 0, bytes_down INTEGER NOT NULL DEFAULT 0, UNIQUE(app, day));
        CREATE INDEX ix_usage_day ON app_usage(day);
        """,
        // v5 (v0.4.19) — manual drag-and-drop order of the tunnel list: every reorder writes 10, 20, 30 …
        // so a dragged config keeps its place across restarts; new profiles default to 0 (alphabetical tail).
        """
        ALTER TABLE tunnel_profiles ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_profiles_sort ON tunnel_profiles(sort_order);
        """,
        // v6 (v0.4.20) — per-subscription panel metadata: the user's chosen engine for every server of that
        // subscription (Auto / sing-box / Xray — the fix for "everything lands on sing-box") and the panel's
        // subscription-userinfo volume/expiry (upload/download/total bytes, Unix expiry) captured on every fetch.
        """
        ALTER TABLE subscriptions ADD COLUMN engine TEXT;
        ALTER TABLE subscriptions ADD COLUMN upload_bytes INTEGER;
        ALTER TABLE subscriptions ADD COLUMN download_bytes INTEGER;
        ALTER TABLE subscriptions ADD COLUMN total_bytes INTEGER;
        ALTER TABLE subscriptions ADD COLUMN expire_unix INTEGER;
        """,
    ];

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;"; cmd.ExecuteNonQuery();
        return c;
    }

    public int CurrentVersion()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL); SELECT COALESCE(MAX(version),0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Migrate(string? backupDir = null)
    {
        var v = CurrentVersion();
        if (v >= Migrations.Length) return;
        if (v > 0 && backupDir is not null && File.Exists(_file))
        {
            using var src = Open(); using var dst = new SqliteConnection($"Data Source={Path.Combine(backupDir, $"koalix-v{v}-{DateTime.Now:yyyyMMddHHmmss}.db")}");
            dst.Open(); src.BackupDatabase(dst);
        }
        using var c = Open();
        for (int i = v; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = Migrations[i] + $"\nINSERT INTO schema_version(version) VALUES({i + 1});";
            cmd.ExecuteNonQuery(); tx.Commit();
        }
    }
}
