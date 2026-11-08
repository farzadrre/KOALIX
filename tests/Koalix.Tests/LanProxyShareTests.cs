using System.Net;
using System.Net.Sockets;
using System.Text;
using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.36 — the LAN proxy relay: only private/link-local sources, optional Basic password (any user name),
/// the Proxy-Authorization header never reaches the engine, 503 while disconnected.
/// </summary>
public class LanProxyShareTests
{
    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("192.168.137.5", true)]   // Windows mobile hotspot
    [InlineData("10.0.0.7", true)]
    [InlineData("172.16.4.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("169.254.3.3", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:192.168.1.9", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]     // CGNAT is not "your" LAN
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_lan_sources_are_accepted(string ip, bool ok) =>
        Assert.Equal(ok, LanProxyShare.IsLanAddress(IPAddress.Parse(ip)));

    private static string Basic(string user, string pass) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void No_password_passes_everything_and_still_strips_the_header()
    {
        var head = "CONNECT a.com:443 HTTP/1.1\r\nHost: a.com:443\r\nProxy-Authorization: Basic " + Basic("x", "y");
        Assert.True(LanProxyShare.TryAuthorize(head, null, out var fwd));
        Assert.DoesNotContain("Proxy-Authorization", fwd);
        Assert.StartsWith("CONNECT a.com:443 HTTP/1.1\r\nHost: a.com:443", fwd);
    }

    [Fact]
    public void Password_must_match_any_user_name_is_fine()
    {
        var baseHead = "GET http://a.com/ HTTP/1.1\r\nHost: a.com";
        Assert.False(LanProxyShare.TryAuthorize(baseHead, "s3cret", out _));
        Assert.False(LanProxyShare.TryAuthorize(baseHead + "\r\nProxy-Authorization: Basic " + Basic("ps", "wrong"), "s3cret", out _));
        Assert.False(LanProxyShare.TryAuthorize(baseHead + "\r\nProxy-Authorization: Basic !!notbase64", "s3cret", out _));
        Assert.True(LanProxyShare.TryAuthorize(baseHead + "\r\nproxy-authorization: Basic " + Basic("anything", "s3cret"), "s3cret", out var fwd));
        Assert.Equal(baseHead, fwd);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static async Task<string> SendAsync(int port, string request)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sb = new StringBuilder();
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0) sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return sb.ToString();
    }

    [Fact]
    public async Task Relays_to_upstream_without_the_auth_header_and_rejects_bad_passwords()
    {
        // fake engine mixed port: records what it received and answers 200.
        var up = new TcpListener(IPAddress.Loopback, 0); up.Start();
        var upPort = ((IPEndPoint)up.LocalEndpoint).Port;
        var received = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using var c = await up.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[4096];
            var n = await s.ReadAsync(buf);
            received.TrySetResult(Encoding.ASCII.GetString(buf, 0, n));
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });

        var port = FreePort();
        await using var share = new LanProxyShare(port, "pw", () => upPort);
        share.Start();

        var denied = await SendAsync(port, "GET http://a.com/ HTTP/1.1\r\nHost: a.com\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 407", denied);

        var ok = await SendAsync(port, $"GET http://a.com/ HTTP/1.1\r\nHost: a.com\r\nProxy-Authorization: Basic {Basic("u", "pw")}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", ok);
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("Proxy-Authorization", got);
        Assert.EndsWith("\r\n\r\n", got);
        up.Stop();
    }

    [Fact]
    public async Task Answers_503_while_koalix_is_disconnected()
    {
        var port = FreePort();
        await using var share = new LanProxyShare(port, null, () => null);
        share.Start();
        var r = await SendAsync(port, "GET http://a.com/ HTTP/1.1\r\nHost: a.com\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 503", r);
    }
}
