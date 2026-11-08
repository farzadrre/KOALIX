using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Koalix.Tunneling;

/// <summary>
/// v0.4.28 — the DNS-poisoning defence («همهٔ کانفیگ‌ها وصل نمی‌شن» — the externally-audited root cause).
///
/// KOALIX's engines dial the proxy server by NAME through the Windows system resolver — and on Iranian
/// ISPs that resolver is regularly poisoned (the field log showed the worker domain answering
/// 188.114.98.0 / 188.114.99.0 — "last octet zero" addresses real DNS never returns — while DoH for the
/// same domain answered the genuine Cloudflare IPs). The result is the classic on/off behaviour:
/// sometimes the poisoned cache expires and the link works, then it comes back and every dial dies with EOF.
///
/// The guard resolves the server domain TWICE at connect time — once through the system resolver and once
/// over DoH (IP-based resolvers, so no bootstrap is needed and the queries cannot be tampered with). When
/// the answers disagree, the engine dials the DoH-confirmed IP while the domain stays in SNI/Host, so TLS
/// and websocket handshakes are byte-for-byte identical to a normal connection. When the answers agree
/// (or DoH is unreachable) nothing changes at all — the guard must never make a working connection worse.
/// </summary>
public sealed record PoisonVerdict(
    string Domain,
    IReadOnlyList<string> SystemIps,
    IReadOnlyList<string> DohIps,
    bool PoisonSuspected,
    string? DialIp,
    string Report)
{
    public static PoisonVerdict None(string domain, string report) =>
        new(domain, [], [], false, null, report);
}

public static class PoisonGuard
{
    /// <summary>
    /// v0.4.31 — the sinkhole kill-net: destination pools the field logs caught the ISP answering with, which the
    /// tunnel must NEVER carry (log 355, 19:53 session: Chrome dialled <c>[2001:4188:2:600:10:10:34:35]:443</c>
    /// 104 times through a perfectly healthy TUN — every poisoned lookup leaks past the TUN when strict_route is
    /// off, and each dial then hangs inside the tunnel until timeout, so Google/YouTube "never load" while the
    /// ip-check site does). The generated config gets a FIRST route rule that REJECTS these prefixes, so a
    /// poisoned answer costs the browser one instant ECONNREFUSED — it falls back to the other address family
    /// or re-resolves through the tunneled DoH instead of hanging. Works with every stack, independent of
    /// strict_route/WFP (belt and braces for the v0.4.30 DNS-leak fix).
    /// </summary>
    public static readonly string[] SinkholeCidrs =
    [
        "10.10.34.0/24",          // the IPv4 sinkhole pool (10.10.34.35 and friends — RFC1918 space the ISP answers with)
        "2001:4188:2:600::/64",   // the same sinkhole as AAAA: the ISP's real /48 prefix with 10.10.34.35 embedded in the low 64 bits
    ];

