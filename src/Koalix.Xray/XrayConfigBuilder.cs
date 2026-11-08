using System.Text.Json.Nodes;
using Koalix.Tunneling;

namespace Koalix.Xray;

/// <summary>
/// v0.4.28 — the engine tweaks requested by the external audit: TLS ClientHello <b>fragmentation</b> (splits the
/// TLS hello into 100–200-byte pieces so DPI cannot fingerprint the handshake — the field logs were full of
/// "wsarecv: connection forcibly closed" EOFs on TLS dials) and <b>mux</b> (multiplexes many streams over one
/// channel — opt-in because it needs a mux-capable server). Both are client-side, Xray-only.
/// </summary>
public sealed record XrayTweaks(bool Fragment, bool Mux);

/// <summary>Generates Xray-core client configurations: a loopback-only SOCKS inbound and one proxy outbound.</summary>
public static class XrayConfigBuilder
{
    /// <param name="serverIps">v0.4.34 — IPv4 addresses KOALIX already resolved for the server's name (PoisonGuard,
    /// at connect time). Pinned as static <c>dns.hosts</c> so xray.exe never has to look the name up itself.</param>
    public static JsonObject Build(ProxyNode n, int socksPort, string logLevel = "warning", XrayTweaks? tweaks = null,
        IReadOnlyList<string>? serverIps = null) => new()
    {
        ["log"] = new JsonObject { ["loglevel"] = logLevel },
        // v0.4.28 — an independent DNS module (IP-based DoH, no bootstrap needed). The proxy outbound dials its
        // server by name through the system resolver, so the real poisoning defence is the connect-time
        // PoisonGuard in Koalix.Tunneling; this section makes the config self-sufficient the moment any routing
        // rule (or a future Xray that resolves outbound names through the DNS module) needs a resolver, and
        // matches the shape every mainstream client ships.
        ["dns"] = DnsSection(n.Server, serverIps),
        ["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "socks-in", ["protocol"] = "socks", ["listen"] = "127.0.0.1", ["port"] = socksPort,
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" },
                ["sniffing"] = Sniffing(),
            },
        },
        ["outbounds"] = new JsonArray { Outbound(n, tweaks), new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" } },
    };

    /// <summary>
    /// v0.4.37 (log koalix-diagnostics-20261108-133946): sing-box hands the SOCKS inbound bare IPs — whatever the
    /// (possibly ISP-poisoned) DNS answered. Sniffing the TLS SNI / HTTP Host and overriding the destination makes the
    /// far end dial the DOMAIN and resolve it itself, so a leaked poisoned lookup (10.10.34.x for YouTube) no longer
    /// decides where the tunnel goes. <c>routeOnly=false</c> is what actually rewrites the destination.
    /// </summary>
    public static JsonObject Sniffing() => new()
    {
        ["enabled"] = true,
        ["destOverride"] = new JsonArray("http", "tls"),
        ["routeOnly"] = false,
    };

