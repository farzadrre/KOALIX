using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Koalix.Core;
using Koalix.Dns;
using Koalix.Tunneling;

namespace Koalix.Infrastructure;

/* ────────────────────────────────────────────────────────────────────────────
   One-click backup / restore (v0.4.12, user request).
   The file is a single AES-GCM encrypted JSON (PBKDF2-SHA256, 210k iterations)
   so it can travel between PCs safely: DPAPI (used at rest) is machine-bound,
   the backup file is not. Payload covers settings, tunnel profiles (incl. tags
   and their subscription links), subscriptions, split-tunnel app rules and the
   saved DNS profiles.
   ──────────────────────────────────────────────────────────────────────────── */

public sealed record BackupProfile(string Name, string Engine, string Source, string Content, string? Tag, long? Subscription, bool Enabled);
public sealed record BackupSubscription(string Name, string Url);
public sealed record BackupAppRule(string DisplayName, string ExePath, string Action, bool IncludeFolder, string? Group);
public sealed record BackupDnsProfile(string Name, string? V4a, string? V4b, string? V6a, string? V6b, bool Favorite);
public sealed record BackupPayload(
    int Version, string ExportedAt,
    Dictionary<string, string> Settings,
    List<BackupProfile> Profiles,
    List<BackupSubscription> Subscriptions,
    List<BackupAppRule> AppRules,
    List<BackupDnsProfile> DnsProfiles);

