using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SplitTunneling;
using Koalix.Tunneling;

namespace Koalix.SingBox;

public sealed record SingBoxBuildOptions
{
    public string TunInterfaceName { get; init; } = "koalix-tun";
    public string TunAddressV4 { get; init; } = "172.19.0.1/30";
    public string TunAddressV6 { get; init; } = "fdfe:dcba:9876::1/126";
    public int Mtu { get; init; } = 9000;
    /// <summary>gvisor / system / mixed. gvisor is the default: it needs no Windows Firewall rule and works next to other VPN/firewall software.</summary>
    public string Stack { get; init; } = "gvisor";
    public bool StrictRoute { get; init; } = true;
    public required int HealthPort { get; init; }
    public required int ClashApiPort { get; init; }
    public required string ClashApiSecret { get; init; }
    public string RemoteDns { get; init; } = "1.1.1.1";
    /// <summary>Resolver used for DIRECT traffic and for resolving proxy server names. Must be reachable on the physical interface.</summary>
    public string DirectDns { get; init; } = "1.1.1.1";
    public string LogLevel { get; init; } = "info";
    /// <summary>
    /// v0.4.28 (PoisonGuard companion): when set, a DoH server (8.8.8.8, IP-based — no bootstrap) is added to the
    /// DNS section and <c>route.default_domain_resolver</c> points at it — outbound SERVER names (the proxy endpoint
    /// itself) then resolve through an untamperable encrypted channel instead of the hijackable UDP resolver.
    /// Only enabled after PoisonGuard confirmed DoH is actually reachable. v0.4.29: the server carries NO
    /// <c>detour</c> — sing-box (1.11+) FATALs at start on a DNS transport detoured to an EMPTY direct outbound
    /// ("detour to an empty direct outbound makes no sense"), and no detour is equivalent anyway: with
    /// route.auto_detect_interface the transport dials the physical interface, exactly like the dns-local
    /// UDP server that always worked this way in the field.
    /// </summary>
    public bool ResolveViaDoh { get; init; }
    public IReadOnlyList<string> AlwaysDirectExecutables { get; init; } = [];
    /// <summary>
    /// Whether the upstream can carry IPv6. Most servers cannot; then tunneled IPv6 flows are rejected immediately (so
    /// browsers fall back to IPv4 at once instead of hanging on "network unreachable") and tunneled DNS returns A records only.
    /// </summary>
    public bool TunnelSupportsIpv6 { get; init; }
    /// <summary>When the upstream only accepts some destinations (WireGuard partial AllowedIPs), the rest goes DIRECT.</summary>
    public IReadOnlyList<string>? TunnelCidrs { get; init; }
    /// <summary>
    /// TLS root store. "mozilla" = bundled roots verified in Go; avoids a crash of sing-box in the Windows CryptoAPI chain
    /// verifier (CertFreeCertificateChain access violation) seen in the field. "system" restores the OS store.
    /// </summary>
    public string CertificateStore { get; init; } = "mozilla";
    /// <summary>
    /// v0.4.35 (log koalix-20261108, 0.4.34 session — «گوگل میاد ولی یوتیوب و اینستاگرام نه»): the proxy SERVER's own
    /// addresses, excluded from the TUN routes (<c>route_exclude_address</c>). Without this, xray.exe's connection to
    /// the worker entered the TUN too and was re-dialed by sing-box's gVisor stack: 169 of 385 dials died with
    /// "websocket: failed to dial … EOF" and 45 more with "read tcp 172.19.0.1 → worker: endpoint not connected",
    /// while the very same config on the same network outside the TUN had zero failures. Excluded, the engine talks
    /// to its server straight through the physical NIC (TLS fragments intact) — what every mainstream client does.
    /// </summary>
    public IReadOnlyList<string> BypassServerIps { get; init; } = [];

    /// <summary>The TUN inbound shared by the generated and the merged configs.</summary>
    internal JsonObject TunInbound()
    {
        var tun = new JsonObject
        {
            ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = TunInterfaceName,
            ["address"] = new JsonArray(TunAddressV4, TunAddressV6), ["mtu"] = Mtu,
            ["auto_route"] = true, ["strict_route"] = StrictRoute, ["stack"] = Stack,
        };
        var cidrs = BypassCidrs(BypassServerIps);
        if (cidrs.Count > 0) tun["route_exclude_address"] = new JsonArray(cidrs.Select(c => (JsonNode)c).ToArray());
        return tun;
    }

