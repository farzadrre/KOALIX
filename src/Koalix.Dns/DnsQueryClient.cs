using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Koalix.Dns;

public enum DnsRcode { NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5 }

public enum DnsTransport { Udp, Tcp }

/// <param name="ValidResponse">A well-formed response with the matching transaction id was received (even NXDOMAIN is a valid response).</param>
public sealed record DnsQueryResult(bool Success, double? ElapsedMs, DnsRcode? Rcode, int AnswerCount, string? Error, DnsTransport Transport = DnsTransport.Udp, bool ValidResponse = false, bool TimedOut = false);

/// <summary>
/// Minimal RFC 1035 DNS client over UDP port 53 used to measure real DNS resolution (independent from ICMP).
/// Sends an A (or AAAA) query and validates transaction id, QR flag and RCODE of the response.
/// </summary>
public sealed class DnsQueryClient
{
    public async Task<DnsQueryResult> QueryAsync(IPAddress server, string domain, bool aaaa = false, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(2);
        var id = (ushort)Random.Shared.Next(0, ushort.MaxValue);
        var packet = BuildQuery(id, domain, aaaa ? (ushort)28 : (ushort)1);
        using var udp = new UdpClient(server.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout.Value);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(packet, new IPEndPoint(server, 53), cts.Token);
            while (true)
            {
                var resp = await udp.ReceiveAsync(cts.Token);
                if (!resp.RemoteEndPoint.Address.Equals(server)) continue;
                var parsed = ParseResponse(resp.Buffer, id);
                if (parsed is null) continue; // not our answer
                sw.Stop();
                var (rcode, answers, truncated) = parsed.Value;
                if (truncated) return await QueryTcpAsync(server, domain, aaaa, timeout.Value, ct);
                var ok = rcode == DnsRcode.NoError && answers > 0;
                return new DnsQueryResult(ok, sw.Elapsed.TotalMilliseconds, rcode, answers, ok ? null : $"RCODE={rcode}, answers={answers}", DnsTransport.Udp, true);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsQueryResult(false, null, null, 0, "Timeout", DnsTransport.Udp, false, true);
        }
        catch (SocketException ex)
        {
            return new DnsQueryResult(false, null, null, 0, ex.SocketErrorCode.ToString());
        }
    }

    /// <summary>
    /// Resolves over UDP and falls back to TCP (RFC 7766) when UDP times out, fails, or the answer is truncated.
    /// Some networks drop UDP/53 to non-local resolvers while TCP/53 still works.
    /// </summary>
    public async Task<DnsQueryResult> QueryWithFallbackAsync(IPAddress server, string domain, bool aaaa, TimeSpan timeout, CancellationToken ct)
    {
        var r = await QueryAsync(server, domain, aaaa, timeout, ct);
        if (r.ValidResponse) return r;
        var t = await QueryTcpAsync(server, domain, aaaa, timeout, ct);
        return t.ValidResponse ? t : r with { Error = $"UDP: {r.Error}; TCP: {t.Error}" };
    }

    public async Task<DnsQueryResult> QueryTcpAsync(IPAddress server, string domain, bool aaaa, TimeSpan timeout, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(0, ushort.MaxValue);
        var q = BuildQuery(id, domain, aaaa ? (ushort)28 : (ushort)1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient(server.AddressFamily) { NoDelay = true };
            await tcp.ConnectAsync(server, 53, cts.Token);
            var stream = tcp.GetStream();
            var framed = new byte[q.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)q.Length);
            q.CopyTo(framed, 2);
            await stream.WriteAsync(framed, cts.Token);
            var lenBuf = new byte[2];
            await stream.ReadExactlyAsync(lenBuf, cts.Token);
            var len = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
            var body = new byte[len];
            await stream.ReadExactlyAsync(body, cts.Token);
            sw.Stop();
            var parsed = ParseResponse(body, id);
            if (parsed is null) return new DnsQueryResult(false, null, null, 0, "Malformed TCP response", DnsTransport.Tcp);
            var (rcode, answers, _) = parsed.Value;
            var ok = rcode == DnsRcode.NoError && answers > 0;
            return new DnsQueryResult(ok, sw.Elapsed.TotalMilliseconds, rcode, answers, ok ? null : $"RCODE={rcode}", DnsTransport.Tcp, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsQueryResult(false, null, null, 0, "Timeout", DnsTransport.Tcp, false, true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException)
        {
            return new DnsQueryResult(false, null, null, 0, ex.Message, DnsTransport.Tcp);
        }
    }

    public static byte[] BuildQuery(ushort id, string domain, ushort qtype)
    {
        var labels = domain.TrimEnd('.').Split('.');
        if (labels.Any(l => l.Length is 0 or > 63)) throw new ArgumentException("Invalid domain", nameof(domain));
        var buf = new byte[12 + labels.Sum(l => l.Length + 1) + 1 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), id);
        buf[2] = 0x01; // RD
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), 1); // QDCOUNT
        var o = 12;
        foreach (var l in labels)
        {
            buf[o++] = (byte)l.Length;
            foreach (var c in l) buf[o++] = (byte)c;
        }
        buf[o++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), qtype); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), 1); // IN
        return buf;
    }

    public static (DnsRcode Rcode, int Answers, bool Truncated)? ParseResponse(ReadOnlySpan<byte> data, ushort expectedId)
    {
        if (data.Length < 12) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(data) != expectedId) return null;
        if ((data[2] & 0x80) == 0) return null; // not a response
        var rcode = (DnsRcode)(data[3] & 0x0F);
        var an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var truncated = (data[2] & 0x02) != 0;
        return (rcode, an, truncated);
    }
}