public sealed class BackupService(Database db, ISecretProtector protector, TunnelProfileRepository profiles,
    SubscriptionRepository subs, AppRuleRepository rules, DnsProfileRepository dns)
{
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16, Iterations = 210_000;
    private const string Magic = "KOALIX-BAK1";

    /// <summary>Writes an encrypted backup. Only called when no tunnel is active (the caller refuses otherwise).</summary>
    public void Export(string file, string password)
    {
        var settingsMap = new Dictionary<string, string>();
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key,value FROM settings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) settingsMap[r.GetString(0)] = r.GetString(1);
        }

        var payload = new BackupPayload(
            1, DateTimeOffset.Now.ToString("O"),
            settingsMap,
            profiles.All().Select(p => new BackupProfile(p.Name, p.Engine.ToString(), p.Source.ToString(), p.Content, p.Tag, p.SubscriptionId, p.Enabled)).ToList(),
            subs.All().Select(s => new BackupSubscription(s.Name, s.Url)).ToList(),
            rules.All().Select(a => new BackupAppRule(a.DisplayName, a.ExecutablePath, a.Action.ToString(), a.IncludeFolder, a.Group)).ToList(),
            dns.All().Select(d => new BackupDnsProfile(d.Name, d.PrimaryV4, d.SecondaryV4, d.PrimaryV6, d.SecondaryV6, d.IsFavorite)).ToList());

        // Subscription ids must be remapped: profiles reference them by index in the backup file.
        var subIds = subs.All().Select(s => s.Id).ToList();
        var remapped = payload with
        {
            Profiles = payload.Profiles.Select(p => p with { Subscription = p.Subscription is { } id ? subIds.IndexOf(id) : null }).ToList(),
        };

        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var json = JsonSerializer.SerializeToUtf8Bytes(remapped, JsonOpts);
        var cipher = new byte[json.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, json, cipher, tag, Encoding.UTF8.GetBytes(Magic));

        using var out1 = new FileStream(file, FileMode.Create, FileAccess.Write);
        out1.Write(Encoding.ASCII.GetBytes(Magic));
        out1.Write(salt); out1.Write(nonce); out1.Write(tag); out1.Write(cipher);
    }

    /// <summary>Reads and decrypts a backup file; throws InvalidDataException (bad file) or CryptographicException (wrong password).</summary>
    private BackupPayload ReadPayload(string file, string password)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        var magic = new byte[Magic.Length];
        try { fs.ReadExactly(magic); }
        catch (EndOfStreamException) { throw new InvalidDataException("Backup.BadFile"); }
        if (Encoding.ASCII.GetString(magic) != Magic) throw new InvalidDataException("Backup.BadFile");
        var salt = new byte[SaltLen]; var nonce = new byte[NonceLen]; var tag = new byte[TagLen];
        try
        {
            fs.ReadExactly(salt); fs.ReadExactly(nonce); fs.ReadExactly(tag);
            var cipher = new byte[fs.Length - fs.Position];
            fs.ReadExactly(cipher);
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
            var json = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, TagLen))
                gcm.Decrypt(nonce, cipher, tag, json, Encoding.UTF8.GetBytes(Magic)); // CryptographicException when the password is wrong
            return JsonSerializer.Deserialize<BackupPayload>(json, JsonOpts) ?? throw new InvalidDataException("Backup.BadFile");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Backup.BadFile"); }
    }

    /// <summary>Reads an encrypted backup and REPLACES the current profiles/subscriptions/rules/dns + merges settings.</summary>
    public void Import(string file, string password)
    {
        var p = ReadPayload(file, password);

        // Everything is replaced inside one transaction: a half-restored state must be impossible.
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        try
        {
            Exec(c, tx, "DELETE FROM tunnel_profiles");
            Exec(c, tx, "DELETE FROM subscriptions");
            Exec(c, tx, "DELETE FROM app_rules");
            Exec(c, tx, "DELETE FROM dns_profiles");
            var subIds = new List<long>();
            foreach (var s in p.Subscriptions)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO subscriptions(name,url_protected) VALUES($n,$u); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$n", s.Name); cmd.Parameters.AddWithValue("$u", protector.Protect(s.Url));
                subIds.Add((long)cmd.ExecuteScalar()!);
            }
            foreach (var pr in p.Profiles)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO tunnel_profiles(name,engine,source,content_protected,subscription_id,tag,enabled) VALUES($n,$e,$s,$c,$sub,$tag,$en)";
                cmd.Parameters.AddWithValue("$n", pr.Name); cmd.Parameters.AddWithValue("$e", pr.Engine);
                cmd.Parameters.AddWithValue("$s", pr.Source); cmd.Parameters.AddWithValue("$c", protector.Protect(pr.Content));
                cmd.Parameters.AddWithValue("$sub", pr.Subscription is { } idx && idx >= 0 && idx < subIds.Count ? subIds[(int)idx] : DBNull.Value);
                cmd.Parameters.AddWithValue("$tag", (object?)pr.Tag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$en", pr.Enabled ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            foreach (var a in p.AppRules)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO app_rules(display_name,exe_path,action,include_folder,grp) VALUES($d,$p,$a,$f,$g)
                    ON CONFLICT(exe_path) DO UPDATE SET display_name=$d, action=$a, include_folder=$f, grp=$g
                    """;
                cmd.Parameters.AddWithValue("$d", a.DisplayName); cmd.Parameters.AddWithValue("$p", a.ExePath);
                cmd.Parameters.AddWithValue("$a", a.Action); cmd.Parameters.AddWithValue("$f", a.IncludeFolder ? 1 : 0);
                cmd.Parameters.AddWithValue("$g", (object?)a.Group ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            foreach (var d in p.DnsProfiles)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO dns_profiles(name,primary_v4,secondary_v4,primary_v6,secondary_v6,favorite) VALUES($n,$a,$b,$c2,$d2,$f)";
                cmd.Parameters.AddWithValue("$n", d.Name);
                cmd.Parameters.AddWithValue("$a", (object?)d.V4a ?? DBNull.Value); cmd.Parameters.AddWithValue("$b", (object?)d.V4b ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c2", (object?)d.V6a ?? DBNull.Value); cmd.Parameters.AddWithValue("$d2", (object?)d.V6b ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$f", d.Favorite ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            foreach (var (k, v) in p.Settings)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
                cmd.Parameters.AddWithValue("$k", k); cmd.Parameters.AddWithValue("$v", v);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
