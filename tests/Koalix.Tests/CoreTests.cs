using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.Dns;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.SystemIntegration;
using Koalix.Tunneling;
using Koalix.WireGuard;
using Koalix.Xray;

namespace Koalix.Tests;

public class DnsTests
{
    [Fact]
    public void Parses_txt_with_issues_and_line_numbers()
    {
        var r = DnsTxtFormat.Parse("# comment\nCloudflare\n1.1.1.1\n1.0.0.1\nBad\n999.1.1.1\nGoogle\n8.8.8.8\n8.8.4.4\n2001:4860:4860::8888\nEmpty");
        Assert.Equal(2, r.Profiles.Count);
        Assert.Equal("Cloudflare", r.Profiles[0].Name);
        Assert.Equal("1.0.0.1", r.Profiles[0].SecondaryV4);
        Assert.Equal("2001:4860:4860::8888", r.Profiles[1].PrimaryV6);
        Assert.Contains(r.Issues, i => i.Line == 6 && i.Code == "Dns.Parse.InvalidAddress");
        Assert.Contains(r.Issues, i => i.Line == 5 && i.Code == "Dns.Parse.NoServers");
        Assert.Contains(r.Issues, i => i.Line == 11 && i.Code == "Dns.Parse.NoServers");
    }

    [Fact]
    public void Export_roundtrip()
    {
        var src = DnsTxtFormat.Parse("A\n1.1.1.1\n1.0.0.1\nB\n9.9.9.9").Profiles;
        var again = DnsTxtFormat.Parse(DnsTxtFormat.Export(src)).Profiles;
        Assert.Equal(src.Select(p => p.Name), again.Select(p => p.Name));
        Assert.Equal(src.Select(p => p.SecondaryV4), again.Select(p => p.SecondaryV4));
    }

    [Theory]
    [InlineData(new double[] { 10, 20, 10, 20 }, 10.0)]
    [InlineData(new double[] { 30, 30, 30 }, 0.0)]
    [InlineData(new double[] { 5, 15, 10 }, 7.5)]
    public void Jitter_is_mean_absolute_successive_difference(double[] samples, double expected) =>
        Assert.Equal(expected, LatencyStats.Jitter(samples)!.Value, 6);

    [Fact]
    public void Jitter_needs_two_samples() => Assert.Null(LatencyStats.Jitter([12]));

    [Theory]
    [InlineData(new double[] { 3, 1, 2 }, 2.0)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    public void Median(double[] s, double m) => Assert.Equal(m, LatencyStats.Median(s));

    private static DnsServerTestResult Result(string s, double? icmp, double dnsMedian, double jitter, int ok, int attempts) =>
        new(s, icmp is null ? new LatencyStats(5, 0, null, null, null, null, null) : new LatencyStats(5, 5, icmp, icmp, icmp, icmp, 1),
            new LatencyStats(attempts, ok, dnsMedian, dnsMedian, dnsMedian, dnsMedian, jitter), ok, attempts, ok, attempts - ok, 0, null);

    [Fact]
    public void Scorer_prefers_fast_reliable_server_and_does_not_penalise_blocked_icmp()
    {
        var fast = Result("a", null, 20, 2, 6, 6);      // ICMP blocked but DNS fast
        var slow = Result("b", 15, 120, 30, 6, 6);
        Assert.True(DnsScorer.ScoreServer(fast, RankingStrategy.BalancedGaming) < DnsScorer.ScoreServer(slow, RankingStrategy.BalancedGaming));
    }

    [Fact]
    public void Scorer_marks_dead_server_unusable()
    {
        var dead = Result("x", null, 0, 0, 0, 6) with { DnsQuery = new LatencyStats(6, 0, null, null, null, null, null) };
        Assert.True(DnsScorer.ScoreServer(dead, RankingStrategy.LowestMedianLatency) >= DnsScorer.Unusable);
    }

    [Fact]
    public void Dns_query_packet_roundtrip()
    {
        var q = DnsQueryClient.BuildQuery(0x1234, "example.com", 1);
        Assert.Equal(0x12, q[0]); Assert.Equal(0x34, q[1]);
        Assert.Equal(1, q[5]); // QDCOUNT = 1
        // fake response: copy query, set QR bit + 1 answer
        var resp = q.ToArray(); resp[2] |= 0x80; resp[7] = 1;
        var parsed = DnsQueryClient.ParseResponse(resp, 0x1234);
        Assert.NotNull(parsed);
        Assert.Equal(DnsRcode.NoError, parsed!.Value.Rcode);
        Assert.Null(DnsQueryClient.ParseResponse(resp, 0x9999)); // wrong id rejected
    }

    [Fact]
    public void Stun_request_has_magic_cookie()
    {
        var tx = new byte[12]; Random.Shared.NextBytes(tx);
        var req = StunTester.BuildBindingRequest(tx);
        Assert.Equal(20, req.Length);
        Assert.Equal(new byte[] { 0x21, 0x12, 0xA4, 0x42 }, req[4..8]);
        Assert.Equal(("stun.l.google.com", 19302), StunTester.SplitHostPort("stun.l.google.com:19302", 3478));
    }
}

public class LinkAndConfigTests
{
    internal const string Vless = "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=ws&security=tls&sni=example.com&path=%2Fws&host=example.com&fp=chrome#My%20Node";
    internal const string Reality = "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?type=tcp&security=reality&sni=www.microsoft.com&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw&sid=6ba85179e30d4fc2&fp=chrome&flow=xtls-rprx-vision#R";
    internal const string Ss = "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpwYXNzd29yZA@10.0.0.1:8388#SS";
    internal const string Trojan = "trojan://secretpw@example.org:443?sni=example.org#T";
    internal const string Xhttp = "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=xhttp&security=tls&sni=example.com&path=%2Fx#XH";

