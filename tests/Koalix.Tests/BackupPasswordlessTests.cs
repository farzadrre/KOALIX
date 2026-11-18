using System.Security.Cryptography;
using Koalix.Core;
using Koalix.Infrastructure;

namespace Koalix.Tests;

/// <summary>v0.4.50 — the backup/restore that "didn't work" plus the «بدون رمز» (passwordless) mode: empty password
/// → KOALIX-BAK0 header sealed with the built-in key; restore auto-detects the header so it never prompts for a
/// password the file does not use; password files keep prompting and keep rejecting wrong passwords.</summary>
public class BackupPasswordlessTests
{
    private static (SettingsRepository settings, BackupService svc) NewSvc(out string dir, string tag = "t")
    {
        dir = Directory.CreateTempSubdirectory($"koalix-bak-{tag}").FullName;
        var db = new Database(Path.Combine(dir, "t.db"));
        db.Migrate(dir);
        var protector = new AesFileSecretProtector(Path.Combine(dir, "k"));
        var svc = new BackupService(db, protector,
            new TunnelProfileRepository(db, protector), new SubscriptionRepository(db, protector),
            new AppRuleRepository(db), new DnsProfileRepository(db));
        return (new SettingsRepository(db), svc);
    }

    [Fact]
    public void Passwordless_round_trip_skips_the_password_entirely()
    {
        var (s1, svc1) = NewSvc(out _, "exp");
        s1.Set("probe.name", "koalix-v050");
        var f1 = Path.Combine(Path.GetTempPath(), $"koalix-{Guid.NewGuid():N}.koalixbak");
        try
        {
            svc1.Export(f1, null); // «بدون رمز» — no password asked at export time either
            Assert.True(BackupService.IsPasswordless(f1));

            var (s2, svc2) = NewSvc(out _, "imp");
            svc2.Import(f1, null); // restore needs NO password for a passwordless file
            Assert.Equal("koalix-v050", s2.Get("probe.name"));
        }
        finally { File.Delete(f1); }
    }

    [Fact]
    public void Password_files_still_prompt_and_still_reject_wrong_passwords()
    {
        var f2 = Path.Combine(Path.GetTempPath(), $"koalix-{Guid.NewGuid():N}.koalixbak");
        try
        {
            var (s1, svc) = NewSvc(out _, "exp3");
            s1.Set("probe.name", "with-password");
            svc.Export(f2, "secret123");
            Assert.False(BackupService.IsPasswordless(f2));

            var (s2, svc2) = NewSvc(out _, "imp2");
            svc2.Import(f2, "secret123");
            Assert.Equal("with-password", s2.Get("probe.name"));

            var (s3, svc3) = NewSvc(out _, "imp3");
            Assert.ThrowsAny<CryptographicException>(() => svc3.Import(f2, "wrong"));
            Assert.Null(s3.Get("probe.name")); // a wrong password restores NOTHING (the import aborts atomically)
        }
        finally { File.Delete(f2); }
    }

    [Fact]
    public void Garbage_file_is_rejected_as_bad_file()
    {
        var (_, svc) = NewSvc(out _, "imp4");
        var f = Path.Combine(Path.GetTempPath(), $"koalix-{Guid.NewGuid():N}.koalixbak");
        try
        {
            File.WriteAllBytes(f, "definitely not a backup"u8.ToArray());
            Assert.False(BackupService.IsPasswordless(f));
            Assert.Throws<InvalidDataException>(() => svc.Import(f, null));
        }
        finally { File.Delete(f); }
    }
}
