using System.Buffers.Binary;
using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.28 — the DNS-poisoning shield (external audit: «همهٔ کانفیگ‌ها وصل نمی‌شن» — KOALIX dials the proxy
/// server by name through the Windows resolver, and Iranian ISPs poison exactly those answers; the field log
/// showed the worker domain answering 188.114.98.0/.99 — .0-octet addresses real DNS never returns — while
/// DoH answered the genuine Cloudflare IPs). These tests lock the decision logic: overlap → nothing changes,
/// disagreement → dial the DoH IP with the domain kept as SNI/Host, DoH dead → never make things worse.
/// </summary>
public class PoisonGuardTests
{
    private const string Domain = "demo7sample0.samplezeus.workers.dev";

    [Fact]
    public void Disjoint_answers_are_judged_poisoned_and_the_doh_ip_wins()
    {
        // the exact shape of the field report: system answers the .0 network addresses, DoH the real ones
        var v = PoisonGuard.Evaluate(Domain, ["188.114.98.0", "188.114.99.0"], ["104.21.29.126", "172.67.149.2"]);
        Assert.True(v.PoisonSuspected);
        Assert.Equal("104.21.29.126", v.DialIp);
        Assert.Contains("188.114.98.0", v.Report); // the honest report names what the system said
    }

    [Fact]
    public void Overlapping_answers_change_nothing()
    {
        var v = PoisonGuard.Evaluate("example.com", ["1.2.3.4", "5.6.7.8"], ["5.6.7.8", "9.9.9.9"]);
        Assert.False(v.PoisonSuspected);
        Assert.Null(v.DialIp);
    }

    [Fact]
    public void DoH_unavailable_never_changes_anything()
    {
        var v = PoisonGuard.Evaluate(Domain, ["188.114.98.0"], []);
        Assert.False(v.PoisonSuspected);
        Assert.Null(v.DialIp);
    }

    [Fact]
    public void System_failure_with_working_doh_dials_the_doh_answer()
    {
        var v = PoisonGuard.Evaluate(Domain, [], ["104.21.29.126"]);
        Assert.True(v.PoisonSuspected);
        Assert.Equal("104.21.29.126", v.DialIp);
    }

    [Fact]
    public void Last_octet_zero_or_255_is_a_poisoning_signature()
    {
        Assert.True(PoisonGuard.HasSuspiciousMarker(["188.114.98.0"]));
        Assert.True(PoisonGuard.HasSuspiciousMarker(["10.0.0.255"]));
        Assert.False(PoisonGuard.HasSuspiciousMarker(["104.21.29.126"]));
        Assert.False(PoisonGuard.HasSuspiciousMarker(["188.114.98.7"]));
    }

    [Fact]
    public void Apply_substitutes_the_ip_and_keeps_the_domain_as_sni()
    {
        var n = new ProxyNode { Protocol = ProxyProtocol.VLess, Name = "ZEUS", Server = Domain, Port = 443, Uuid = "50414e45-4c5f-5a45-5553-aaaabbbbcccc" };
        var v = PoisonGuard.Evaluate(Domain, ["188.114.98.0"], ["104.21.29.126"]);
        var fixedNode = PoisonGuard.Apply(n, v);
        Assert.Equal("104.21.29.126", fixedNode.Server);
        // the certificate is issued for the DOMAIN — the SNI must stay the domain or every TLS handshake fails
        Assert.Equal(Domain, fixedNode.Sni);
    }

    [Fact]
    public void Apply_keeps_an_existing_sni_and_host()
    {
        var n = new ProxyNode { Protocol = ProxyProtocol.VLess, Name = "ZEUS", Server = Domain, Port = 443, Uuid = "u", Sni = "cdn.example.com", Host = "cdn.example.com" };
        var v = PoisonGuard.Evaluate(Domain, ["188.114.98.0"], ["104.21.29.126"]);
        var fixedNode = PoisonGuard.Apply(n, v);
        Assert.Equal("104.21.29.126", fixedNode.Server);
        Assert.Equal("cdn.example.com", fixedNode.Sni);
        Assert.Equal("cdn.example.com", fixedNode.Host);
    }

    [Fact]
    public void Apply_is_a_noop_without_a_verdict_or_for_ip_servers()
    {
        var n = new ProxyNode { Protocol = ProxyProtocol.VLess, Name = "x", Server = "1.2.3.4", Port = 443, Uuid = "u" };
        Assert.Same(n, PoisonGuard.Apply(n, null));
        var v = PoisonGuard.Evaluate("1.2.3.4", [], []);
        Assert.Same(n, PoisonGuard.Apply(n, v)); // no DoH answers → no substitution
    }

    [Fact]
    public void Literal_ip_check_is_strict()
    {
        Assert.True(PoisonGuard.IsLiteralIp("104.21.29.126"));
        Assert.True(PoisonGuard.IsLiteralIp("2606:4700:4700::1111"));
        Assert.False(PoisonGuard.IsLiteralIp("188.114"));     // IPAddress.TryParse alone accepts this!
        Assert.False(PoisonGuard.IsLiteralIp("example.com"));
        Assert.False(PoisonGuard.IsLiteralIp(""));
        Assert.False(PoisonGuard.IsLiteralIp(null));
    }

    [Fact]
    public void BuildQuery_produces_a_valid_A_query()
    {
        var q = PoisonGuard.BuildQuery(0x1234, "example.com", 1);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(q));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4))); // QDCOUNT
        // answer ourselves: walk the question and confirm the A/IN tail
        Assert.Equal(new byte[] { 0, 1, 0, 1 }, q[^4..]);
        Assert.Contains(q, b => b == (byte)'e');
    }

    [Fact]
    public void ParseARecords_reads_answers_with_and_without_compression()
    {
        // build a minimal wire response: header + question + 2 answers (one compressed name, one inline)
        var q = PoisonGuard.BuildQuery(0x4242, "demo.samplezeus.workers.dev", 1);
        var m = new List<byte>(q);
        m[2] = 0x81; m[3] = 0x80;                       // QR=1, RD=1, RA=1
        m[6] = 0; m[7] = 2;                             // ANCOUNT = 2
        m.AddRange(new byte[] { 0xC0, 12 });            // name → pointer to question name at offset 12
        m.AddRange(new byte[] { 0, 1, 0, 1 });          // type A, class IN
        m.AddRange(new byte[] { 0, 0, 0, 60 });         // TTL 60
        m.AddRange(new byte[] { 0, 4 });                // RDLENGTH 4
        m.AddRange(new byte[] { 104, 21, 29, 126 });    // 104.21.29.126
        m.AddRange(new byte[] { 3, 99, 100, 110, 0 });  // inline name "cdn." + root (skip-name exercise)
        m.AddRange(new byte[] { 0, 1, 0, 1 });
        m.AddRange(new byte[] { 0, 0, 0, 60 });
        m.AddRange(new byte[] { 0, 4 });
        m.AddRange(new byte[] { 172, 67, 149, 2 });     // 172.67.149.2
        var parsed = PoisonGuard.ParseARecords(m.ToArray());
        Assert.NotNull(parsed);
        Assert.Equal(new[] { "104.21.29.126", "172.67.149.2" }, parsed);
    }

    [Fact]
    public void ParseARecords_rejects_garbage()
    {
        Assert.Null(PoisonGuard.ParseARecords([]));
        Assert.Null(PoisonGuard.ParseARecords(new byte[] { 1, 2, 3 }));
    }
}