    /// <summary>
    /// v0.4.34 (log koalix-20261108 — 0.4.33 broke every connection): once <c>sockopt.domainStrategy</c> is set,
    /// Xray resolves the server name through THIS module instead of Go's system resolver — and a DoH-only module is
    /// unreachable on Iranian networks ("Post https://8.8.8.8/dns-query: context deadline exceeded" → "failed to
    /// resolve ip > empty response" for every dial). Now: the server's own name is pinned to the IPs KOALIX resolved
    /// before start (<c>hosts</c> — no lookup at all), the system resolver (<c>localhost</c>) comes first for anything
    /// else, and DoH is only the last resort. Field-checked against the real worker: 204/200 in &lt;2 s, zero queries.
    /// </summary>
    public static JsonObject DnsSection(string? server = null, IReadOnlyList<string>? serverIps = null)
    {
        var dns = new JsonObject
        {
            ["servers"] = new JsonArray(
            "localhost",
            new JsonObject { ["address"] = "https://8.8.8.8/dns-query" },
            new JsonObject { ["address"] = "https://9.9.9.9/dns-query" },
            new JsonObject { ["address"] = "https://1.1.1.1/dns-query" }),
            ["queryStrategy"] = "UseIPv4",
            ["tag"] = "koalix-dns",
        };
        var ips = serverIps?.Where(ip => System.Net.IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Distinct().ToList();
        if (server is not null && !System.Net.IPAddress.TryParse(server, out _) && ips is { Count: > 0 })
            dns["hosts"] = new JsonObject { [server] = new JsonArray(ips.Select(ip => (JsonNode)ip).ToArray()) };
        return dns;
    }

    public static JsonObject Outbound(ProxyNode n, XrayTweaks? tweaks = null)
    {
        var o = new JsonObject { ["tag"] = "proxy" };
        switch (n.Protocol)
        {
            case ProxyProtocol.VMess:
                o["protocol"] = "vmess";
                o["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = n.Server, ["port"] = n.Port,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = n.Uuid, ["alterId"] = n.AlterId, ["security"] = n.VmessSecurity ?? "auto" }) }) };
                break;
            case ProxyProtocol.VLess:
                o["protocol"] = "vless";
                var user = new JsonObject { ["id"] = n.Uuid, ["encryption"] = "none" };
                if (!string.IsNullOrEmpty(n.Flow)) user["flow"] = n.Flow;
                o["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = n.Server, ["port"] = n.Port, ["users"] = new JsonArray(user) }) };
                break;
            case ProxyProtocol.Trojan:
                o["protocol"] = "trojan";
                o["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = n.Server, ["port"] = n.Port, ["password"] = n.Password }) };
                break;
            case ProxyProtocol.Shadowsocks:
                o["protocol"] = "shadowsocks";
                o["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = n.Server, ["port"] = n.Port, ["method"] = n.Method, ["password"] = n.Password }) };
                break;
            case ProxyProtocol.Socks:
                o["protocol"] = "socks";
                var srv = new JsonObject { ["address"] = n.Server, ["port"] = n.Port };
                if (n.Username is not null) srv["users"] = new JsonArray(new JsonObject { ["user"] = n.Username, ["pass"] = n.Password });
                o["settings"] = new JsonObject { ["servers"] = new JsonArray(srv) };
                break;
            default:
                throw new NotSupportedException($"{n.Protocol} is not supported by Xray-core; use sing-box");
        }
        o["streamSettings"] = Stream(n, tweaks);
        if (tweaks is { Mux: true } && MuxAllowed(n))
            o["mux"] = new JsonObject { ["enabled"] = true, ["concurrency"] = 8 };
        return o;
    }

    /// <summary>Mux only when the protocol/transport actually supports it (never with Vision flow, XHTTP or QUIC).</summary>
    private static bool MuxAllowed(ProxyNode n) =>
        n.Protocol is ProxyProtocol.VMess or ProxyProtocol.VLess or ProxyProtocol.Trojan or ProxyProtocol.Shadowsocks
        && string.IsNullOrEmpty(n.Flow)
        && n.Transport is not ("xhttp" or "splithttp" or "quic" or "mkcp");

    private static JsonObject Stream(ProxyNode n, XrayTweaks? tweaks = null)
    {
        var network = n.Transport switch { "http" => "h2", "splithttp" => "xhttp", "mkcp" => "kcp", var t => t };
        var s = new JsonObject { ["network"] = network, ["security"] = n.Security };
        switch (network)
        {
            case "ws": s["wsSettings"] = new JsonObject { ["path"] = n.Path ?? "/", ["host"] = n.Host ?? "" }; break;
            case "grpc": s["grpcSettings"] = new JsonObject { ["serviceName"] = n.ServiceName ?? n.Path ?? "" }; break;
            case "h2": s["httpSettings"] = new JsonObject { ["path"] = n.Path ?? "/", ["host"] = n.Host is null ? new JsonArray() : new JsonArray(n.Host.Split(',').Select(h => (JsonNode)h.Trim()).ToArray()) }; break;
            case "httpupgrade": s["httpupgradeSettings"] = new JsonObject { ["path"] = n.Path ?? "/", ["host"] = n.Host ?? "" }; break;
            case "xhttp": s["xhttpSettings"] = new JsonObject { ["path"] = n.Path ?? "/", ["host"] = n.Host ?? "" }; break;
            case "kcp": s["kcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = n.HeaderType ?? "none" } }; break;
            case "tcp" when n.HeaderType == "http":
                s["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = new JsonObject {
                    ["path"] = new JsonArray(n.Path ?? "/"), ["headers"] = new JsonObject { ["Host"] = new JsonArray(n.Host ?? n.Server) } } } };
                break;
        }
        if (n.Security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = n.Sni ?? n.Host ?? n.Server, ["allowInsecure"] = n.AllowInsecure };
            if (n.Alpn.Length > 0) tls["alpn"] = new JsonArray(n.Alpn.Select(a => (JsonNode)a).ToArray());
            // uTLS is ALWAYS set (chrome when the config doesn't pick one): with the bare Go ClientHello, Iranian
            // DPI fingerprints the TLS handshake and RSTs it — the field log was full of "wsarecv: An existing
            // connection was forcibly closed by the remote host" while the very same configs worked in clients
            // that default to uTLS (v2rayN/Hiddify). uTLS chrome is compatible with every standard TLS server.
            tls["fingerprint"] = n.SafeFingerprint;
            s["tlsSettings"] = tls;
        }
        else if (n.Security == "reality")
            s["realitySettings"] = new JsonObject { ["serverName"] = n.Sni ?? n.Server, ["fingerprint"] = n.SafeFingerprint, ["publicKey"] = n.RealityPublicKey, ["shortId"] = n.RealityShortId ?? "" };
        // v0.4.28 — ClientHello fragmentation (audit suggestion): only for TLS/REALITY dials over a TCP transport
        // (QUIC is UDP, mKCP is UDP — fragmentation does not apply), opt-out via Settings.
        if (tweaks is { Fragment: true } && n.Security is "tls" or "reality"
            && n.Transport is "tcp" or "ws" or "grpc" or "http" or "httpupgrade")
            s["sockopt"] = new JsonObject
            {
                ["fragment"] = new JsonObject { ["packets"] = "tlshello", ["length"] = "100-200", ["interval"] = "10-20" },
                ["tcpNoDelay"] = true,
            };
        // v0.4.33 (log koalix-20261107, 0.4.32 session): xray.exe resolved the worker name to BOTH families and
        // sometimes dialed its IPv6 address. Under TUN that dial is accepted instantly by the gVisor stack while the
        // real IPv6 path is dead, so the tunnel connection hung ~14 s ("open connection to [2a06:98c1:3122::]:443 …
        // operation was canceled"). IPv4 only for the server's own name — resolved from dns.hosts (see DnsSection).
        if (!System.Net.IPAddress.TryParse(n.Server, out _))
        {
            var so = s["sockopt"] as JsonObject ?? new JsonObject();
            so["domainStrategy"] = "UseIPv4";
            s["sockopt"] = so;
        }
        return s;
    }
}
