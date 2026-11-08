using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.Tunneling;
using Koalix.Xray;

namespace Koalix.Tests;

/// <summary>v0.4.37 — «تحریم رو دور میزنه ولی فیلترینگ نه»: leaked ISP DNS sends filtered names to the 10.10.34.x
/// sinkhole; tunneled apps' sinkhole flows must reach the sniffing Xray upstream instead of the reject.</summary>
public class SinkholeRescueTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Xray = @"C:\K\xray.exe";
    private static readonly AppRule ChromeTunnel = new() { DisplayName = "Chrome", ExecutablePath = Chrome, Action = AppAction.Tunnel };
    private static readonly AppRule ChromeDirect = new() { DisplayName = "Chrome", ExecutablePath = Chrome, Action = AppAction.Direct };

    private static JsonObject Build(SplitPolicy policy, bool viaProxy, bool reject = true) =>
        SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(ShareLinkParser.Parse(LinkAndConfigTests.Vless)), false, policy,
            new SingBoxBuildOptions
            {
                HealthPort = 18081, ClashApiPort = 19091, ClashApiSecret = "s",
                AlwaysDirectExecutables = [Xray], RejectSinkholes = reject, SinkholeViaProxy = viaProxy,
            });

    private static List<JsonObject> Rules(JsonObject cfg) => cfg["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();

    private static int RejectIndex(List<JsonObject> rules) =>
        rules.FindIndex(r => (string?)r["action"] == "reject" && r["ip_cidr"] is JsonArray a && a.Any(x => (string?)x == "10.10.34.0/24"));

    [Fact]
    public void Include_mode_sends_tunneled_apps_sinkhole_flows_to_proxy_before_the_reject()
    {
        var rules = Rules(Build(new SplitPolicy(SplitMode.Include, [ChromeTunnel]), viaProxy: true));
        var rescue = rules[0];
        Assert.Equal("proxy", (string?)rescue["outbound"]);
        Assert.Equal("tcp", (string?)rescue["network"]);
        Assert.Contains(rescue["process_path"]!.AsArray(), x => (string?)x == Chrome);
        Assert.Contains(rescue["ip_cidr"]!.AsArray(), x => (string?)x == "10.10.34.0/24");
        Assert.Equal(1, RejectIndex(rules)); // everything else still hits the kill-net
    }

    [Fact]
    public void Native_engine_keeps_the_plain_reject_first()
    {
        var rules = Rules(Build(new SplitPolicy(SplitMode.Include, [ChromeTunnel]), viaProxy: false));
        Assert.Equal(0, RejectIndex(rules));
        Assert.DoesNotContain(rules, r => r["ip_cidr"] is JsonArray a && a.Any(x => (string?)x == "10.10.34.0/24") && r["outbound"] is not null);
    }

    [Fact]
    public void No_rescue_without_the_poison_guard()
    {
        var rules = Rules(Build(new SplitPolicy(SplitMode.Include, [ChromeTunnel]), viaProxy: true, reject: false));
        Assert.Equal(-1, RejectIndex(rules));
        Assert.DoesNotContain(rules, r => r["ip_cidr"] is JsonArray a && a.Any(x => (string?)x == "10.10.34.0/24"));
    }

    [Fact]
    public void Exclude_mode_rescues_everything_except_direct_apps_and_the_engine()
    {
        var rules = Rules(Build(new SplitPolicy(SplitMode.Exclude, [ChromeDirect]), viaProxy: true));
        var rescue = rules[0];
        Assert.Equal("logical", (string?)rescue["type"]);
        Assert.Equal("proxy", (string?)rescue["outbound"]);
        var subs = rescue["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var inverted = subs.Single(s => s["process_path"] is not null);
        Assert.True((bool)inverted["invert"]!);
        var paths = inverted["process_path"]!.AsArray().Select(x => (string?)x).ToList();
        Assert.Contains(Chrome, paths);
        Assert.Contains(Xray, paths);
        Assert.Equal(1, RejectIndex(rules));
    }

    [Fact]
    public void Full_tunnel_excludes_only_the_engine()
    {
        var r = SplitRuleCompiler.SinkholeRescue(new SplitPolicy(SplitMode.FullTunnel, []), [Xray], PoisonGuard.SinkholeCidrs);
        var rule = Assert.Single(r.OfType<JsonObject>());
        var inverted = rule["rules"]!.AsArray().OfType<JsonObject>().Single(s => s["process_path"] is not null);
        Assert.Equal([Xray], inverted["process_path"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    [Fact]
    public void Include_mode_with_no_tunneled_apps_adds_nothing()
    {
        Assert.Empty(SplitRuleCompiler.SinkholeRescue(new SplitPolicy(SplitMode.Include, []), [Xray], PoisonGuard.SinkholeCidrs));
    }

    [Fact]
    public void Xray_socks_inbound_sniffs_and_overrides_the_destination()
    {
        var cfg = XrayConfigBuilder.Build(ShareLinkParser.Parse(LinkAndConfigTests.Vless), 10808);
        var socks = cfg["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => (string?)i["tag"] == "socks-in");
        var sniff = socks["sniffing"]!.AsObject();
        Assert.True((bool)sniff["enabled"]!);
        Assert.False((bool)sniff["routeOnly"]!); // routeOnly=true would sniff but keep dialing the poisoned IP
        var over = sniff["destOverride"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Contains("tls", over);
        Assert.Contains("http", over);
    }
}
