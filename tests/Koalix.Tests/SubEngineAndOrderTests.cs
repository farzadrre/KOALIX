using Koalix.Core;
using Koalix.Tunneling;
using Koalix.Infrastructure;

namespace Koalix.Tests;

/// <summary>v0.4.17 — subscription engine preference, panel volume/expiry persistence, and the manual profile order.</summary>
public class SubEngineAndOrderTests
{
    private static (Database db, TunnelProfileRepository profiles, SubscriptionRepository subs) NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("koalix-test").FullName;
        var db = new Database(Path.Combine(dir, "t.db"));
        db.Migrate(dir);
        Assert.Equal(Database.Migrations.Length, db.CurrentVersion()); // v6 shipped in this round (v5 = drag order, v6 = sub engine + volume)
        var protector = new AesFileSecretProtector(Path.Combine(dir, "k"));
        return (db, new TunnelProfileRepository(db, protector), new SubscriptionRepository(db, protector));
    }

    private const string Vless1 =
        "vless://50414e45-4c5f-5a45-5553-ddddeeeeffff@45.131.209.24:443?encryption=none&security=tls&sni=a.example.com&type=ws&host=a.example.com&path=%2F#A";
    private const string Vless2 =
        "vless://50414e45-4c5f-5a45-5553-ddddeeeeffff@45.131.209.25:443?encryption=none&security=tls&sni=b.example.com&type=ws&host=b.example.com&path=%2F#B";

    [Fact]
    public void Subscription_stores_and_reads_back_engine_choice()
    {
        var (_, _, subs) = NewDb();
        var id = subs.Add("My sub", "https://panel.example.com/sub/abc", EngineKind.Xray);
        var read = subs.All().Single();
        Assert.Equal(id, read.Id);
        Assert.Equal(EngineKind.Xray, read.Engine);
        // flip to sing-box
        subs.SetEngine(id, EngineKind.SingBox);
        Assert.Equal(EngineKind.SingBox, subs.All().Single().Engine);
        // and clear back to auto
        subs.SetEngine(id, null);
        Assert.Null(subs.All().Single().Engine);
    }

    [Fact]
    public void Subscription_volume_and_expiry_persist()
    {
        var (_, _, subs) = NewDb();
        var id = subs.Add("S", "https://panel.example.com/sub/x");
        subs.SetInfo(id, new SubUserInfo { UploadBytes = 1000, DownloadBytes = 3000, TotalBytes = 5000, ExpireUnix = 2000000000 });
        var s = subs.All().Single();
        Assert.Equal(1000, s.UploadBytes);
        Assert.Equal(3000, s.DownloadBytes);
        Assert.Equal(5000, s.TotalBytes);
        Assert.Equal(2000000000, s.ExpireUnix);
        Assert.False(s.VolumeExhausted); // 1000+3000 = 4000 < 5000 — not exhausted yet
    }

    [Fact]
    public void Subscription_info_flags_dead_accounts()
    {
        var (_, _, subs) = NewDb();
        var id = subs.Add("S", "https://panel.example.com/sub/x");
        // exhausted volume
        subs.SetInfo(id, new SubUserInfo { UploadBytes = 4000, DownloadBytes = 2000, TotalBytes = 5000 });
        Assert.True(subs.All().Single().VolumeExhausted);
        // expired account
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        subs.SetInfo(id, new SubUserInfo { ExpireUnix = past });
        Assert.True(subs.All().Single().Expired);
    }

    [Fact]
    public void Profile_manual_order_persists_and_appends()
    {
        var (db, profiles, _) = NewDb();
        var a = profiles.Add(new TunnelProfile { Name = "A", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = Vless1 });
        var b = profiles.Add(new TunnelProfile { Name = "B", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = Vless2 });
        // insertion order first (A then B — the Add assigns ascending sort_order)
        Assert.Equal(["A", "B"], profiles.All().Select(p => p.Name));

        // drag B in front of A (Reorder writes 10, 20, 30 …)
        profiles.Reorder([b, a]);
        Assert.Equal(["B", "A"], profiles.All().Select(p => p.Name));

        // a NEW profile lands at the END of the manual order, not in front
        var c = profiles.Add(new TunnelProfile { Name = "C", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = Vless2 });
        Assert.Equal(["B", "A", "C"], profiles.All().Select(p => p.Name));

        // bulk engine switch persists for many rows in one go
        profiles.SetEngineMany([a, b, c], EngineKind.Xray);
        Assert.All(profiles.All(), p => Assert.Equal(EngineKind.Xray, p.Engine));

        // bulk delete
        profiles.DeleteMany([a, c]);
        Assert.Equal(["B"], profiles.All().Select(p => p.Name));
        _ = db;
    }
}
