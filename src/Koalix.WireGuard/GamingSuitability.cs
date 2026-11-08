using System.Net;
using Koalix.Tunneling;

namespace Koalix.WireGuard;

public enum UdpSupport { Native, OverTcp, None, Unknown }
public enum GamingVerdict { Good, Ok, Bad, Unknown }

/// <summary>Static verdict about a profile's gaming fitness, before any live measurement.</summary>
public sealed record GamingAssessment(GamingVerdict Verdict, UdpSupport Udp, bool CloudflareFronted, IReadOnlyList<string> ReasonCodes);

/// <summary>
/// Static gaming-suitability analysis of a tunnel profile (v0.4.0). Games need real UDP, low jitter and a
/// direct server path. The classic trap this catches: a VLESS/VMess-over-WebSocket config fronted by a
/// Cloudflare Worker — Cloudflare's free/worker path cannot carry UDP at all and adds a CDN hop, so the
/// config works for browsing but is useless for games, however good its "ping" looks in a TCP test.
/// Detection signals: workers.dev hosts/SNI/Host header, "worker" in the profile name, or a server IP
/// inside Cloudflare's published anycast ranges combined with a WebSocket-family transport.
/// </summary>
public static class GamingSuitability
{
    // Cloudflare published IPv4 ranges (https://www.cloudflare.com/ips-v4).
    private static readonly string[] CloudflareCidrs =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
        "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
        "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    ];

    public static bool IsCloudflareIp(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        foreach (var c in CloudflareCidrs)
            if (IPNetwork.TryParse(c, out var net) && net.Contains(ip)) return true;
        return false;
    }

    private static bool MentionsWorker(string? s) =>
        s is not null && (s.Contains("workers.dev", StringComparison.OrdinalIgnoreCase) || s.Contains("worker", StringComparison.OrdinalIgnoreCase));

    public static bool IsCloudflareFronted(ProxyNode n, string? profileName)
    {
        if (MentionsWorker(n.Server) || MentionsWorker(n.Sni) || MentionsWorker(n.Host) || MentionsWorker(profileName) || MentionsWorker(n.Name)) return true;
        if (IPAddress.TryParse(n.Server, out var ip) && IsCloudflareIp(ip)) return true;
        return false;
    }

    private static bool IsWsFamily(string transport) => transport is "ws" or "httpupgrade" or "http" or "xhttp" or "splithttp";

    public static GamingAssessment Analyze(TunnelProfile p)
    {
        try
        {
            return p.Source switch
            {
                ProfileSource.ShareLink => AnalyzeNode(ShareLinkParser.Parse(p.Content), p.Name),
                // WireGuard is UDP end-to-end by design; its endpoint cannot be CDN-fronted.
                ProfileSource.WireGuardConf => new GamingAssessment(GamingVerdict.Good, UdpSupport.Native, false, ["Gaming.Reason.UdpNative"]),
                ProfileSource.SingBoxJson => AnalyzeSingBoxJson(p.Content, p.Name),
                _ => new GamingAssessment(GamingVerdict.Unknown, UdpSupport.Unknown, false, ["Gaming.Reason.RawJson"]),
            };
        }
        catch { return new GamingAssessment(GamingVerdict.Unknown, UdpSupport.Unknown, false, ["Gaming.Reason.RawJson"]); }
    }