    [Fact]
    public void Parses_vless_ws_tls()
    {
        var n = ShareLinkParser.Parse(Vless);
        Assert.Equal(ProxyProtocol.VLess, n.Protocol);
        Assert.Equal("My Node", n.Name);
        Assert.Equal("ws", n.Transport);
        Assert.Equal("/ws", n.Path);
        Assert.Equal("tls", n.Security);
        Assert.Equal(Vless, n.RawLink);
        Assert.Empty(n.Validate());
    }

    [Fact]
    public void Parses_shadowsocks_sip002() { var n = ShareLinkParser.Parse(Ss); Assert.Equal("chacha20-ietf-poly1305", n.Method); Assert.Equal("password", n.Password); }

    [Fact]
    public void Parses_base64_subscription()
    {
        var body = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Vless + "\n" + Trojan + "\nbogus://x"));
        var r = ShareLinkParser.ParseMany(body);
        Assert.Equal(2, r.Nodes.Count); Assert.Single(r.Errors);
    }

    [Fact]
    public void Xhttp_is_routed_to_xray_engine()
    {
        Assert.False(SingBoxConfigBuilder.IsNativelySupported(ShareLinkParser.Parse(Xhttp), out _));
        Assert.True(SingBoxConfigBuilder.IsNativelySupported(ShareLinkParser.Parse(Vless), out _));
    }

    [Theory]
    [InlineData("https://example.com/sub", true)]
    [InlineData("http://example.com/sub", false)]
    [InlineData("https://127.0.0.1/sub", false)]
    [InlineData("https://user:pw@example.com/sub", false)]
    public void Subscription_url_validation(string url, bool ok) => Assert.Equal(ok, SubscriptionClient.IsValidSubscriptionUrl(url, out _));
}

