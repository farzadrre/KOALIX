using System.IO;
using System.Security.Cryptography;
using System.Linq;
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
/// <summary>v0.4.52 — what the user chose to put inside a backup file (BackupScopeDialog). Profiles imply
/// subscriptions (profiles link to them by index); logs are not an option anywhere — they are never exported.</summary>
public sealed record BackupOptions(bool Settings = true, bool Profiles = false, bool Subscriptions = false, bool AppRules = false, bool DnsProfiles = false);

public sealed record BackupPayload(
    int Version, string ExportedAt,
    Dictionary<string, string> Settings,
    List<BackupProfile> Profiles,
    List<BackupSubscription> Subscriptions,
    List<BackupAppRule> AppRules,
    List<BackupDnsProfile> DnsProfiles,
    /// <summary>v0.4.52 — which sections the file actually carries; null = a legacy all-in file.</summary>
    List<string>? Sections = null);

public sealed class BackupService(Database db, ISecretProtector protector, TunnelProfileRepository profiles,
    SubscriptionRepository subs, AppRuleRepository rules, DnsProfileRepository dns)
{
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16, Iterations = 210_000;
    private const string Magic = "KOALIX-BAK1";
    /// <summary>v0.4.50 («یک قابلیت بدون رمز هم بزار براش») — passwordless files carry a different magic and are
    /// sealed with a built-in application key: still tamper-protected (AES-GCM) and machine-independent, but
    /// honestly NOT secret. The header distinguishes the two modes so restore never asks for a password it does
    /// not need and export never forces one on the user.</summary>
    private const string MagicPlain = "KOALIX-BAK0";

    private static byte[] OpenKey() => SHA256.HashData(Encoding.UTF8.GetBytes("KOALIX-OPEN-BACKUP-KEY-V1"));

    /// <summary>Writes an encrypted backup. Only called when no tunnel is active (the caller refuses otherwise).
    /// v0.4.50: pass null for the passwordless «بدون رمز» mode.</summary>
    public void Export(string file, string? password, BackupOptions? opts = null)
    {
        var settingsMap = new Dictionary<string, string>();
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key,value FROM settings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) settingsMap[r.GetString(0)] = r.GetString(1);
        }

        // v0.4.52 — the user picks the sections (BackupScopeDialog); nothing unchecked ever leaves this PC.
        // Profiles imply subscriptions (profiles reference them by index); logs have NO section anywhere.
        var optsUse = opts ?? new BackupOptions(true, true, true, true, true);
        var sections = new List<string>();
        if (optsUse.Settings) sections.Add("settings");
        if (optsUse.Profiles) sections.Add("profiles");
        if (optsUse.Profiles || optsUse.Subscriptions) sections.Add("subscriptions");
        if (optsUse.AppRules) sections.Add("appRules");
        if (optsUse.DnsProfiles) sections.Add("dnsProfiles");
        var payload = new BackupPayload(
            1, DateTimeOffset.Now.ToString("O"),
            optsUse.Settings ? settingsMap : new Dictionary<string, string>(),
            optsUse.Profiles ? profiles.All().Select(p => new BackupProfile(p.Name, p.Engine.ToString(), p.Source.ToString(), p.Content, p.Tag, p.SubscriptionId, p.Enabled)).ToList() : [],
            optsUse.Profiles || optsUse.Subscriptions ? subs.All().Select(s => new BackupSubscription(s.Name, s.Url)).ToList() : [],
            optsUse.AppRules ? rules.All().Select(a => new BackupAppRule(a.DisplayName, a.ExecutablePath, a.Action.ToString(), a.IncludeFolder, a.Group)).ToList() : [],
            optsUse.DnsProfiles ? dns.All().Select(d => new BackupDnsProfile(d.Name, d.PrimaryV4, d.SecondaryV4, d.PrimaryV6, d.SecondaryV6, d.IsFavorite)).ToList() : [],
            sections);

        // Subscription ids must be remapped: profiles reference them by index in the backup file.
        var subIds = subs.All().Select(s => s.Id).ToList();
        var remapped = payload with
        {
            Profiles = optsUse.Profiles
                ? payload.Profiles.Select(p => p with { Subscription = p.Subscription is { } id ? subIds.IndexOf(id) : null }).ToList()
                : payload.Profiles,
        };

        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var json = JsonSerializer.SerializeToUtf8Bytes(remapped, JsonOpts);
        var cipher = new byte[json.Length];
        var tag = new byte[TagLen];
        var passwordless = password is not { Length: > 0 }; // v0.4.50 — «بدون رمز»
        var magic = passwordless ? MagicPlain : Magic;
        var key = passwordless
            ? OpenKey()
            : Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password!), salt, Iterations, HashAlgorithmName.SHA256, 32);
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, json, cipher, tag, Encoding.UTF8.GetBytes(magic));

        using var out1 = new FileStream(file, FileMode.Create, FileAccess.Write);
        out1.Write(Encoding.ASCII.GetBytes(magic));
        out1.Write(salt); out1.Write(nonce); out1.Write(tag); out1.Write(cipher);
    }

    /// <summary>v0.4.50 — true when the file was exported WITHOUT a password: restore then skips the prompt entirely.</summary>
    public static bool IsPasswordless(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        var magic = new byte[Magic.Length];
        try { fs.ReadExactly(magic); } catch (EndOfStreamException) { return false; }
        return Encoding.ASCII.GetString(magic) == MagicPlain;
    }

    /// <summary>Reads and decrypts a backup file; throws InvalidDataException (bad file) or CryptographicException (wrong password).</summary>
    private BackupPayload ReadPayload(string file, string? password)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        var magic = new byte[Magic.Length];
        try { fs.ReadExactly(magic); }
        catch (EndOfStreamException) { throw new InvalidDataException("Backup.BadFile"); }
        var magicText = Encoding.ASCII.GetString(magic);
        if (magicText is not (Magic or MagicPlain)) throw new InvalidDataException("Backup.BadFile");
        var passwordless = magicText == MagicPlain; // v0.4.50 — restore never prompts for a password this file does not use
        var salt = new byte[SaltLen]; var nonce = new byte[NonceLen]; var tag = new byte[TagLen];
        try
        {
            fs.ReadExactly(salt); fs.ReadExactly(nonce); fs.ReadExactly(tag);
            var cipher = new byte[fs.Length - fs.Position];
            fs.ReadExactly(cipher);
            var key = passwordless
                ? OpenKey()
                : Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? throw new InvalidDataException("Backup.BadFile")), salt, Iterations, HashAlgorithmName.SHA256, 32);
            var json = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, TagLen))
                gcm.Decrypt(nonce, cipher, tag, json, Encoding.UTF8.GetBytes(magicText)); // CryptographicException when the password is wrong
            return JsonSerializer.Deserialize<BackupPayload>(json, JsonOpts) ?? throw new InvalidDataException("Backup.BadFile");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Backup.BadFile"); }
    }

    /// <summary>Reads a backup and REPLACES the current profiles/subscriptions/rules/dns + merges settings.</summary>
    public void Import(string file, string? password)
    {
        var p = ReadPayload(file, password);

        // Everything is replaced inside one transaction: a half-restored state must be impossible.
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        try
        {
            // v0.4.52 — selective restore: only the sections the file actually carries are replaced; everything
            // else the user keeps on this machine stays untouched. Legacy files (Sections == null) import all.
            var sections = p.Sections ?? ["settings", "profiles", "subscriptions", "appRules", "dnsProfiles"];
            bool Has(string s) => sections.Contains(s);
            var subIds = new List<long>();
            if (Has("profiles"))
            {
                Exec(c, tx, "DELETE FROM tunnel_profiles");
                Exec(c, tx, "DELETE FROM subscriptions");
            }
            else if (Has("subscriptions"))
            {
                Exec(c, tx, "DELETE FROM subscriptions");
            }
            foreach (var s in Has("profiles") || Has("subscriptions") ? p.Subscriptions : Enumerable.Empty<BackupSubscription>())
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO subscriptions(name,url_protected) VALUES($n,$u); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$n", s.Name); cmd.Parameters.AddWithValue("$u", protector.Protect(s.Url));
                subIds.Add((long)cmd.ExecuteScalar()!);
            }
            foreach (var pr in Has("profiles") ? p.Profiles : Enumerable.Empty<BackupProfile>())
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
            if (Has("appRules"))
                Exec(c, tx, "DELETE FROM app_rules");
            foreach (var a in Has("appRules") ? p.AppRules : Enumerable.Empty<BackupAppRule>())
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
            if (Has("dnsProfiles"))
                Exec(c, tx, "DELETE FROM dns_profiles");
            foreach (var d in Has("dnsProfiles") ? p.DnsProfiles : Enumerable.Empty<BackupDnsProfile>())
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
            foreach (var (k, v) in Has("settings") ? p.Settings : Enumerable.Empty<KeyValuePair<string, string>>())
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
