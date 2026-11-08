using System.Net;
using Koalix.Dns;
using Xunit.Abstractions;

namespace Koalix.Tests;

/// <summary>Real network tests (UDP DNS, TCP DNS, STUN). Enabled with KOALIX_NETWORK_TESTS=1.</summary>
public class NetworkTests(ITestOutputHelper output)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("KOALIX_NETWORK_TESTS") == "1";

    [Fact]
    public async Task Real_dns_query_and_server_test()
    {
        if (!Enabled) { output.WriteLine("skipped"); return; }
        var client = new DnsQueryClient();
        var r = await client.QueryAsync(IPAddress.Parse("1.1.1.1"), "www.google.com", timeout: TimeSpan.FromSeconds(3));
        output.WriteLine($"udp: {r}");
        Assert.True(r.Success && r.ValidResponse && r.AnswerCount > 0, r.ToString());

        var tester = new DnsTester(client, new IcmpProber());
        var res = await tester.TestServerAsync("8.8.8.8", new DnsTestOptions { PingCount = 3, DnsQueries = 4 }, CancellationToken.None);
        output.WriteLine($"8.8.8.8: dns median={res.DnsQuery.MedianMs} jitter={res.DnsQuery.JitterMs} success={res.DnsSuccessRate}% icmp={res.Icmp.Received}/{res.Icmp.Sent}");
        Assert.True(res.DnsWorking);
    }

    [Fact]
    public async Task Real_stun_latency()
    {
        if (!Enabled) { output.WriteLine("skipped"); return; }
        var s = await new StunTester().MeasureAsync("stun.l.google.com:19302", 3, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        output.WriteLine($"stun: {s}");
        Assert.True(s.Received > 0, "no STUN response (UDP may be blocked in this environment)");
    }
}
