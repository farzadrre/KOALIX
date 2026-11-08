using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.Tunneling;
using Koalix.Xray;

namespace Koalix.Tests;

/// <summary>
/// v0.4.30 postmortem regression guard — the Windows DNS-leak hole.
///
/// THE FIELD REPORT (log 354, KOALIX 0.4.29): «وصل می‌کنم، ipnumberia.com میاد بالا و آی‌پی
/// خارجی می‌زنه، ولی گوگل و یوتیوب و هیچ سایت خارجی باز نمی‌شه؛ سایتای داخلی میاد». The log
/// pinned it verbatim: Chrome dialed <c>[2001:4188:2:600:10:10:34:35]:443</c> — the ISP's poisoned
/// AAAA with the 10.10.34.35 sinkhole embedded — 164 times through a perfectly healthy TUN, while
/// unblocked domains (ipnumberia, Iranian sites) resolved clean and loaded through the tunnel.
/// Root cause: the v0.4.15 decision to ship <c>strict_route</c> OFF. Without it sing-box installs
/// no WFP rules, so Windows' "ordinary DNS requests" (svchost → the Wi-Fi adapter's ISP resolvers)
/// BYPASS the TUN — the ISP answers blocked domains with the sinkhole, browsers dial the dead
/// destination through the tunnel, and every foreign site times out. The fix is three layers:
/// (1) strict_route defaults ON (explicit "0" still disables it) on the engine mapping AND the
/// settings VM, (2) a one-time migration flips every existing installation once (tun.strict.migrated)
/// — a user who later switches it off is never re-flipped, (3) the OS resolver cache is flushed the
/// moment the tunnel is up and right after teardown, so poisoned entries cannot outlive the
/// topology they were learned in.
/// These tests pin all three layers plus the generated sing-box strict_route flag.
/// </summary>
public class DnsLeakGuardTests
{
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"repo root not found above {AppContext.BaseDirectory} — run tests from the source tree");
        var src = Path.Combine(dir.FullName, "src");
        Assert.True(Directory.Exists(src), $"src not found at {src}");
        return src;
    }

    private static string Read(string project, string file) =>
        File.ReadAllText(Path.Combine(SrcDir, project, file));

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startMarker} not found");
        var end = source.IndexOf(endMarker, start + 1, StringComparison.Ordinal);
        Assert.True(end > start, $"{endMarker} not found after {startMarker}");
        return source[start..end];
    }

    // ---- Layer 1: strict_route defaults ON --------------------------------------------

    [Fact]
    public void AppHost_maps_tun_strict_with_default_ON()
    {
        var appHost = Read("Koalix.App", Path.Combine("Services", "AppHost.cs"));
        // The v0.4.15–0.4.29 form (== "1", default OFF) is the exact bug — it must never come back.
        Assert.DoesNotMatch(@"StrictRoute\s*=\s*settings\.Get\(""tun\.strict""\)\s*==\s*""1""", appHost);
        Assert.Matches(@"StrictRoute\s*=\s*settings\.Get\(""tun\.strict""\)\s*!=\s*""0""", appHost);
    }

    [Fact]
    public void SettingsViewModel_loads_and_resets_strict_route_ON()
    {
        var vm = Read("Koalix.App", Path.Combine("ViewModels", "SettingsViewModel.cs"));
        Assert.Matches(@"_strictRoute\s*=\s*s\.Get\(""tun\.strict""\)\s*!=\s*""0""", vm);
        // ResetDefaults ships it ON; the old default-off must not reappear anywhere.
        Assert.Contains("StrictRoute = true", vm);
        Assert.DoesNotContain("StrictRoute = false", vm);
    }

    // ---- Layer 2: the one-time migration ----------------------------------------------

    [Fact]
    public void AppHost_ships_the_one_time_strict_route_migration()
    {
        var appHost = Read("Koalix.App", Path.Combine("Services", "AppHost.cs"));
        // The guard key must be read (gate) and written (latch) — a migration that cannot run
        // exactly once either never fires (old settings stay off) or fires forever (overrides
        // the user's later choice on every launch).
        Assert.Contains(@"Get(""tun.strict.migrated"")", appHost);
        Assert.Contains(@"Set(""tun.strict.migrated"", ""1"")", appHost);
        Assert.Contains(@"Set(""tun.strict"", ""1"")", appHost);
    }

    // ---- Layer 3: the DNS cache flush --------------------------------------------------

    [Fact]
    public void ConnectionManager_flushes_the_dns_cache_on_tunnel_up_and_on_teardown()
    {
        var cm = Read("Koalix.Tunneling", "ConnectionManager.cs");

        // The flush is a real ipconfig /flushdns, bounded and failure-proof.
        Assert.Contains("ipconfig.exe", cm);
        Assert.Contains("/flushdns", cm);
        Assert.Matches(@"WaitForExit\(3000\)", cm);

        // Tunnel-up: inside ConnectAsync, AFTER the engine start (the TUN must exist before the
        // flush — otherwise new lookups race back into the still-uncached, still-poisonable world).
        var connect = MethodBody(cm, "public async Task<bool> ConnectAsync", "private async Task<bool> WaitHealthyAsync");
        var startedAt = connect.IndexOf("await engine.StartAsync(profile, policy, ct);", StringComparison.Ordinal);
        Assert.True(startedAt >= 0, "ConnectAsync no longer starts the engine at the expected site");
        var flushedAt = connect.IndexOf("FlushDnsCache();", StringComparison.Ordinal);
        Assert.True(flushedAt > startedAt, "the DNS flush must come after engine.StartAsync (TUN up) in ConnectAsync");

        // Teardown: a clean cache for the direct world too.
        var teardown = MethodBody(cm, "private async Task TeardownAsync", "public async ValueTask DisposeAsync");
        Assert.Contains("FlushDnsCache();", teardown);
    }

    // ---- The generated sing-box configs carry the flag --------------------------------

    private static ProxyNode Node() => new()
    {
        Protocol = ProxyProtocol.VLess, Name = "ZEUS", Server = "demo7sample0.samplezeus.workers.dev",
        Port = 443, Uuid = "50414e45-4c5f-5a45-5553-aaaabbbbcccc", Transport = "ws", Security = "tls",
        Path = "/stream", Host = "demo7sample0.samplezeus.workers.dev", Sni = "demo7sample0.samplezeus.workers.dev",
    };

    private static readonly SplitPolicy Policy = new(SplitMode.FullTunnel, []);

    private static bool TunStrictRoute(JsonObject root)
    {
        var tun = ((root["inbounds"] as JsonArray) ?? []).OfType<JsonObject>()
            .First(i => (string?)i["type"] == "tun");
        return (bool?)tun["strict_route"] ?? false;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generated_configs_carry_the_strict_route_flag(bool strict)
    {
        var o = new SingBoxBuildOptions { HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s", StrictRoute = strict };
        Assert.Equal(strict, TunStrictRoute(SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(Node()), false, Policy, o)));
    }

    [Fact]
    public void Merged_panel_configs_carry_the_strict_route_flag()
    {
        var user = JsonNode.Parse("""
        {
          "outbounds": [{ "type": "vless", "tag": "proxy", "server": "panel.example.com", "server_port": 443, "uuid": "50414e45-4c5f-5a45-5553-aaaabbbbcccc" }],
          "route": { "final": "proxy" }
        }
        """)!.AsObject();
        var o = new SingBoxBuildOptions { HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s", StrictRoute = true };
        Assert.True(TunStrictRoute(SingBoxConfigBuilder.MergeUserConfig(user, "proxy", Policy, o)));
    }
}