    /// <summary>Public, non-private unicast IPs only (never a whole range, never loopback/LAN) as host routes.</summary>
    public static List<string> BypassCidrs(IEnumerable<string> ips)
    {
        var list = new List<string>();
        foreach (var s in ips)
        {
            if (!System.Net.IPAddress.TryParse(s, out var a) || System.Net.IPAddress.IsLoopback(a)) continue;
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                if (b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127)) continue;
                list.Add(a + "/32");
            }
            else if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal
                     && !a.IsIPv6Multicast && (a.GetAddressBytes()[0] & 0xFE) != 0xFC)
                list.Add(a + "/128");
        }
        return list.Distinct().ToList();
    }
    /// <summary>Remote rule-set for Iranian domains (used only when the policy's IranDirectPreset is on).</summary>
    public string GeoSiteIrUrl { get; init; } = "https://cdn.jsdelivr.net/gh/Chocolate4U/Iran-sing-box-rules@rule-set/geosite-ir.srs";
    /// <summary>Remote rule-set for Iranian IP ranges (companion of <see cref="GeoSiteIrUrl"/>).</summary>
    public string GeoIpIrUrl { get; init; } = "https://cdn.jsdelivr.net/gh/Chocolate4U/Iran-sing-box-rules@rule-set/geoip-ir.srs";
    /// <summary>Where sing-box persists downloaded rule-sets between runs (null = in-memory only).</summary>
    public string? RuleSetCachePath { get; init; }
    /// <summary>
    /// v0.4.31 — the sinkhole kill-net: when set, the config's FIRST route rule REJECTS the ISP's observed
    /// poison pools (<see cref="PoisonGuard.SinkholeCidrs"/>). A leaked poisoned lookup then costs the browser
    /// one instant connection-refused instead of a dead tunnel dial that hangs until timeout — Chrome falls back
    /// to the clean address family / re-resolves through the tunneled DoH. Tied to the PoisonGuard switch.
    /// </summary>
    public bool RejectSinkholes { get; init; }
    /// <summary>v0.4.37 — the upstream sniffs and re-addresses flows by domain (the Xray chain): a tunneled app's
    /// sinkhole-bound flows go to it instead of the reject (see <see cref="SplitRuleCompiler.SinkholeRescue"/>).</summary>
    public bool SinkholeViaProxy { get; init; }
}