public class SplitTunnelTests
{
    private static readonly AppRule Game = new() { DisplayName = "Game", ExecutablePath = @"C:\Games\Valorant\VALORANT.exe", Action = AppAction.Tunnel, IncludeFolder = true };
    private static readonly AppRule Browser = new() { DisplayName = "Chrome", ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe", Action = AppAction.Direct };
    private static readonly AppRule Bad = new() { DisplayName = "Bad", ExecutablePath = @"C:\x\evil.exe", Action = AppAction.Block };

    private static IEnumerable<JsonObject> Rules(SplitRuleCompiler.Compiled c) => c.RouteRules.OfType<JsonObject>();

    [Fact]
    public void Include_mode_routes_only_selected_apps_to_proxy_and_defaults_direct()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [Game, Browser]), [@"C:\N\xray.exe"]);
        Assert.Equal("direct", c.RouteFinal);
        var proxyRule = Rules(c).Single(r => r["outbound"]?.ToString() == "proxy");
        Assert.Contains("Valorant", proxyRule.ToJsonString());
        Assert.Contains("process_path_regex", proxyRule.ToJsonString()); // folder mode
        // engine helper is forced direct BEFORE any proxy rule
        var all = Rules(c).ToList();
        var engineIdx = all.FindIndex(r => r.ToJsonString().Contains("xray.exe"));
        Assert.True(engineIdx >= 0 && engineIdx < all.IndexOf(proxyRule));
    }

    [Fact]
    public void Include_mode_resolves_dns_through_tunnel_not_poisonable_local()
    {
        // Regression (v0.3.6, field log): Windows DNS queries are sent by svchost (DNS Client service), never by
        // the app process, so a LOCAL dns final in Include mode sent every lookup to the plain-UDP local resolver.
        // On hijacking ISPs blocked domains came back as the 10.10.34.35 sinkhole and tunneled apps broke while
        // the tunnel itself was healthy. Traffic final stays DIRECT; only DNS moves into the tunnel.
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [Game]), []);
        Assert.Equal("direct", c.RouteFinal);
        Assert.Equal("dns-remote", c.DnsFinal);
    }

    [Fact]
    public void Include_mode_honours_explicit_tunnel_dns_opt_out()
    {
        var policy = new SplitPolicy(SplitMode.Include, [Game], TunnelDns: false);
        var c = SplitRuleCompiler.Compile(policy, []);
        Assert.Equal("dns-local", c.DnsFinal);
    }

    [Fact]
    public void Include_mode_without_ipv6_upstream_hands_out_ipv4_only_dns()
    {
        // tunnelIpv6: false — AAAA answers (incl. synthetic NAT64 forms of poisoned answers) must not be
        // handed to apps that would only stall on them; applies to Include mode as well since v0.3.6.
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [Game]), [], tunnelIpv6: false);
        Assert.True(c.DnsIpv4Only);
        Assert.Equal("dns-remote", c.DnsFinal);
    }

    [Fact]
    public void Exclude_mode_defaults_to_proxy_and_sends_excluded_direct()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Exclude, [Browser]), []);
        Assert.Equal("proxy", c.RouteFinal);
        Assert.Contains(Rules(c), r => r["outbound"]?.ToString() == "direct" && r.ToJsonString().Contains("chrome.exe"));
    }

    [Fact]
    public void Block_rule_rejects()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.FullTunnel, [Bad]), []);
        Assert.Contains(Rules(c), r => r["action"]?.ToString() == "reject" && r.ToJsonString().Contains("evil.exe"));
    }

    private static readonly AppRule ChromeTunnel = new() { DisplayName = "Chrome", ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe", Action = AppAction.Tunnel };

    private static int SniffIndex(SplitRuleCompiler.Compiled c) =>
        Rules(c).ToList().FindIndex(r => (string?)r["action"] == "sniff" && r.Count == 1);

    [Fact]
    public void Ipv6_tcp_reject_for_tunneled_browser_is_pre_matched_before_sniff()
    {
        // v0.4.32 field bug (log koalix-20261107): Include mode + IPv4-only upstream. The v6 reject sat AFTER
        // sniff, so sing-box let gVisor finish the TCP handshake and only then reset it — Chrome/Firefox never fell
        // back to IPv4 and every dual-stack site (Google, YouTube) failed while ipnumberia (IPv4-only) worked.
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [ChromeTunnel]), [@"C:\N\xray.exe"], tunnelIpv6: false);
        var all = Rules(c).ToList();
        var sniff = SniffIndex(c);
        var early = all.FindIndex(r => (string?)r["action"] == "reject" && (string?)r["network"] == "tcp"
                                       && r["ip_cidr"]?[0]?.ToString() == "2000::/3" && r.ToJsonString().Contains("chrome.exe"));
        Assert.True(early >= 0 && early < sniff, "TCP v6 reject must precede sniff to be pre-matched (RST to the SYN)");
        // UDP backstop (QUIC) is kept after sniff/hijack-dns
        Assert.Contains(all.Skip(sniff), r => (int?)r["ip_version"] == 6 && (string?)r["action"] == "reject");
        // engine (xray.exe) is never caught by the early per-app reject
        Assert.DoesNotContain(all.Take(sniff), r => r.ToJsonString().Contains("xray.exe"));
    }

    [Fact]
    public void Global_ipv6_leak_block_spares_engine_and_runs_before_sniff()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.FullTunnel, [], BlockIpv6Leaks: true) { BlockQuic = false }, [@"C:\N\xray.exe"]);
        var all = Rules(c).ToList();
        var first = all[0];
        Assert.Equal("logical", (string?)first["type"]);
        Assert.Equal("reject", (string?)first["action"]);
        var json = first.ToJsonString();
        Assert.Contains("2000::/3", json);
        Assert.Contains("xray.exe", json);
        Assert.Contains("\"invert\":true", json);
        Assert.True(SniffIndex(c) > 0);
    }

    [Fact]
    public void Exclude_mode_v6_early_reject_spares_direct_apps()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Exclude, [Browser]) { BlockQuic = false }, [], tunnelIpv6: false);
        var all = Rules(c).ToList();
        var early = all.Take(SniffIndex(c)).Single();
        Assert.Contains("chrome.exe", early.ToJsonString());
        Assert.Contains("\"invert\":true", early.ToJsonString());
    }

    [Fact]
    public void No_early_rules_when_tunnel_carries_ipv6()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [ChromeTunnel]) { BlockQuic = false }, []);
        Assert.Equal(0, SniffIndex(c));
    }

    private static bool IsQuicReject(JsonObject r) =>
        (string?)r["action"] == "reject" && r.ToJsonString() is var j && j.Contains("udp") && j.Contains("443");

    [Fact]
    public void Quic_of_tunneled_apps_is_refused_before_sniff_in_include_mode()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [ChromeTunnel]), []);
        var q = Rules(c).Take(SniffIndex(c)).Single(IsQuicReject);
        Assert.Contains("chrome.exe", q.ToJsonString());
    }

    [Fact]
    public void Quic_reject_spares_the_engine_in_full_tunnel()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.FullTunnel, []), [@"C:\k\xray.exe"]);
        var q = Rules(c).Take(SniffIndex(c)).Single(IsQuicReject);
        Assert.Contains("xray.exe", q.ToJsonString());
        Assert.Contains("\"invert\":true", q.ToJsonString());
    }

    [Fact]
    public void Quic_reject_spares_direct_apps_in_exclude_mode()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Exclude, [Browser]), []);
        var q = Rules(c).Take(SniffIndex(c)).Single(IsQuicReject);
        Assert.Contains("chrome.exe", q.ToJsonString());
        Assert.Contains("\"invert\":true", q.ToJsonString());
    }

    [Fact]
    public void Quic_reject_can_be_turned_off()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [ChromeTunnel]) { BlockQuic = false }, []);
        Assert.DoesNotContain(Rules(c), IsQuicReject);
    }

    [Fact]
    public void Blocked_app_tcp_is_refused_before_sniff()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.FullTunnel, [Bad]), []);
        var sniff = SniffIndex(c);
        Assert.Contains(Rules(c).Take(sniff), r => (string?)r["network"] == "tcp" && (string?)r["action"] == "reject" && r.ToJsonString().Contains("evil.exe"));
    }

    [Fact]
    public void Health_inbound_rule_lands_right_after_sniff_whatever_the_early_rules()
    {
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [ChromeTunnel, Bad], BlockIpv6Leaks: true), []);
        Assert.Equal(SniffIndex(c) + 1, SplitRuleCompiler.AfterSniff(c.RouteRules));
    }

    [Fact]
    public void Folder_regex_is_anchored_and_case_insensitive()
    {
        var rx = SplitRuleCompiler.FolderRegex(Game.ExecutablePath);
        Assert.Matches(new System.Text.RegularExpressions.Regex(rx), @"c:\games\valorant\Riot\helper.exe");
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(rx), @"C:\Games\ValorantX\a.exe");
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(rx), @"D:\C:\Games\Valorant\a.exe");
    }

    [Fact]
    public void Folder_rule_matches_everything_inside_folder()
    {
        var folder = new AppRule { DisplayName = "Games", ExecutablePath = @"C:\Games\Steam", IncludeFolder = true, Action = AppAction.Tunnel };
        Assert.True(folder.IsFolderRule);
        Assert.Equal(@"C:\Games\Steam\", folder.FolderPrefix);
        Assert.True(folder.Matches(@"c:\games\steam\steamapps\common\cs2\cs2.exe"));
        Assert.False(folder.Matches(@"C:\Games\SteamX\a.exe"));
        Assert.Empty(new SplitPolicy(SplitMode.Include, [folder]).Validate());
        var c = SplitRuleCompiler.Compile(new SplitPolicy(SplitMode.Include, [folder]), []);
        var rule = Rules(c).First(r => r["process_path_regex"] is not null);
        var rx = new System.Text.RegularExpressions.Regex(rule["process_path_regex"]![0]!.ToString());
        Assert.Matches(rx, @"C:\Games\Steam\steam.exe");
        Assert.DoesNotMatch(rx, @"C:\Games\SteamX\steam.exe");
        Assert.Null(rule["process_path"]);
    }

    [Theory]
    [InlineData(@"C:\Games\Steam", true)]
    [InlineData(@"C:\Games\Steam\", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"relative\dir", false)]
    [InlineData(@"C:\Games\..\Windows", false)]
    [InlineData(@"C:\Games\*", false)]
    public void Folder_path_validator(string p, bool ok) => Assert.Equal(ok, PathValidator.IsValidFolderPath(p));

    [Fact]
    public void Folder_rule_without_include_flag_is_invalid() =>
        Assert.NotEmpty(new SplitPolicy(SplitMode.FullTunnel, [new AppRule { DisplayName = "x", ExecutablePath = @"C:\Games", IncludeFolder = false }]).Validate());

    [Fact]
    public void Include_without_apps_is_invalid() =>
        Assert.Contains("Split.Error.IncludeNeedsApp", new SplitPolicy(SplitMode.Include, [Browser]).Validate());

    [Theory]
    [InlineData(@"C:\Games\a.exe", true)]
    [InlineData(@"C:\Games\a.txt", false)]
    [InlineData(@"relative\a.exe", false)]
    [InlineData("C:\\Games\\a\".exe", false)]
    [InlineData(@"C:\Games\*.exe", false)]
    public void Path_validator(string p, bool ok) => Assert.Equal(ok, PathValidator.IsValidExecutablePath(p));

    [Fact]
    public void Inspector_never_reports_unknown_as_mismatch()
    {
        var unknown = new AppTrafficSummary(@"C:\a.exe", "tunnel", ObservedRoute.Unknown, 1, 0, 0);
        var wrong = new AppTrafficSummary(@"C:\a.exe", "tunnel", ObservedRoute.Direct, 1, 0, 0);
        var ok = new AppTrafficSummary(@"C:\a.exe", "direct", ObservedRoute.Bypass, 1, 0, 0);
        Assert.False(Koalix_InspectorRules.IsMismatch(unknown));
        Assert.True(Koalix_InspectorRules.IsMismatch(wrong));
        Assert.False(Koalix_InspectorRules.IsMismatch(ok));
    }

    [Fact]
    public void Egress_verdicts()
    {
        Assert.Equal("Unknown", EgressVerifier.Compare(null, "1.1.1.1").Verdict);
        Assert.Equal("Same", EgressVerifier.Compare("1.1.1.1", "1.1.1.1").Verdict);
        Assert.Equal("Verified", EgressVerifier.Compare("1.1.1.1", "2.2.2.2").Verdict);
    }
}

/// <summary>Mirror of the UI mismatch rule (kept in sync with InspectorViewModel.IsMismatch).</summary>
internal static class Koalix_InspectorRules
{
    public static bool IsMismatch(AppTrafficSummary a) => (a.ConfiguredRoute, a.ObservedRoute) switch
    {
        (_, ObservedRoute.Unknown) => false,
        ("tunnel", ObservedRoute.Tunnel) or ("direct", ObservedRoute.Direct) or ("direct", ObservedRoute.Bypass) or ("block", ObservedRoute.Blocked) => false,
        _ => true,
    };
}

public class WireGuardAndSecurityTests
{
    internal const string Conf = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 10.8.0.2/32, fd00::2/128
        DNS = 1.1.1.1
        MTU = 1420

        [Peer]
        PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
        PresharedKey = FpCyhws9cxwWoV4xELtfJvjJN+zQVRPISllRWgeopVE=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = vpn.example.com:51820
        PersistentKeepalive = 25
        """;

    [Fact]
    public void Parses_wireguard_conf()
    {
        var c = WireGuardConfig.Parse(Conf, out var errors);
        Assert.Empty(errors);
        Assert.Equal(2, c.Addresses.Count);
        Assert.Equal(("vpn.example.com", 51820), c.Peers[0].EndpointParts);
        var ep = c.ToSingBoxEndpoint();
        Assert.Equal("wireguard", ep["type"]!.ToString());
    }

    [Fact]
    public void Wireguard_errors_are_reported()
    {
        WireGuardConfig.Parse("[Interface]\nPrivateKey = nope\n", out var errors);
        Assert.Contains("WG.Error.PrivateKey", errors);
        Assert.Contains("WG.Error.NoPeers", errors);
    }

    [Fact]
    public void Redacted_wireguard_hides_keys()
    {
        var s = WireGuardConfig.Parse(Conf, out _).ToRedactedString();
        Assert.DoesNotContain("yAnz5TF", s); Assert.DoesNotContain("FpCyhws9", s);
    }

    [Fact]
    public void Redactor_hides_secrets()
    {
        var text = $"link {LinkAndConfigTests.Vless} key=\"PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\" {{\"password\":\"hunter2\"}} https://x.com/sub?token=abc123";
        var r = Redactor.Redact(text);
        Assert.DoesNotContain("11111111-2222", r);
        Assert.DoesNotContain("yAnz5TF", r);
        Assert.DoesNotContain("hunter2", r);
        Assert.DoesNotContain("abc123", r);
        Assert.Contains("203.0.113.0", Redactor.Redact("ip 203.0.113.0")); // IPs kept unless requested
        Assert.DoesNotContain("203.0.113.0", Redactor.Redact("ip 203.0.113.0", redactIpAddresses: true));
    }

    [Fact]
    public void State_machine_rejects_invalid_transitions()
    {
        var sm = new ConnectionStateMachine();
        Assert.False(sm.CanTransition(ConnectionState.Connected)); // must go through Connecting
        sm.Transition(ConnectionState.Connecting);
        sm.Transition(ConnectionState.Connected);
        Assert.True(sm.TryTransition(ConnectionState.Degraded));
        Assert.Throws<InvalidOperationException>(() => sm.Transition(ConnectionState.Connecting));
    }
}

public class InfrastructureTests
{
    [Fact]
    public void Database_migrates_and_repositories_roundtrip_with_encryption()
    {
        var dir = Directory.CreateTempSubdirectory("koalix-test").FullName;
        var db = new Koalix.Infrastructure.Database(Path.Combine(dir, "t.db"));
        db.Migrate(dir);
        Assert.Equal(Koalix.Infrastructure.Database.Migrations.Length, db.CurrentVersion());
        db.Migrate(dir); // idempotent
        var protector = new Koalix.Infrastructure.AesFileSecretProtector(Path.Combine(dir, "k"));
        var repo = new Koalix.Infrastructure.TunnelProfileRepository(db, protector);
        repo.Add(new TunnelProfile { Name = "n", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = LinkAndConfigTests.Vless });
        Assert.Equal(LinkAndConfigTests.Vless, repo.All().Single().Content);
        // stored content is not plaintext
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT content_protected FROM tunnel_profiles";
        Assert.DoesNotContain("vless://", (string)cmd.ExecuteScalar()!);

        var dns = new Koalix.Infrastructure.DnsProfileRepository(db);
        Assert.True(dns.TryAdd(new DnsProfile { Name = "CF", PrimaryV4 = "1.1.1.1", SecondaryV4 = "1.0.0.1" }, out _));
        Assert.False(dns.TryAdd(new DnsProfile { Name = "CF2", PrimaryV4 = "1.1.1.1", SecondaryV4 = "1.0.0.1" }, out _)); // duplicate
    }

    [Fact]
    public void Journal_survives_restart_and_completes()
    {
        var f = Path.Combine(Directory.CreateTempSubdirectory().FullName, "j.json");
        var j = new NetworkStateJournal(f);
        var e = j.Record(JournalKind.DnsChange, "eth0", "{}", "1.1.1.1");
        Assert.Single(new NetworkStateJournal(f).Pending()); // "crash" → new instance still sees it
        j.Complete(e.Id);
        Assert.Empty(new NetworkStateJournal(f).Pending());
    }

    /// <summary>v0.4.19 — drag-and-drop reordering: Reorder writes the manual sequence and All() serves it back
    /// (sort_order first, name for the never-dragged tail); the order survives a "restart" (new repository).</summary>
    [Fact]
    public void Reorder_persists_manual_drag_order()
    {
        var dir = Directory.CreateTempSubdirectory("koalix-sort").FullName;
        var db = new Koalix.Infrastructure.Database(Path.Combine(dir, "t.db"));
        db.Migrate(dir);
        var protector = new Koalix.Infrastructure.AesFileSecretProtector(Path.Combine(dir, "k"));
        var repo = new Koalix.Infrastructure.TunnelProfileRepository(db, protector);
        var a = repo.Add(new TunnelProfile { Name = "aaa", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = LinkAndConfigTests.Vless });
        var b = repo.Add(new TunnelProfile { Name = "bbb", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = LinkAndConfigTests.Vless });
        var c = repo.Add(new TunnelProfile { Name = "ccc", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = LinkAndConfigTests.Vless });
        // nothing dragged yet → alphabetical (all sort_order = 0)
        Assert.Equal(["aaa", "bbb", "ccc"], repo.All().Select(p => p.Name));
        // drag c above b: the user's order becomes a, c, b
        repo.Reorder([a, c, b]);
        var after = repo.All().Select(p => p.Name).ToList();
        Assert.Equal(["aaa", "ccc", "bbb"], after);
        Assert.Equal(20, repo.All().Single(p => p.Name == "ccc").SortOrder);
        // a dragged row keeps its place across a "restart" (fresh repository over the same file)
        var repo2 = new Koalix.Infrastructure.TunnelProfileRepository(db, protector);
        Assert.Equal(["aaa", "ccc", "bbb"], repo2.All().Select(p => p.Name));
        // a NEW profile appends at the END of the manual order (never jumps above dragged rows)
        repo.Add(new TunnelProfile { Name = "zzz", Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = LinkAndConfigTests.Vless });
        Assert.Equal(["aaa", "ccc", "bbb", "zzz"], repo.All().Select(p => p.Name));
    }

    [Theory]
    [InlineData("SHA2-256= 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("MD5= x\nSHA256 (Xray-windows-64.zip) = ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("nothing", null)]
    public void Parses_xray_dgst(string text, string? expected) => Assert.Equal(expected, Koalix.Infrastructure.EngineManager.ParseDgst(text));

    [Fact]
    public void Dns_inspector_parses_or_ignores()
    {
        Assert.Equal("example.com", DnsInspector.TryParse("DEBUG dns: exchange example.com. IN A", DateTimeOffset.Now)?.Domain);
        Assert.Null(DnsInspector.TryParse("INFO inbound/tun[tun-in]: started", DateTimeOffset.Now));
    }
}

public class GamingAndPresetTests
{
    private static TunnelProfile Link(string link, string name = "N") => new()
    { Name = name, Engine = EngineKind.SingBox, Source = ProfileSource.ShareLink, Content = link };

    [Fact]
    public void Cloudflare_worker_config_is_flagged_not_gameable()
    {
        // The classic trap: VLESS+WS behind a Cloudflare Worker — browses fine, cannot carry UDP at all.
        var link = "vless://11111111-2222-3333-4444-555555555555@myworker.example.workers.dev:443?type=ws&security=tls&sni=myworker.example.workers.dev&host=myworker.example.workers.dev&path=%2F#CF%20Worker";
        var a = GamingSuitability.Analyze(Link(link));
        Assert.True(a.CloudflareFronted);
        Assert.Equal(UdpSupport.None, a.Udp);
        Assert.Equal(GamingVerdict.Bad, a.Verdict);
        Assert.Contains("Gaming.Reason.CloudflareNoUdp", a.ReasonCodes);
    }

    [Fact]
    public void Cloudflare_ip_fronted_ws_is_detected_by_range()
    {
        var link = "vless://11111111-2222-3333-4444-555555555555@104.16.132.229:443?type=ws&security=tls&sni=cdn.example.com&host=cdn.example.com#Edge";
        var a = GamingSuitability.Analyze(Link(link));
        Assert.True(a.CloudflareFronted);
        Assert.Equal(GamingVerdict.Bad, a.Verdict);
        Assert.True(GamingSuitability.IsCloudflareIp(System.Net.IPAddress.Parse("172.64.10.1")));
        Assert.False(GamingSuitability.IsCloudflareIp(System.Net.IPAddress.Parse("31.14.119.77")));
    }

    [Fact]
    public void Wireguard_and_hysteria_are_native_udp_good()
    {
        var wg = new TunnelProfile { Name = "WG", Engine = EngineKind.WireGuard, Source = ProfileSource.WireGuardConf, Content = WireGuardAndSecurityTests.Conf };
        var a = GamingSuitability.Analyze(wg);
        Assert.Equal(UdpSupport.Native, a.Udp);
        Assert.Equal(GamingVerdict.Good, a.Verdict);

        var hy2 = GamingSuitability.Analyze(Link("hy2://pass@203.0.113.10:443?sni=example.com#HY2"));
        Assert.Equal(UdpSupport.Native, hy2.Udp);
        Assert.Equal(GamingVerdict.Good, hy2.Verdict);
    }

    [Fact]
    public void Plain_tcp_vless_is_udp_over_tcp_ok()
    {
        var a = GamingSuitability.Analyze(Link(LinkAndConfigTests.Reality));
        Assert.Equal(UdpSupport.OverTcp, a.Udp);
        Assert.Equal(GamingVerdict.Ok, a.Verdict);
        Assert.False(a.CloudflareFronted);
    }

    [Fact]
    public void Self_check_sinkhole_signature()
    {
        Assert.True(SystemSelfCheck.IsSinkhole(System.Net.IPAddress.Parse("10.10.34.35")));
        Assert.False(SystemSelfCheck.IsSinkhole(System.Net.IPAddress.Parse("10.10.35.1")));
        Assert.True(SystemSelfCheck.IsPrivate(System.Net.IPAddress.Parse("192.168.1.1")));
        Assert.False(SystemSelfCheck.IsPrivate(System.Net.IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Iran_preset_full_mode_adds_rule_sets_and_direct_rule()
    {
        var node = ShareLinkParser.Parse(LinkAndConfigTests.Vless);
        var policy = new SplitPolicy(SplitMode.FullTunnel, [], true, false, IranDirectPreset: true);
        var json = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, policy, new SingBoxBuildOptions { HealthPort = 18081, ClashApiPort = 19091, ClashApiSecret = "testsecret" });
        var route = json["route"]!;
        var sets = route["rule_set"]!.AsArray().OfType<JsonObject>().Select(s => (string)s["tag"]!).ToList();
        Assert.Contains("geosite-ir", sets);
        Assert.Contains("geoip-ir", sets);
        var presetRule = route["rules"]!.AsArray().OfType<JsonObject>()
            .Single(r => r["rule_set"] is JsonArray && r["outbound"]?.ToString() == "direct");
        Assert.Contains("geosite-ir", presetRule["rule_set"]!.AsArray().Select(x => x!.ToString()));
        var dnsRule = json["dns"]!["rules"]!.AsArray().OfType<JsonObject>().First();
        Assert.Equal("dns-local", dnsRule["server"]!.ToString());
        Assert.Equal("proxy", route["final"]!.ToString());
        Assert.Empty(SingBoxConfigLinter.Lint(json));
    }

    [Fact]
    public void Iran_preset_is_ignored_in_include_mode()
    {
        var node = ShareLinkParser.Parse(LinkAndConfigTests.Vless);
        var policy = new SplitPolicy(SplitMode.Include,
            [new AppRule { DisplayName = "Chrome", ExecutablePath = @"C:\P\chrome.exe", Action = AppAction.Tunnel }],
            true, false, IranDirectPreset: true);
        var json = SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(node), false, policy, new SingBoxBuildOptions { HealthPort = 18081, ClashApiPort = 19091, ClashApiSecret = "testsecret" });
        Assert.Null(json["route"]!["rule_set"]);
        Assert.Empty(SingBoxConfigLinter.Lint(json));
    }
}

public class DomainRuleTests
{
    [Fact]
    public void Parse_domains_normalises_urls_wildcards_and_junk()
    {
        var d = SplitPolicy.ParseDomains("https://www.YouTube.com/watch?v=1\n*.googlevideo.com, .ggpht.com\n# comment\nnot a domain\nlocalhost\nexample.com:8443\nyoutube.com\nwww.youtube.com");
        Assert.Equal(["www.youtube.com", "googlevideo.com", "ggpht.com", "example.com", "youtube.com"], d);
        Assert.Empty(SplitPolicy.ParseDomains(null));
    }

    [Fact]
    public void Domain_rules_are_compiled_before_app_split_with_matching_dns()
    {
        var chrome = new AppRule { DisplayName = "Chrome", ExecutablePath = @"C:\c\chrome.exe", Action = AppAction.Tunnel };
        var policy = new SplitPolicy(SplitMode.Include, [chrome]) { ProxyDomains = ["youtube.com"], DirectDomains = ["digikala.com"] };
        var c = SplitRuleCompiler.Compile(policy, [], tunnelIpv6: false);
        var all = c.RouteRules.OfType<JsonObject>().ToList();
        var direct = all.FindIndex(r => r["domain_suffix"]?[0]?.ToString() == "digikala.com" && (string?)r["outbound"] == "direct");
        var proxy = all.FindIndex(r => r["domain_suffix"]?[0]?.ToString() == "youtube.com" && (string?)r["outbound"] == "proxy");
        var app = all.FindIndex(r => r["process_path"] is not null && (string?)r["outbound"] == "proxy");
        Assert.True(direct >= 0 && proxy > direct && app > proxy);
        // IPv4-only upstream: v6 to a proxy domain is refused rather than stalling in the tunnel
        Assert.Contains(all, r => r["domain_suffix"] is not null && (int?)r["ip_version"] == 6 && (string?)r["action"] == "reject");
        Assert.Contains(c.DnsRules.OfType<JsonObject>(), r => r["domain_suffix"]?[0]?.ToString() == "youtube.com" && (string?)r["server"] == "dns-remote");
        Assert.Contains(c.DnsRules.OfType<JsonObject>(), r => r["domain_suffix"]?[0]?.ToString() == "digikala.com" && (string?)r["server"] == "dns-local");
    }
}
