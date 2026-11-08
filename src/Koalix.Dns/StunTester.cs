using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Koalix.Dns;

/// <summary>
/// UDP latency test using the STUN Binding request/response protocol (RFC 5389).
/// Only endpoints that explicitly implement STUN are used — arbitrary UDP endpoints are never assumed to reply.
/// </summary>
public sealed class StunTester
{
    public static readonly string[] DefaultServers = ["stun.l.google.com:19302", "stun.cloudflare.com:3478"];
    private const uint MagicCookie = 0x2112A442;

    public async Task<LatencyStats> MeasureAsync(string hostPort, int count, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var (host, port) = SplitHostPort(hostPort, 3478);
        var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct);
        var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.First();
        using var udp = new UdpClient(ip.AddressFamily);
        var samples = new List<double>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var txId = RandomNumberGenerator.GetBytes(12);
            var req = BuildBindingRequest(txId);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var sw = Stopwatch.StartNew();
            try
            {
                await udp.SendAsync(req, new IPEndPoint(ip, port), cts.Token);
                while (true)
                {
                    var resp = await udp.ReceiveAsync(cts.Token);
                    if (IsMatchingResponse(resp.Buffer, txId)) { samples.Add(sw.Elapsed.TotalMilliseconds); break; }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* lost */ }
            catch (SocketException) { }
            if (i < count - 1) await Task.Delay(interval, ct);
        }
        return LatencyStats.From(count, samples);
    }

    public static byte[] BuildBindingRequest(byte[] txId)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(b, 0x0001); // Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), MagicCookie);
        txId.CopyTo(b, 8);
        return b;
    }

    public static bool IsMatchingResponse(ReadOnlySpan<byte> r, ReadOnlySpan<byte> txId) =>
        r.Length >= 20 && BinaryPrimitives.ReadUInt16BigEndian(r) == 0x0101 &&
        BinaryPrimitives.ReadUInt32BigEndian(r[4..]) == MagicCookie && r.Slice(8, 12).SequenceEqual(txId);

    public static (string Host, int Port) SplitHostPort(string s, int defaultPort)
    {
        var idx = s.LastIndexOf(':');
        if (idx > 0 && !s.Contains("::") && int.TryParse(s[(idx + 1)..], out var p)) return (s[..idx], p);
        return (s, defaultPort);
    }
}
