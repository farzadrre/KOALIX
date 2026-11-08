using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Koalix.Tunneling;

/// <summary>
/// v0.4.36 — LAN PROXY SHARING («اشتراک پراکسی در شبکهٔ خانه، اختیاری و با رمز»). The engine's own proxy port
/// listens on 127.0.0.1 only, so a console / phone / TV on the same Wi-Fi (or on this PC's hotspot) can never
/// reach it. This relay listens on every interface, accepts ONLY private / link-local / hotspot source addresses,
/// optionally demands an HTTP proxy password (Basic; any user name), and forwards to the engine's loopback mixed
/// port — which the sing-box config always routes through the proxy outbound, whatever the split mode is.
/// Without a password, SOCKS5 clients are passed through as well; with one, only HTTP clients (the kind every
/// console / phone proxy screen speaks) can authenticate.
/// </summary>
public sealed class LanProxyShare : IAsyncDisposable
{
    private readonly Func<int?> _upstreamPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _clients;

    public LanProxyShare(int port, string? password, Func<int?> upstreamPort)
    {
        Port = port;
        Password = string.IsNullOrEmpty(password) ? null : password;
        _upstreamPort = upstreamPort;
    }

    public int Port { get; }
    public string? Password { get; }
    public bool IsRunning => _listener is not null;
    /// <summary>Connections currently being relayed (live count for the UI).</summary>
    public int ActiveClients => Volatile.Read(ref _clients);
    /// <summary>Distinct LAN devices seen since start.</summary>
    public IReadOnlyCollection<string> SeenDevices => _seen.Keys.ToList();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seen = new();

    public event Action<string>? Log;

    /// <summary>Opens the listener; throws SocketException when the port is taken.</summary>
    public void Start()
    {
        if (_listener is not null) return;
        var l = new TcpListener(IPAddress.Any, Port);
        l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        l.Start(64);
        _listener = l;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(l, _cts.Token);
        Log?.Invoke($"LAN share: listening on 0.0.0.0:{Port} (password {(Password is null ? "off" : "on")})");
    }

    public async ValueTask DisposeAsync()
    {
        var l = _listener; _listener = null;
        try { _cts?.Cancel(); } catch { }
        try { l?.Stop(); } catch { }
        _cts?.Dispose(); _cts = null;
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleAsync(c, ct);
        }
    }

    /// <summary>Private IPv4 (10/8, 172.16/12, 192.168/16 — incl. the Windows hotspot's 192.168.137.x), link-local,
    /// CGNAT-free; IPv6 ULA / link-local; IPv4-mapped forms of those. Loopback is allowed too (local testing).</summary>
    public static bool IsLanAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();
            return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }

    /// <summary>Checks the request head's Proxy-Authorization (Basic, any user, password must match) and returns
    /// the head with that header removed (the upstream must never see it). With no password everything passes.</summary>
    public static bool TryAuthorize(string head, string? password, out string forwardedHead)
    {
        var lines = head.Split("\r\n");
        var kept = new List<string>(lines.Length);
        string? supplied = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line["Proxy-Authorization:".Length..].Trim();
                if (v.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var dec = Encoding.UTF8.GetString(Convert.FromBase64String(v[6..].Trim()));
                        var colon = dec.IndexOf(':');
                        supplied = colon >= 0 ? dec[(colon + 1)..] : dec;
                    }
                    catch (FormatException) { }
                }
                continue;
            }
            kept.Add(line);
        }
        forwardedHead = string.Join("\r\n", kept);
        if (password is null) return true;
        if (supplied is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(password));
    }

    private const string Resp407 = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"KOALIX\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    private const string Resp503 = "HTTP/1.1 503 Service Unavailable\r\nContent-Type: text/plain\r\nContent-Length: 26\r\nConnection: close\r\n\r\nKOALIX is not connected.\r\n";

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is null || !IsLanAddress(remote)) return; // internet-facing scanners get a silent close
        Interlocked.Increment(ref _clients);
        try
        {
            client.NoDelay = true;
            var cs = client.GetStream();
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(TimeSpan.FromSeconds(15));

            var buf = new byte[32 * 1024];
            var n = await cs.ReadAsync(buf, headCts.Token);
            if (n <= 0) return;
            byte[] first;
            if (buf[0] == 0x05)
            {
                if (Password is not null) return; // SOCKS can't carry our Basic password — HTTP only when protected
                first = buf[..n];
            }
            else
            {
                // read until the end of the HTTP head
                var total = n;
                int end;
                while ((end = IndexOfHeadEnd(buf, total)) < 0)
                {
                    if (total >= buf.Length) return;
                    var r = await cs.ReadAsync(buf.AsMemory(total), headCts.Token);
                    if (r <= 0) return;
                    total += r;
                }
                var head = Encoding.Latin1.GetString(buf, 0, end);
                if (!TryAuthorize(head, Password, out var fwd))
                {
                    await cs.WriteAsync(Encoding.ASCII.GetBytes(Resp407), ct);
                    return;
                }
                var rest = buf.AsSpan(end + 4, total - end - 4);
                first = [.. Encoding.Latin1.GetBytes(fwd + "\r\n\r\n"), .. rest];
            }

            if (_upstreamPort() is not { } up)
            {
                if (buf[0] != 0x05) await cs.WriteAsync(Encoding.ASCII.GetBytes(Resp503), ct);
                return;
            }
            if (_seen.TryAdd(remote.ToString(), 0)) Log?.Invoke($"LAN share: new device {remote}");

            using var upstream = new TcpClient { NoDelay = true };
            await upstream.ConnectAsync(IPAddress.Loopback, up, ct);
            var us = upstream.GetStream();
            await us.WriteAsync(first, ct);
            using var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var a = cs.CopyToAsync(us, pipeCts.Token);
            var b = us.CopyToAsync(cs, pipeCts.Token);
            await Task.WhenAny(a, b);
            pipeCts.Cancel();
        }
        catch { /* client went away, upstream closed, or cancelled — nothing to report */ }
        finally { Interlocked.Decrement(ref _clients); }
    }

    private static int IndexOfHeadEnd(byte[] b, int len)
    {
        for (var i = 3; i < len; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }
}