    /// <summary>
    /// Static analysis of user-imported sing-box JSON (panel configs, e.g. BPB): every proxy outbound is checked
    /// for Cloudflare fronting (workers.dev / pages.dev hosts, worker hints, Cloudflare anycast IPs) and the
    /// transport family. The classic panel setup — VLESS/Trojan over WebSocket behind a Worker/Pages domain —
    /// cannot carry UDP, so it must be flagged "not suitable for games" instead of "unknown".
    /// </summary>
    private static GamingAssessment AnalyzeSingBoxJson(string content, string? profileName)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(content) is not System.Text.Json.Nodes.JsonObject root ||
            root["outbounds"] is not System.Text.Json.Nodes.JsonArray arr)
            return new GamingAssessment(GamingVerdict.Unknown, UdpSupport.Unknown, false, ["Gaming.Reason.RawJson"]);
        var proxies = arr.OfType<System.Text.Json.Nodes.JsonObject>()
            .Where(o => (string?)o["type"] is { } t && t is not ("direct" or "block" or "dns" or "selector" or "urltest"))
            .ToList();
        if (proxies.Count == 0)
            return new GamingAssessment(GamingVerdict.Unknown, UdpSupport.Unknown, false, ["Gaming.Reason.RawJson"]);

        bool CloudflareFronted(System.Text.Json.Nodes.JsonObject o)
        {
            string?[] fields = [(string?)o["server"], (string?)o["tls"]?["server_name"], (string?)o["transport"]?["headers"]?["Host"], profileName];
            if (fields.Any(f => f is not null && (f.Contains("workers.dev", StringComparison.OrdinalIgnoreCase) ||
                                                  f.Contains("pages.dev", StringComparison.OrdinalIgnoreCase) ||
                                                  f.Contains("worker", StringComparison.OrdinalIgnoreCase))))
                return true;
            return (string?)o["server"] is { } s && IPAddress.TryParse(s, out var ip) && IsCloudflareIp(ip);
        }

        var cf = proxies.All(CloudflareFronted);
        var wsFamily = proxies.All(o => (string?)o["transport"]?["type"] is null or "ws" or "httpupgrade" or "http");
        var nativeUdp = proxies.All(o => (string?)o["type"] is "hysteria2" or "tuic" or "shadowsocks");
        if (cf && wsFamily)
            return new GamingAssessment(GamingVerdict.Bad, UdpSupport.None, true, ["Gaming.Reason.CloudflareNoUdp", "Gaming.Reason.UdpOverTcp"]);
        if (cf)
            return new GamingAssessment(GamingVerdict.Ok, UdpSupport.OverTcp, true, ["Gaming.Reason.CloudflareFronted", "Gaming.Reason.UdpOverTcp"]);
        return new GamingAssessment(nativeUdp ? GamingVerdict.Good : GamingVerdict.Ok,
            nativeUdp ? UdpSupport.Native : UdpSupport.OverTcp, false,
            [nativeUdp ? "Gaming.Reason.UdpNative" : "Gaming.Reason.UdpOverTcp"]);
    }

    public static GamingAssessment AnalyzeNode(ProxyNode n, string? profileName)
    {
        var reasons = new List<string>();
        var cf = IsCloudflareFronted(n, profileName);
        var free = (profileName ?? n.Name).Contains("free", StringComparison.OrdinalIgnoreCase);

        UdpSupport udp;
        switch (n.Protocol)
        {
            case ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic:
                udp = UdpSupport.Native; reasons.Add("Gaming.Reason.UdpNative"); break;
            case ProxyProtocol.Shadowsocks:
                udp = UdpSupport.Native; reasons.Add("Gaming.Reason.UdpNative"); break;
            default:
                // Stream protocols carry UDP encapsulated inside the (TCP/QUIC) stream — sing-box calls this
                // UDP-over-TCP / XUDP. It works, but head-of-line blocking makes it second-class for games.
                udp = UdpSupport.OverTcp; reasons.Add("Gaming.Reason.UdpOverTcp"); break;
        }

        GamingVerdict verdict;
        if (cf && IsWsFamily(n.Transport))
        {
            // The browsing-works-gaming-doesn't case: the Cloudflare free/worker path drops UDP entirely.
            udp = UdpSupport.None;
            verdict = GamingVerdict.Bad;
            reasons.Add("Gaming.Reason.CloudflareNoUdp");
        }
        else if (cf && udp == UdpSupport.OverTcp)
        {
            verdict = GamingVerdict.Ok;
            reasons.Add("Gaming.Reason.CloudflareFronted");
        }
        else
        {
            if (cf) reasons.Add("Gaming.Reason.CloudflareFronted");
            verdict = udp == UdpSupport.Native ? GamingVerdict.Good : GamingVerdict.Ok;
        }
        if (free)
        {
            reasons.Add("Gaming.Reason.FreeHost");
            if (verdict == GamingVerdict.Good) verdict = GamingVerdict.Ok;
        }
        return new GamingAssessment(verdict, udp, cf, reasons);
    }
}

/// <summary>Live gaming measurement for one profile: delay samples, jitter, and a real UDP (STUN) probe through the tunnel.</summary>
public sealed record GamingResult(long ProfileId, GamingAssessment Static, double? MedianMs, double? JitterMs, bool? UdpOk, double? UdpMs, GamingVerdict Verdict, IReadOnlyList<string> ReasonCodes);