/// <summary>Generates sing-box 1.12+ configurations (TUN inbound on Wintun + process-aware routing).</summary>
public static class SingBoxConfigBuilder
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>String value of a node, or null for anything else (never throws on malformed input).</summary>
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// True for loopback/private/link-local/CGNAT addresses. WireGuard configs routinely point DNS at the peer's
    /// internal resolver (10.8.0.1, 172.16.x, 192.168.x…). Such resolvers never carry a public TLS certificate, so
    /// querying them over DoH produced "tls: failed to verify certificate: x509: … doesn't contain any IP SANs" in
    /// the field and EVERY tunneled lookup failed (browser shows nothing, counters stay at zero). Private resolvers
    /// are now queried over plain UDP through the tunnel; only public addresses keep DoH.
    /// </summary>
    public static bool IsPrivateDnsAddress(string? server)
    {
        if (server is null || !IPAddress.TryParse(server, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] is 10 or 169 or 100 or 192 || (b[0] == 172 && b[1] is >= 16 and <= 31);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80); // ULA fc00::/7, link-local fe80::/10
        }
        return false;
    }

    /// <summary>The tunneled (remote) DNS server object for the given address: DoH for public IPs/hostnames,
    /// plain UDP for private resolvers; hostname servers get <c>domain_resolver</c> so sing-box 1.12+ can
    /// bootstrap them over the local resolver (route.default_domain_resolver alone is not honored everywhere).</summary>
    public static JsonObject RemoteDnsServer(string remoteDns, string localTag, string detourTag)
    {
        var s = new JsonObject { ["tag"] = SplitRuleCompiler.RemoteDnsTag, ["server"] = remoteDns, ["detour"] = detourTag };
        if (IsPrivateDnsAddress(remoteDns)) s["type"] = "udp";
        else
        {
            s["type"] = "https";
            if (!IPAddress.TryParse(remoteDns, out _)) s["domain_resolver"] = localTag;
        }
        return s;
    }

    /// <summary>Returns true when sing-box can natively handle the node; otherwise Xray must be used as the upstream.</summary>
    public static bool IsNativelySupported(ProxyNode n, out string? reason)
    {
        reason = n.Transport switch
        {
            "xhttp" or "splithttp" => "Transport XHTTP requires Xray",
            "kcp" or "mkcp" => "mKCP requires Xray",
            _ => null,
        };
        if (reason is null && n.Transport == "tcp" && n.HeaderType == "http") reason = "TCP HTTP header obfuscation requires Xray";
        if (reason is null && n.Flow is { Length: > 0 } f && f != "xtls-rprx-vision") reason = $"Flow '{f}' requires Xray";
        return reason is null;
    }

    public static JsonObject Outbound(ProxyNode n, string tag = SplitRuleCompiler.ProxyTag)
    {
        var o = new JsonObject { ["type"] = TypeName(n.Protocol), ["tag"] = tag, ["server"] = n.Server, ["server_port"] = n.Port };
        switch (n.Protocol)
        {
            case ProxyProtocol.VMess:
                o["uuid"] = n.Uuid; o["security"] = n.VmessSecurity ?? "auto"; o["alter_id"] = n.AlterId; break;
            case ProxyProtocol.VLess:
                o["uuid"] = n.Uuid; if (!string.IsNullOrEmpty(n.Flow)) o["flow"] = n.Flow; o["packet_encoding"] = "xudp"; break;
            case ProxyProtocol.Trojan:
                o["password"] = n.Password; break;
            case ProxyProtocol.Shadowsocks:
                o["method"] = n.Method; o["password"] = n.Password;
                if (!string.IsNullOrEmpty(n.Plugin)) { o["plugin"] = n.Plugin; o["plugin_opts"] = n.PluginOpts ?? ""; }
                break;
            case ProxyProtocol.Hysteria2:
                o["password"] = n.Password;
                if (n.Obfs == "salamander") o["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = n.ObfsPassword };
                break;
            case ProxyProtocol.Tuic:
                o["uuid"] = n.Uuid; o["password"] = n.Password; o["congestion_control"] = n.CongestionControl ?? "bbr"; break;
            case ProxyProtocol.Socks:
                o["version"] = "5"; if (n.Username is not null) { o["username"] = n.Username; o["password"] = n.Password; } break;
            case ProxyProtocol.Http:
                if (n.Username is not null) { o["username"] = n.Username; o["password"] = n.Password; } break;
        }
        var tls = Tls(n);
        if (tls is not null) o["tls"] = tls;
        var transport = Transport(n);
        if (transport is not null) o["transport"] = transport;
        return o;
    }

    private static string TypeName(ProxyProtocol p) => p switch
    {
        ProxyProtocol.VMess => "vmess", ProxyProtocol.VLess => "vless", ProxyProtocol.Trojan => "trojan",
        ProxyProtocol.Shadowsocks => "shadowsocks", ProxyProtocol.Hysteria2 => "hysteria2", ProxyProtocol.Tuic => "tuic",
        ProxyProtocol.Socks => "socks", _ => "http",
    };

    private static JsonObject? Tls(ProxyNode n)
    {
        var quic = n.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic;
        if (n.Security is not ("tls" or "reality") && !quic) return null;
        var t = new JsonObject { ["enabled"] = true, ["server_name"] = n.Sni ?? n.Host ?? n.Server };
        if (n.AllowInsecure) t["insecure"] = true;
        var alpn = n.Alpn.Length > 0 ? n.Alpn : n.Protocol == ProxyProtocol.Tuic ? ["h3"] : [];
        if (alpn.Length > 0) t["alpn"] = new JsonArray(alpn.Select(a => (JsonNode)a).ToArray());
        // uTLS for every non-QUIC TLS outbound (chrome when unset): the bare Go ClientHello is fingerprinted and
        // RST'd by Iranian DPI — same reason the Xray builder now always sets a fingerprint (v0.4.8 field fix).
        // v0.4.22: SafeFingerprint — never a raw value like "unsafe" (sing-box rejects it, v0.4.21 field log).
        if (!quic && (n.Fingerprint is not null || n.Security is "tls" or "reality"))
            t["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = n.SafeFingerprint };
        if (n.Security == "reality")
            t["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = n.RealityPublicKey, ["short_id"] = n.RealityShortId ?? "" };
        return t;
    }

    private static JsonObject? Transport(ProxyNode n) => n.Transport switch
    {
        "ws" => WithHost(new JsonObject { ["type"] = "ws", ["path"] = n.Path ?? "/" }, n.Host, asHeader: true),
        "grpc" => new JsonObject { ["type"] = "grpc", ["service_name"] = n.ServiceName ?? n.Path ?? "" },
        "http" => new JsonObject { ["type"] = "http", ["path"] = n.Path ?? "/", ["host"] = n.Host is null ? new JsonArray() : new JsonArray(n.Host.Split(',').Select(h => (JsonNode)h.Trim()).ToArray()) },
        "httpupgrade" => WithHost(new JsonObject { ["type"] = "httpupgrade", ["path"] = n.Path ?? "/" }, n.Host, asHeader: false),
        "quic" when n.Protocol is not (ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic) => new JsonObject { ["type"] = "quic" },
        _ => null,
    };

    private static JsonObject WithHost(JsonObject o, string? host, bool asHeader)
    {
        if (string.IsNullOrEmpty(host)) return o;
        if (asHeader) o["headers"] = new JsonObject { ["Host"] = host }; else o["host"] = host;
        return o;
    }

    /// <summary>Full client configuration: Wintun TUN inbound + private health inbound + proxy/direct outbounds + process routing.</summary>
    public static JsonObject Build(JsonNode proxyOutboundOrEndpoint, bool isEndpoint, SplitPolicy policy, SingBoxBuildOptions o)
    {
        var compiled = SplitRuleCompiler.Compile(policy, o.AlwaysDirectExecutables, SplitRuleCompiler.ProxyTag, o.TunnelSupportsIpv6, o.TunnelCidrs);
        // v0.4.31 — the sinkhole kill-net goes FIRST (before ip_is_private→direct, which would otherwise match
        // the IPv4 pool and send the sinkhole DIRECT instead of killing it).
        if (o.RejectSinkholes)
        {
            compiled.RouteRules.Insert(0, new JsonObject
            {
                ["action"] = "reject",
                ["ip_cidr"] = new JsonArray(PoisonGuard.SinkholeCidrs.Select(c => (JsonNode)c).ToArray()),
            });
            // v0.4.37 — rescue (proxy + upstream domain sniffing) goes in front of the reject.
            if (o.SinkholeViaProxy)
            {
                var rescue = SplitRuleCompiler.SinkholeRescue(policy, o.AlwaysDirectExecutables, PoisonGuard.SinkholeCidrs);
                for (var i = rescue.Count - 1; i >= 0; i--) { var r = rescue[i]!; rescue.RemoveAt(i); compiled.RouteRules.Insert(0, r); }
            }
        }
        compiled.RouteRules.Insert(SplitRuleCompiler.AfterSniff(compiled.RouteRules), new JsonObject { ["inbound"] = "health-in", ["outbound"] = SplitRuleCompiler.ProxyTag });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = o.LogLevel, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    RemoteDnsServer(o.RemoteDns, SplitRuleCompiler.LocalDnsTag, SplitRuleCompiler.ProxyTag),
                    new JsonObject { ["type"] = "udp", ["tag"] = SplitRuleCompiler.LocalDnsTag, ["server"] = o.DirectDns },
                },
                ["rules"] = compiled.DnsRules,
                ["final"] = compiled.DnsFinal,
                ["strategy"] = compiled.DnsIpv4Only ? "ipv4_only" : "prefer_ipv4",
            },
            ["certificate"] = new JsonObject { ["store"] = o.CertificateStore },
            ["inbounds"] = new JsonArray
            {
                o.TunInbound(),
                new JsonObject { ["type"] = "mixed", ["tag"] = "health-in", ["listen"] = "127.0.0.1", ["listen_port"] = o.HealthPort },
            },
            ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = SplitRuleCompiler.DirectTag } },
            ["route"] = new JsonObject
            {
                ["rules"] = compiled.RouteRules,
                ["final"] = compiled.RouteFinal,
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = SplitRuleCompiler.LocalDnsTag,
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{o.ClashApiPort}", ["secret"] = o.ClashApiSecret },
            },
        };
        // v0.4.28 — poison-proof resolution of the outbound server name (see SingBoxBuildOptions.ResolveViaDoh).
        if (o.ResolveViaDoh) EnableDohResolver(root);
        if (isEndpoint) root["endpoints"] = new JsonArray(proxyOutboundOrEndpoint);
        else ((JsonArray)root["outbounds"]!).Insert(0, proxyOutboundOrEndpoint);
        // Iran-direct preset: meaningless in Include mode (everything is direct by default there).
        if (policy.IranDirectPreset && policy.Mode != SplitMode.Include)
            ApplyIranPreset(root, compiled.RouteRules, compiled.DnsRules, o);
        return root;
    }

    /// <summary>
    /// v0.4.28 — adds the poison-proof DoH resolver used for outbound server names and points
    /// <c>route.default_domain_resolver</c> at it. The server is IP-based (no bootstrap). v0.4.29 field fix:
    /// it carries NO <c>detour</c> — sing-box (1.11+) refuses to start with "detour to an empty direct outbound
    /// makes no sense" (FATAL, engine code 1) when a DNS transport detours to our bare {"type":"direct"} outbound,
    /// which killed EVERY connect of a domain-based profile in the field (log 353: six connects, six corpses).
    /// The detour was also redundant: DNS transports dial directly (they do not re-enter route rules), and with
    /// route.auto_detect_interface their sockets bind to the physical interface — the exact path the dns-local
    /// UDP server has always used without a detour.
    /// </summary>
    public static void EnableDohResolver(JsonObject root)
    {
        if (root["dns"] is not JsonObject dns || dns["servers"] is not JsonArray servers) return;
        if (!servers.OfType<JsonObject>().Any(s => (string?)s["tag"] == DohDnsTag))
            // NO "detour" — see the doc comment above. The empty-direct detour FATALs sing-box at start.
            servers.Insert(0, new JsonObject { ["type"] = "https", ["tag"] = DohDnsTag, ["server"] = "8.8.8.8" });
        if (root["route"] is JsonObject route)
            route["default_domain_resolver"] = DohDnsTag;
    }

    public const string DohDnsTag = "koalix-dns-doh";

    private static JsonObject RemoteRuleSet(string tag, string url) => new()
    {
        ["type"] = "remote", ["tag"] = tag, ["format"] = "binary", ["url"] = url,
        ["download_detour"] = SplitRuleCompiler.DirectTag, ["update_interval"] = "168h",
    };

    /// <summary>
    /// Adds the "Iran direct, everything else tunnel" preset on top of an already-compiled config:
    /// a route rule sending rule-set matches (Iranian domains + IP ranges) DIRECT before the tunnel final,
    /// a DNS rule resolving Iranian domains with the local resolver, the two remote rule-set definitions
    /// (downloaded once over the physical interface, then cached on disk), and the sing-box cache file.
    /// <paramref name="localDnsTag"/> is the tag of a local resolver that exists in THIS config (generated
    /// configs use dns-local; merged user configs have their own tags — passing the fixed name used to make
    /// the linter reject every panel config with the Iran preset enabled).
    /// </summary>
    private static void ApplyIranPreset(JsonObject root, JsonArray routeRules, JsonArray? dnsRules, SingBoxBuildOptions o, string? localDnsTag = null)
    {
        routeRules.Add(new JsonObject { ["rule_set"] = new JsonArray("geosite-ir", "geoip-ir"), ["outbound"] = SplitRuleCompiler.DirectTag });
        var local = localDnsTag ?? SplitRuleCompiler.LocalDnsTag;
        dnsRules?.Insert(0, new JsonObject { ["rule_set"] = new JsonArray("geosite-ir"), ["server"] = local });
        var route = (JsonObject)root["route"]!;
        var sets = route["rule_set"] as JsonArray ?? (JsonArray)(route["rule_set"] = new JsonArray());
        if (!sets.OfType<JsonObject>().Any(s => (string?)s["tag"] == "geosite-ir")) sets.Add(RemoteRuleSet("geosite-ir", o.GeoSiteIrUrl));
        if (!sets.OfType<JsonObject>().Any(s => (string?)s["tag"] == "geoip-ir")) sets.Add(RemoteRuleSet("geoip-ir", o.GeoIpIrUrl));
        if (o.RuleSetCachePath is not null)
        {
            var exp = root["experimental"] as JsonObject ?? (JsonObject)(root["experimental"] = new JsonObject());
            exp["cache_file"] = new JsonObject { ["enabled"] = true, ["path"] = o.RuleSetCachePath };
        }
    }

    /// <summary>
    /// Chooses which outbound of a user config becomes the tunnel target. Panel configs (BPB, Marzban, …) list
    /// dozens of individual nodes first and put their <c>selector</c>/<c>urltest</c> groups — the entry point their own
    /// <c>route.final</c> points at — at the END. Blindly taking the first candidate hardwires the health check and
    /// all tunneled traffic to a single (often dead, often domain-based) node: the config can never connect even
    /// though its auto-selecting group would have picked a working node. Preference order:
    /// 1. the user's <c>route.final</c> (the config author's intent), 2. the first selector group,
    /// 3. the first urltest group, 4. the conventional "proxy" tag, 5. the first candidate.
    /// </summary>
    public static string PickTunnelTarget(JsonObject user, IReadOnlyList<string> candidates)
    {
        var final = Str(user["route"]?["final"]);
        if (final is not null && candidates.Contains(final)) return final;
        foreach (var groupType in new[] { "selector", "urltest" })
            foreach (var key in new[] { "outbounds", "endpoints" })
                if (user[key] is JsonArray arr)
                    foreach (var x in arr.OfType<JsonObject>())
                        if ((string?)x["type"] == groupType && (string?)x["tag"] is { } tag && candidates.Contains(tag))
                            return tag;
        return candidates.Contains("proxy") ? "proxy" : candidates[0];
    }

    /// <summary>Result of adapting the user's DNS section: tags that actually exist in it.</summary>
    private sealed record DnsPlan(string? LocalTag, string? RemoteTag);

    /// <summary>
    /// Adapts the compiled DNS rules/final to a user config's own DNS section instead of assuming KOALIX's fixed
    /// <c>dns-remote</c>/<c>dns-local</c> tags (which do not exist there — the Iran preset used to reference
    /// <c>dns-local</c> and every user config with different tags was rejected by the linter).
    /// Classifies the user's servers (a detour other than "direct" marks a remote resolver), creates the missing
    /// kind only when the user config has none, remaps the compiled per-process rules onto the real tags, and
    /// makes sure a final resolver exists — a local (non-detoured) final while Tunnel-DNS is on reproduces the
    /// v0.3.6 field bug: on Windows svchost sends the queries, so tunneled apps receive the ISP's poisoned answers.
    /// Returns null when the user's DNS section is malformed (left untouched; `sing-box check` reports it).
    /// </summary>
    private static DnsPlan? MergeUserDns(JsonObject root, JsonArray compiledDnsRules, SplitPolicy policy, string userProxyTag, SingBoxBuildOptions o)
    {
        if (root["dns"] is not JsonObject dns)
        {
            if (root["dns"] is not null) return null; // malformed (not an object): leave for the engine to report
            dns = new JsonObject();
            root["dns"] = dns;
        }
        if (dns["servers"] is not null and not JsonArray) return null;
        if (dns["rules"] is not null and not JsonArray) return null; // malformed rules: leave for the engine to report
        var servers = dns["servers"] as JsonArray ?? (JsonArray)(dns["servers"] = new JsonArray());

        string? localTag = null, remoteTag = null;
        foreach (var s in servers.OfType<JsonObject>())
        {
            if (Str(s["tag"]) is not { } tag) continue;
            var detour = Str(s["detour"]);
            if (detour is not null && detour != SplitRuleCompiler.DirectTag) { if (remoteTag is null) remoteTag = tag; }
            else if (localTag is null) localTag = tag;
        }

        string FreeTag(string baseTag)
        {
            var used = servers.OfType<JsonObject>().Select(s => Str(s["tag"])).OfType<string>().ToHashSet();
            var t = baseTag; var n = 2;
            while (used.Contains(t)) t = $"koalix-{baseTag}-{n++}";
            return t;
        }

        if (localTag is null)
        {
            localTag = FreeTag(SplitRuleCompiler.LocalDnsTag);
            servers.Add(new JsonObject { ["type"] = "udp", ["tag"] = localTag, ["server"] = o.DirectDns });
        }
        if (remoteTag is null)
        {
            remoteTag = FreeTag(SplitRuleCompiler.RemoteDnsTag);
            var created = RemoteDnsServer(o.RemoteDns, localTag, userProxyTag);
            created["tag"] = remoteTag;
            servers.Add(created);
        }

        var rules = dns["rules"] as JsonArray ?? (JsonArray)(dns["rules"] = new JsonArray());
        foreach (var r in compiledDnsRules.OfType<JsonObject>())
        {
            var rule = (JsonObject)r.DeepClone();
            if (Str(rule["server"]) == SplitRuleCompiler.RemoteDnsTag) rule["server"] = remoteTag;
            else if (Str(rule["server"]) == SplitRuleCompiler.LocalDnsTag) rule["server"] = localTag;
            rules.Add(rule);
        }

        var final = Str(dns["final"]);
        if (final is null) dns["final"] = policy.TunnelDns ? remoteTag : localTag;
        else if (policy.TunnelDns && final != remoteTag)
        {
            // The authored final is a plain local resolver: with Tunnel-DNS on, hijacked queries from tunneled
            // apps would be answered by the ISP (sinkhole) — switch them to the remote resolver (v0.3.6 lesson).
            var srv = servers.OfType<JsonObject>().FirstOrDefault(s => Str(s["tag"]) == final);
            if (srv is not null && (Str(srv["detour"]) is null || Str(srv["detour"]) == SplitRuleCompiler.DirectTag))
                dns["final"] = remoteTag;
        }
        return new DnsPlan(localTag, remoteTag);
    }

    /// <summary>
    /// Migrates sing-box DNS options that are deprecated in 1.14 and removed (FATAL) from 1.16 on — the
    /// EngineManager installs the newest stable sing-box, so merged output must stay forward-compatible:
    ///  - <c>independent_cache</c>: redundant since the new (1.12+) DNS server format gives every server its own
    ///    cache; only a warning today, an error the day the engine updates to 1.16;
    ///  - the legacy <c>strategy</c> DNS rule action option: already FATAL TODAY on 1.14.x
    ///    ("initialize dns router: …") when the same config also contains any query_type/ip_version DNS rule or
    ///    rule-set — which every BPB-style panel config carries.
    /// When <paramref name="ipv4Only"/> is set (Block-IPv6-leaks, or an upstream without IPv6), the equivalent
    /// modern form — the global <c>dns.strategy</c> — replaces the removed per-rule option, so tunneled apps still
    /// never receive AAAA answers for IPv6-less upstreams. The user's own global <c>dns.strategy</c> is respected
    /// whenever KOALIX does not need IPv4-only.
    /// </summary>
    private static void MigrateDeprecatedDns(JsonObject? dns, bool ipv4Only)
    {
        if (dns is null) return;
        dns.Remove("independent_cache");
        if (dns["rules"] is JsonArray rules)
            foreach (var r in rules.OfType<JsonObject>())
                r.Remove("strategy");
        if (ipv4Only) dns["strategy"] = "ipv4_only";
    }

    /// <summary>
    /// Injects KOALIX routing into a user-provided sing-box JSON while preserving every field we do not own.
    /// The user's outbound named <paramref name="userProxyTag"/> becomes the tunnel target (see <see cref="PickTunnelTarget"/>).
    /// </summary>
    public static JsonObject MergeUserConfig(JsonObject user, string userProxyTag, SplitPolicy policy, SingBoxBuildOptions o)
    {
        var root = (JsonObject)user.DeepClone();
        var inbounds = root["inbounds"] as JsonArray ?? (JsonArray)(root["inbounds"] = new JsonArray());
        if (!inbounds.OfType<JsonObject>().Any(i => (string?)i["type"] == "tun"))
            inbounds.Add(o.TunInbound());
        // Security: panel configs ship a mixed inbound on 0.0.0.0 (unauthenticated proxy open to the LAN).
        // KOALIX runs elevated while connected — rebind every non-loopback listener to loopback, keeping the port.
        foreach (var i in inbounds.OfType<JsonObject>())
        {
            var listen = (string?)i["listen"];
            if ((string?)i["type"] != "tun" && listen is not null && listen is not ("127.0.0.1" or "localhost" or "::1"))
                i["listen"] = "127.0.0.1";
        }
        inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "health-in", ["listen"] = "127.0.0.1", ["listen_port"] = o.HealthPort });

        var outbounds = root["outbounds"] as JsonArray ?? (JsonArray)(root["outbounds"] = new JsonArray());
        if (!outbounds.OfType<JsonObject>().Any(x => (string?)x["tag"] == SplitRuleCompiler.DirectTag))
            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = SplitRuleCompiler.DirectTag });

        var route = root["route"] as JsonObject ?? (JsonObject)(root["route"] = new JsonObject());
        var existingRules = route["rules"] as JsonArray ?? new JsonArray();
        route.Remove("rules");
        var compiled = SplitRuleCompiler.Compile(policy, o.AlwaysDirectExecutables, userProxyTag, o.TunnelSupportsIpv6, o.TunnelCidrs);
        if (root["certificate"] is null) root["certificate"] = new JsonObject { ["store"] = o.CertificateStore };
        compiled.RouteRules.Insert(SplitRuleCompiler.AfterSniff(compiled.RouteRules), new JsonObject { ["inbound"] = "health-in", ["outbound"] = userProxyTag });
        // DNS first: the Iran preset below needs a local-resolver tag that actually exists in the user's section.
        var dnsPlan = MergeUserDns(root, compiled.DnsRules, policy, userProxyTag, o);
        // Then retire deprecated DNS options (independent_cache, legacy per-rule `strategy`) and carry the
        // IPv4-only protection over to the global dns.strategy — see MigrateDeprecatedDns.
        MigrateDeprecatedDns(root["dns"] as JsonObject, compiled.DnsIpv4Only);
        // Keep the user's route rules in EVERY mode — compiled rules come first and encode the app split, but the
        // author's own rules (hijack-dns for their TUN, udp-reject for worker configs, clash-mode switches…) must
        // not be silently dropped in Include/Exclude mode as they were before.
        foreach (var r in existingRules.ToArray()) { existingRules.Remove(r); compiled.RouteRules.Add(r); }
        if (policy.Mode == SplitMode.FullTunnel)
        {
            if (route["final"] is null) route["final"] = userProxyTag;
        }
        else route["final"] = compiled.RouteFinal;
        if (policy.IranDirectPreset && policy.Mode != SplitMode.Include)
            ApplyIranPreset(root, compiled.RouteRules, (root["dns"] as JsonObject)?["rules"] as JsonArray, o, dnsPlan?.LocalTag);
        route["rules"] = compiled.RouteRules;
        route["auto_detect_interface"] = true;
        // sing-box 1.12+ refuses to start when a domain-server outbound has no resolver; user configs that set
        // `domain_resolver` per outbound work either way, everything else gets a safe default. With the
        // v0.4.28 PoisonGuard companion that default is the untamperable DoH resolver (the user's own
        // explicit default_domain_resolver, when authored, is still respected).
        if (route["default_domain_resolver"] is null)
        {
            if (o.ResolveViaDoh) EnableDohResolver(root);
            else if (dnsPlan?.LocalTag is { } lt) route["default_domain_resolver"] = lt;
        }

        var exp = root["experimental"] as JsonObject ?? (JsonObject)(root["experimental"] = new JsonObject());
        // Keep the author's clash default mode ("Rule"/"Global"/"Direct") so their clash_mode rules behave as designed.
        var userMode = Str(exp["clash_api"]?["default_mode"]);
        exp["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{o.ClashApiPort}", ["secret"] = o.ClashApiSecret };
        if (userMode is not null) ((JsonObject)exp["clash_api"]!)["default_mode"] = userMode;
        return root;
    }

    public static string ToJson(JsonNode node) => node.ToJsonString(Pretty);

    /// <summary>Lists outbound/endpoint tags of a user config that can act as a tunnel target.</summary>
    public static IReadOnlyList<string> ProxyCandidates(JsonObject user)
    {
        var tags = new List<string>();
        foreach (var key in new[] { "outbounds", "endpoints" })
            if (user[key] is JsonArray arr)
                foreach (var x in arr.OfType<JsonObject>())
                    if ((string?)x["type"] is { } t && t is not ("direct" or "block" or "dns") && (string?)x["tag"] is { } tag) tags.Add(tag);
        return tags;
    }
}
