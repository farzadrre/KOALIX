using System.Diagnostics;
using Koalix.Core;
using Koalix.Tunneling;
using Koalix.WireGuard;
using Xunit.Abstractions;

namespace Koalix.Tests;

/// <summary>Real-delay ping through a local Shadowsocks server run by the real sing-box binary (needs internet).</summary>
public class LatencyTesterTests(ITestOutputHelper output)
{
    private static readonly string? SingBox = Environment.GetEnvironmentVariable("KOALIX_SINGBOX_BIN");
    private static bool Network => Environment.GetEnvironmentVariable("KOALIX_NETWORK_TESTS") == "1";

    /// <summary>v0.4.19 (user: «تست برای اوپن وی پی ان خرابه و تست نمیگیره همرو خراب نشون میده»): .ovpn profiles
    /// used to fall through ServerOf with "no server" so EVERY OpenVPN row showed ×. The TCP-ping fallback now
    /// targets the profile's first TCP remote (UDP-only profiles: the first remote).</summary>
    [Fact]
    public async Task OpenVpn_profile_tcp_ping_targets_the_first_tcp_remote()
    {
        var port = EnginePaths.FreeTcpPort();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        try
        {
            var ovpn = $"""
                client
                dev tun
                proto udp
                remote 203.0.113.9 443
                remote 127.0.0.1 {port} tcp
                """;
            var alive = new TunnelProfile { Id = 7, Name = "ovpn", Engine = EngineKind.OpenVpn, Source = ProfileSource.OpenVpnConf, Content = ovpn };
            var r = await ProfileLatencyTester.TcpPingAsync(alive, CancellationToken.None);
            output.WriteLine($"alive: {r.Ms} ms ({r.Method}, {r.Error})");
            Assert.NotNull(r.Ms);                                     // reached the listener: remote extracted, no more "no server"
            Assert.Equal(LatencyMethod.TcpPing, r.Method);
        }
        finally { listener.Stop(); }

        var deadPort = EnginePaths.FreeTcpPort();
        var dead = new TunnelProfile
        {
            Id = 8, Name = "dead ovpn", Engine = EngineKind.OpenVpn, Source = ProfileSource.OpenVpnConf,
            Content = $"client\r\ndev tun\r\nproto udp\r\nremote 127.0.0.1 {deadPort}\r\n",
        };
        var r2 = await ProfileLatencyTester.TcpPingAsync(dead, CancellationToken.None);
        output.WriteLine($"dead: {r2.Ms} ms ({r2.Error})");
        Assert.Null(r2.Ms);
        Assert.NotEqual("no server", r2.Error);                        // the remote was found; it honestly failed to connect
    }

    [Fact]
    public async Task Real_delay_through_local_shadowsocks_server_and_failure_for_dead_server()
    {
        if (SingBox is null || !Network) { output.WriteLine("skipped (needs KOALIX_SINGBOX_BIN + KOALIX_NETWORK_TESTS=1)"); return; }
        var root = Path.Combine(Path.GetTempPath(), "koalix-lat-" + Guid.NewGuid().ToString("N"));
        var paths = new EnginePaths(root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SingBoxExe)!);
        File.Copy(SingBox, paths.SingBoxExe);
        File.SetUnixFileMode(paths.SingBoxExe, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);

        var port = EnginePaths.FreeTcpPort();
        var srvCfg = Path.Combine(root, "srv.json");
        File.WriteAllText(srvCfg, $$"""
        {"log":{"level":"error"},"inbounds":[{"type":"shadowsocks","listen":"127.0.0.1","listen_port":{{port}},"method":"aes-128-gcm","password":"koalixtest"}],"outbounds":[{"type":"direct"}]}
        """);
        using var srv = Process.Start(new ProcessStartInfo(SingBox, ["run", "-c", srvCfg]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            await Task.Delay(800);
            var ss = "ss://" + Convert.ToBase64String("aes-128-gcm:koalixtest"u8.ToArray()) + $"@127.0.0.1:{port}#local";
            var dead = "ss://" + Convert.ToBase64String("aes-128-gcm:koalixtest"u8.ToArray()) + $"@127.0.0.1:{EnginePaths.FreeTcpPort()}#dead";
            var profiles = new[]
            {
                new TunnelProfile { Id = 1, Name = "local", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = ss },
                new TunnelProfile { Id = 2, Name = "dead", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = dead },
            };
            var res = await new ProfileLatencyTester(paths).TestAsync(profiles, null, CancellationToken.None);
            foreach (var r in res) output.WriteLine($"{r.ProfileId}: {r.Ms} {r.Method} {r.Error}");
            Assert.NotNull(res.Single(r => r.ProfileId == 1).Ms);
            Assert.Equal(LatencyMethod.RealDelay, res.Single(r => r.ProfileId == 1).Method);
            Assert.Null(res.Single(r => r.ProfileId == 2).Ms);
        }
        finally { try { srv.Kill(true); } catch { } try { Directory.Delete(root, true); } catch { } }
    }
}