    /// <summary>True when ip falls inside a <see cref="SinkholeCidrs"/> pool (pure, unit-testable).</summary>
    public static bool IsSinkholeAddress(string? ip)
    {
        if (ip is null || !IPAddress.TryParse(ip, out var a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 && b[1] == 10 && b[2] == 34;
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();
            // 2001:0418:0002:0600 — the observed poison /64
            return b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x41 && b[3] == 0x88
                && b[4] == 0x00 && b[5] == 0x02 && b[6] == 0x06 && b[7] == 0x00;
        }
        return false;
    }

    /// <summary>IP-based DoH endpoints — no DNS needed to reach them, and their answers cannot be tampered with.</summary>
    public static readonly string[] DohUrls =
    [
        "https://8.8.8.8/dns-query",
        "https://9.9.9.9/dns-query",
        "https://1.1.1.1/dns-query",
    ];

    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SystemTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DohTimeout = TimeSpan.FromSeconds(4);

    private static readonly ConcurrentDictionary<string, (PoisonVerdict V, DateTimeOffset At)> Cache = new();

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false, // a stale WinINet proxy must never carry the DoH check (v0.4.10 lesson)
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    { Timeout = DohTimeout };

    /// <summary>Strict literal-IP test (IPAddress.TryParse alone accepts "188.114" as a numeric address).</summary>
    public static bool IsLiteralIp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Contains(':')) return IPAddress.TryParse(s, out _);
        var parts = s.Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, out _));
    }

    /// <summary>
    /// The pure decision (unit-testable, no network): compare the two answer sets.
    /// Overlap → not poisoned, dial by name (zero behaviour change). Disjoint → dial the DoH-confirmed IP.
    /// System failed but DoH answered → the DoH IP is strictly more trustworthy, use it.
    /// DoH unreachable → no verdict, keep the system answer (never make things worse).
    /// </summary>
    public static PoisonVerdict Evaluate(string domain, IReadOnlyList<string> systemIps, IReadOnlyList<string> dohIps)
    {
        var sys = systemIps.Where(IsLiteralIp).Distinct().ToList();
        var doh = dohIps.Where(IsLiteralIp).Distinct().ToList();
        if (doh.Count == 0)
            return new(domain, sys, doh, false, null, "DoH unavailable — keeping the system resolver's answer (guard cannot judge)");
        if (sys.Count == 0)
            return new(domain, sys, doh, true, doh[0],
                $"system DNS failed to answer, DoH resolved {doh[0]} — dialing the DoH-confirmed IP with the domain kept as SNI/Host");
        if (sys.Intersect(doh).Any())
            return new(domain, sys, doh, false, null, "system DNS and DoH agree — no poisoning suspected, dialing by name");
        var signature = HasSuspiciousMarker(sys)
            ? "system answers carry a poisoning signature (a .0/.255 last octet real DNS never returns)"
            : "system answers are completely different from DoH (rotation or poisoning)";
        return new(domain, sys, doh, true, doh[0],
            $"{signature}: system=[{string.Join(", ", sys)}] DoH=[{string.Join(", ", doh)}] — dialing {doh[0]} with the domain kept as SNI/Host");
    }

    /// <summary>True when an answer ends in .0 or .255 — network/broadcast octets are a classic injected-answer marker.</summary>
    public static bool HasSuspiciousMarker(IEnumerable<string> ips) => ips.Any(ip =>
        !ip.Contains(':') && ip.Split('.') is [_, _, _, var last] && last is "0" or "255");

    /// <summary>Applies a verdict to a node: swaps the dial address for the clean IP while SNI/Host keep the domain.</summary>
    public static ProxyNode Apply(ProxyNode n, PoisonVerdict? verdict)
    {
        if (verdict is not { PoisonSuspected: true, DialIp: { } ip }) return n;
        if (IsLiteralIp(n.Server)) return n; // already dialing an IP — nothing to fix
        var withIp = n with { Server = ip };
        // The certificate is issued for the DOMAIN: when the node has no explicit SNI/Host, the substituted
        // IP would otherwise become the SNI (n.Sni ?? n.Host ?? n.Server) and every TLS handshake would fail.
        if (withIp.Sni is null && withIp.Host is null) withIp = withIp with { Sni = verdict.Domain };
        return withIp;
    }

    /// <summary>Checks a server (cached per domain for CacheTtl). Safe to call for every connect.</summary>
    public static async Task<PoisonVerdict> CheckAsync(string server, bool enabled, CancellationToken ct)
    {
        if (!enabled) return PoisonVerdict.None(server, "guard disabled by setting");
        if (IsLiteralIp(server)) return PoisonVerdict.None(server, "server is already an IP — nothing to check");
        if (Cache.TryGetValue(server, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl) return hit.V;
        var v = await ResolveAsync(server, ct);
        Cache[server] = (v, DateTimeOffset.UtcNow);
        return v;
    }

    /// <summary>Resolves through the system resolver AND DoH in parallel, then evaluates.</summary>
    public static async Task<PoisonVerdict> ResolveAsync(string domain, CancellationToken ct)
    {
        var systemTask = SystemIpsAsync(domain, ct);
        var dohTask = DohIpsAsync(domain, ct);
        await Task.WhenAll(systemTask, dohTask);
        return Evaluate(domain, systemTask.Result, dohTask.Result);
    }

    /// <summary>
    /// Cheap DoH availability probe (cached): used for profile kinds where there is a single JSON with many
    /// outbound names (panel configs) — the generated/merged config then routes outbound-server-name
    /// resolution through a DoH server with detour=direct instead of the hijackable UDP resolver.
    /// </summary>
    public static async Task<bool> DohReachableAsync(CancellationToken ct)
    {
        if (_dohOkAt is { } at && DateTimeOffset.UtcNow - at < CacheTtl) return _dohOk == true;
        var ips = await DohIpsAsync("www.google.com", ct);
        _dohOk = ips.Count > 0;
        _dohOkAt = DateTimeOffset.UtcNow;
        return _dohOk == true;
    }

    private static bool? _dohOk;
    private static DateTimeOffset? _dohOkAt;

    private static async Task<List<string>> SystemIpsAsync(string domain, CancellationToken ct)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(domain, ct).WaitAsync(SystemTimeout, ct);
            return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).Distinct().ToList();
        }
        catch (TimeoutException) { return []; }
        catch (SocketException) { return []; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
    }

    private static async Task<List<string>> DohIpsAsync(string domain, CancellationToken ct)
    {
        var query = BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), domain, qtype: 1);
        async Task<string[]?> TryOne(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new ByteArrayContent(query);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                return ParseARecords(await resp.Content.ReadAsByteArrayAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; } // resolver down/blocked — the parallel ones decide
        }
        try
        {
            var results = await Task.WhenAll(DohUrls.Select(TryOne));
            return results.Where(r => r is { Length: > 0 }).SelectMany(r => r!).Distinct().ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
    }

    /// <summary>Minimal RFC 1035 wire query for one A record (KOALIX.Dns is not referenced from this project).</summary>
    public static byte[] BuildQuery(ushort id, string domain, ushort qtype)
    {
        var labels = domain.TrimEnd('.').Split('.');
        if (labels.Any(l => l.Length is 0 or > 63)) throw new ArgumentException("Invalid domain", nameof(domain));
        var buf = new byte[12 + labels.Sum(l => l.Length + 1) + 1 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(buf, id);
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

    /// <summary>Extracts the A records of a DNS wire response (handles name compression pointers). null = malformed.</summary>
    public static string[]? ParseARecords(byte[] msg)
    {
        if (msg.Length < 12) return null;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
        var pos = 12;
        for (var i = 0; i < questions; i++)
        {
            if (!SkipName(msg, ref pos)) return null;
            pos += 4; // qtype + qclass
        }
        var ips = new List<string>();
        for (var i = 0; i < answers; i++)
        {
            if (!SkipName(msg, ref pos) || pos + 10 > msg.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos + 8));
            pos += 10;
            if (type == 1 && rdlen == 4 && pos + 4 <= msg.Length)
                ips.Add($"{msg[pos]}.{msg[pos + 1]}.{msg[pos + 2]}.{msg[pos + 3]}");
            pos += rdlen;
        }
        return ips.ToArray();
    }

    private static bool SkipName(byte[] msg, ref int pos)
    {
        while (true)
        {
            if (pos >= msg.Length) return false;
            var len = msg[pos];
            if (len == 0) { pos++; return true; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return true; } // compression pointer: name ends here
            pos += 1 + len;
        }
    }
}
