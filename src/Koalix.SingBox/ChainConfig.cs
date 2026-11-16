using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.SplitTunneling;
using Koalix.Tunneling;

namespace Koalix.SingBox;

/// <summary>What the chain (config combination) does with the selected tunnel. v0.4.47.</summary>
public enum ChainMode
{
    /// <summary>Gaming split: game UDP exits through a direct Cloudflare WARP tunnel, everything else (TCP) keeps the
    /// selected config. The practical free setup for worker configs — they are TCP-only (their own route rules reject
    /// UDP) and can never carry game traffic; WARP is a real UDP VPN the ISP rarely blocks.</summary>
    WarpGaming,
    /// <summary>Full WARP exit: ALL traffic (TCP + UDP) leaves through WARP; the selected config stays as fallback for
    /// the tunnel DNS only when WARP-side resolution is impossible. The escape hatch when the worker domain is dead.</summary>
    WarpExit,
    /// <summary>The classic "chain": the WARP endpoint is dialed THROUGH the selected config (detour). Works only when
    /// the upstream really carries UDP (real Xray/sing-box servers, hysteria2, tuic…). With Cloudflare-Worker configs
    /// the WireGuard handshake cannot travel through a TCP-only WebSocket — KOALIX lets it be selected (other clients
    /// offer it too) but the health check then reports the chain as broken, with this exact reason in the log.</summary>
    WarpOverProxy,
    /// <summary>Psiphon exit through the Psiphon 3 Windows client's local SOCKS proxy (KOALIX → 127.0.0.1:port →
    /// Psiphon). For unblocking web traffic; Psiphon's local SOCKS does not carry game UDP reliably — not a gaming mode.</summary>
    Psiphon,
    /// <summary>v0.4.48 — Aether exit: the built-in Aether section keeps its own local SOCKS5 proxy (default
    /// 127.0.0.1:1819); sing-box routes ALL traffic through it. No external app — aether.exe is downloaded and
    /// managed inside KOALIX. Gaming-friendly when the Aether section runs the WireGuard protocol (see Guide → Aether).</summary>
    Aether,
}

/// <summary>
/// The WARP identity (a WireGuard interface registered with Cloudflare's free WARP service): private key, peer key,
/// interface addresses and the 3 reserved bytes Cloudflare uses as a user identifier. Created either by in-app
/// registration (same API call as wgcf/BPB: POST api.cloudflareclient.com/&lt;ver&gt;/reg) or by pasting an existing
/// sing-box WireGuard outbound/endpoint JSON or a plain WireGuard .conf. The private key is a credential: it is stored
/// through the app's secret protector and never written to logs or configs that leave the machine.
/// </summary>
public sealed record WarpIdentity
{
    /// <summary>Cloudflare's fixed WARP peer key — identical for every WARP account (only the client identity differs).</summary>
    public const string WarpPeerKey = "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=";

    /// <summary>The default WARP endpoint host. v0.4.48: the IP literal, NOT "engage.cloudflareclient.com" — field log
    /// koalix-20261116 showed the Iranian ISP resolver answering that name with NXDOMAIN on BOTH uplinks, so the
    /// WireGuard peer had "no known endpoint" and every UDP rule routed to WARP black-holed (the whole reason the
    /// configs "didn't connect" when the chain was on). An IP never needs DNS; <see cref="ResolveWarpEndpointAsync"/>
    /// still refreshes it from Cloudflare DoH when the network allows.</summary>
    public const string DefaultEndpointHost = "162.159.192.1";
    /// <summary>Fallback WARP engage endpoints, tried in order when the primary cannot be probed. 188.114.97.1 and
    /// 162.159.195.1 are alternate Cloudflare engage addresses (the same service announced from different ranges);
    /// the domain stays last so a network with clean DNS can still use it. AAAA (v6) engage: 2606:4700:d0::a29f:c001.</summary>
    public static readonly string[] FallbackEndpointHosts = ["162.159.192.1", "188.114.97.1", "162.159.195.1", "engage.cloudflareclient.com"];

    public required string PrivateKey { get; init; }
    public string PeerPublicKey { get; init; } = WarpPeerKey;
    public string AddressV4 { get; init; } = "172.16.0.2";
    public string? AddressV6 { get; init; }
    /// <summary>Reserved bytes (base64-decoded <c>client_id</c> of the registration) — required for the WARP handshake.</summary>
    public IReadOnlyList<int>? Reserved { get; init; }
    public string EndpointHost { get; init; } = DefaultEndpointHost;
    public int EndpointPort { get; init; } = 2408;
    public string? ClientId { get; init; }

    public static bool IsKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { return Convert.FromBase64String(s.Trim()).Length == 32; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Parses a pasted WARP identity: a sing-box WireGuard outbound ({"type":"wireguard", …}), a sing-box config
    /// containing one, a bare WireGuard endpoint object, or a plain WireGuard .conf ([Interface]/[Peer]).
    /// Plain .conf files carry no reserved bytes — without them the WARP handshake fails, so the caller should warn.
    /// </summary>
    public static WarpIdentity? TryParse(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Chain.Error.Empty"; return null; }
        text = text.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                var root = JsonNode.Parse(text) as JsonObject;
                var ob = FindWireguard(root) ?? throw new InvalidDataException("Chain.Error.NoWireguard");
                return FromSingBoxObject(ob, out error);
            }
            catch (JsonException) { error = "Chain.Error.BadJson"; return null; }
            catch (InvalidDataException ex) { error = ex.Message; return null; }
        }
        return FromConf(text, out error);
    }

    private static JsonObject? FindWireguard(JsonObject? root)
    {
        if (root is null) return null;
        if (Str(root["type"]) == "wireguard") return root;
        foreach (var key in new[] { "outbounds", "endpoints" })
            if (root[key] is JsonArray arr)
                foreach (var o in arr.OfType<JsonObject>())
                    if (Str(o["type"]) == "wireguard") return o;
        return null;
    }

    private static WarpIdentity? FromSingBoxObject(JsonObject ob, out string? error)
    {
        error = null;
        var priv = Str(ob["private_key"]);
        if (!IsKey(priv)) { error = "Chain.Error.BadPrivateKey"; return null; }
        string? peer = Str(ob["peer_public_key"]);
        int[]? reserved = IntArray(ob["reserved"]);
        string host = Str(ob["server"]) ?? "";
        int port = ob["server_port"]?.GetValue<int>() ?? 0;
        var addrs = StrArray(ob["local_address"]) ?? StrArray(ob["address"]) ?? [];
        if (ob["peers"] is JsonArray peers && peers.Count > 0 && peers[0] is JsonObject p)
        {
            peer = Str(p["public_key"]) ?? peer;
            reserved ??= IntArray(p["reserved"]);
            host = Str(p["address"]) ?? host;
            port = p["port"]?.GetValue<int>() ?? port;
        }
        var v4 = addrs.FirstOrDefault(a => a.Contains('.') && !a.Contains(':'));
        var v6 = addrs.FirstOrDefault(a => a.Contains(':'));
        if (v4 is null) { error = "Chain.Error.NoAddress"; return null; }
        return new WarpIdentity
        {
            PrivateKey = priv!.Trim(), PeerPublicKey = (IsKey(peer) ? peer!.Trim() : WarpPeerKey),
            AddressV4 = StripCidr(v4), AddressV6 = v6 is null ? null : StripCidr(v6),
            Reserved = reserved, EndpointHost = host.Length > 0 ? host : DefaultEndpointHost,
            EndpointPort = port is >= 1 and <= 65535 ? port : 2408,
        };
    }

    private static WarpIdentity? FromConf(string text, out string? error)
    {
        error = null;
        string? priv = null, peer = null, endpoint = null;
        var addrs = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var i = raw.IndexOf('=');
            if (i <= 0) continue;
            var k = raw[..i].Trim();
            var v = raw[(i + 1)..].Trim();
            switch (k.ToLowerInvariant())
            {
                case "privatekey": priv = v; break;
                case "publickey": peer = v; break;
                case "address": addrs.AddRange(v.Split(',').Select(a => a.Trim())); break;
                case "endpoint": endpoint = v; break;
            }
        }
        if (!IsKey(priv)) { error = "Chain.Error.BadPrivateKey"; return null; }
        var v4 = addrs.FirstOrDefault(a => a.Contains('.') && !a.Contains(':'));
        if (v4 is null) { error = "Chain.Error.NoAddress"; return null; }
        string host = DefaultEndpointHost; int port = 2408;
        if (endpoint is { } e)
        {
            var ci = e.LastIndexOf(':');
            if (ci > 0 && int.TryParse(e[(ci + 1)..], out var p)) { host = e[..ci].Trim('[', ']'); port = p; }
        }
        // .conf carries no reserved bytes — without them Cloudflare's handshake (a 24-bit client id in the WG header)
        // is refused. Allowed, but the caller surfaces Chain.Warn.NoReserved.
        return new WarpIdentity { PrivateKey = priv!.Trim(), PeerPublicKey = (IsKey(peer) ? peer!.Trim() : WarpPeerKey), AddressV4 = StripCidr(v4), AddressV6 = addrs.FirstOrDefault(a => a.Contains(':')) is { } v6 ? StripCidr(v6) : null, EndpointHost = host, EndpointPort = port };
    }

    private static string StripCidr(string a) => a.Contains('/') ? a[..a.IndexOf('/')] : a;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int[]? IntArray(JsonNode? n) => n is JsonArray a
        ? a.Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : (int?)null).OfType<int>().ToArray() is { Length: > 0 } r ? r : null
        : null;

    private static string[]? StrArray(JsonNode? n) => n is JsonArray a
        ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Where(s => s.Length > 0).ToArray() is { Length: > 0 } r ? r : null
        : null;

    /// <summary>Full sing-box endpoint object (1.11+ endpoint form — the outbound form is removed in 1.13).</summary>
    public JsonObject ToSingBoxEndpoint(string tag, string? detour, string? domainResolverTag, int mtu)
    {
        // v0.4.48 — one peer per known engage address. sing-box's WireGuard roams among peers of an endpoint
        // (handshake with whichever answers), so listing the fallback IPs next to the primary makes the tunnel
        // survive a single dead anycast address AND removes the startup dependency on DNS: the primary is an
        // IP literal, and the domain is only kept as the LAST peer with a domain_resolver for networks with
        // clean DNS (field log koalix-20261116: ISP NXDOMAIN on engage.cloudflareclient.com killed every
        // handshake because the sole peer could not be resolved).
        var peers = new JsonArray();
        foreach (var host in EndpointPeers())
        {
            var p = new JsonObject
            {
                ["address"] = host, ["port"] = EndpointPort, ["public_key"] = PeerPublicKey,
                ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0"),
            };
            if (Reserved is { Count: 3 }) p["reserved"] = new JsonArray(Reserved.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
            peers.Add(p);
        }
        var ep = new JsonObject
        {
            ["type"] = "wireguard", ["tag"] = tag,
            ["address"] = new JsonArray(AddressV4 + "/32"),
            ["private_key"] = PrivateKey, ["mtu"] = mtu, ["peers"] = peers,
        };
        if (AddressV6 is { } v6) ((JsonArray)ep["address"]!).Add(v6 + "/128");
        if (detour is { Length: > 0 }) ep["detour"] = detour;
        if (domainResolverTag is { Length: > 0 })
            ep["domain_resolver"] = new JsonObject { ["server"] = domainResolverTag, ["strategy"] = "prefer_ipv4" };
        return ep;
    }

    /// <summary>The engage addresses this identity hands to sing-box: the configured host first, then the other
    /// known fallbacks (deduplicated; the domain form only when DNS can resolve it — checked by the caller, not here).</summary>
    public IEnumerable<string> EndpointPeers()
    {
        yield return EndpointHost;
        foreach (var h in FallbackEndpointHosts)
            if (!string.Equals(h, EndpointHost, StringComparison.OrdinalIgnoreCase))
                yield return h;
    }

    /// <summary>v0.4.48 — refreshes the WARP endpoint host over DoH before a connect. Iranian ISP resolvers answer
    /// engage.cloudflareclient.com with NXDOMAIN, so this NEVER uses the system resolver: it asks Cloudflare's and
    /// Google's DoH endpoints directly (HTTPS to fixed IPs) and keeps the first A record. Failures are silent — the
    /// IP-literal default already works without any DNS at all, this only picks up Cloudflare-side address changes.</summary>
    public static async Task<string> ResolveWarpEndpointAsync(HttpClient http, CancellationToken ct)
    {
        foreach (var (ip, path) in new[] { ("1.1.1.1", "/dns-query?name=engage.cloudflareclient.com&type=A"), ("8.8.8.8", "/resolve?name=engage.cloudflareclient.com&type=A") })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{ip}{path}");
                req.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("Answer", out var answers))
                    foreach (var a in answers.EnumerateArray())
                        if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 && a.TryGetProperty("data", out var d) &&
                            System.Net.IPAddress.TryParse(d.GetString(), out _))
                            return d.GetString()!;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException) { /* next provider */ }
        }
        return DefaultEndpointHost;
    }

    /// <summary>Registers a NEW free WARP identity: keypair from the installed sing-box CLI (no hand-rolled crypto),
    /// then the same registration call wgcf/BPB use. Throws with a log-safe message on failure. The exe is resolved
    /// by the caller via EnginePaths.FindSingBoxExe() (v0.4.47.1: system-wide installs count too). Optional log gets
    /// a key-redacted excerpt when the keygen output is unrecognized.</summary>
    public static async Task<WarpIdentity> RegisterAsync(string? singBoxExe, HttpClient http, CancellationToken ct, Action<string>? log = null)
    {
        if (singBoxExe is null || !File.Exists(singBoxExe)) throw new InvalidOperationException("Engine.Error.SingBoxMissing");
        var kp = await EngineProcess.RunToEndAsync(singBoxExe, ["generate", "wg-keypair"], Path.GetDirectoryName(singBoxExe)!, TimeSpan.FromSeconds(10), ct);
        var (priv, pub) = ParseKeypair(kp.StdOut + kp.StdErr);
        if (priv is null || pub is null)
        {
            // v0.4.47.2 — sing-box 1.13/1.14 changed `generate wg-keypair` from JSON to plain "PrivateKey: …" lines;
            // the old parser only understood JSON and every newer engine failed with a blind toast. Log a REDACTED
            // excerpt (no key material ever reaches the logs) so an unrecognized future format is diagnosable.
            var excerpt = ScrubKeys((kp.StdErr + " " + kp.StdOut).Trim());
            log?.Invoke($"chain: keygen output unrecognized (exit {kp.ExitCode}): {Redactor.Redact(excerpt[..Math.Min(220, excerpt.Length)])}");
            throw new InvalidOperationException("Chain.Error.Keygen");
        }

        // Matches wgcf 2.x exactly (the 1.1.1.1 Android client identity): version, UA and CF-Client-Version must agree.
        const string ver = "v0a5641";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflareclient.com/{ver}/reg");
        req.Headers.TryAddWithoutValidation("User-Agent", "1.1.1.1/6.38.9-5641 (Android 16.0.0)");
        req.Headers.TryAddWithoutValidation("CF-Client-Version", "a-6.38.9-5641");
        req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fcm_token"] = "", ["install_id"] = "", ["key"] = pub, ["locale"] = "en_US", ["model"] = "PC",
            ["tos"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["serial_number"] = "",
            ["os_version"] = "16.0.0", ["key_type"] = "curve25519", ["tunnel_type"] = "wireguard",
        }), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Chain.Error.Register:{(int)resp.StatusCode} {Redactor.Redact(body[..Math.Min(160, body.Length)])}");
        var root = JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("Chain.Error.Register:bad body");
        var cfg = root["config"] as JsonObject ?? throw new InvalidOperationException("Chain.Error.Register:no config");
        var v4 = Str(cfg["interface"]?["addresses"]?["v4"]) ?? throw new InvalidOperationException("Chain.Error.Register:no v4");
        string? peerKey = null, endpointHost = null;
        if (cfg["peers"] is JsonArray pe and { Count: > 0 } && pe[0] is JsonObject p0)
        {
            peerKey = Str(p0["public_key"]);
            endpointHost = Str(p0["endpoint"]?["host"]);
        }
        var clientId = Str(cfg["client_id"]) ?? Str(root["client_id"]);
        int[]? reserved = null;
        if (clientId is { Length: > 0 })
        {
            try
            {
                var norm = clientId.Replace('-', '+').Replace('_', '/');
                var bytes = Convert.FromBase64String(norm.Length % 4 == 0 ? norm : norm.PadRight(norm.Length + (4 - norm.Length % 4), '='));
                if (bytes.Length >= 3) reserved = [bytes[0], bytes[1], bytes[2]];
            }
            catch (FormatException) { /* reserved stays null — handshake may still work on some endpoints */ }
        }
        return new WarpIdentity
        {
            PrivateKey = priv, PeerPublicKey = IsKey(peerKey) ? peerKey! : WarpPeerKey, AddressV4 = v4,
            AddressV6 = Str(cfg["interface"]?["addresses"]?["v6"]), Reserved = reserved,
            ClientId = clientId, EndpointHost = endpointHost ?? DefaultEndpointHost, EndpointPort = 2408,
        };
    }

    private static string ExtractJson(string s)
    {
        var a = s.IndexOf('{');
        var b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : s;
    }

    /// <summary>v0.4.47.2 — parses `sing-box generate wg-keypair` output in BOTH shapes: JSON
    /// ({"private_key":…,"public_key":…}, used up to 1.12.x) and the plain "PrivateKey: … / PublicKey: …"
    /// lines sing-box 1.13+ prints. Keys are validated with IsKey; nulls mean "neither shape matched".</summary>
    public static (string? Priv, string? Pub) ParseKeypair(string output)
    {
        try
        {
            var j = JsonNode.Parse(ExtractJson(output)) as JsonObject;
            var p = Str(j?["private_key"]);
            var q = Str(j?["public_key"]);
            if (IsKey(p) && IsKey(q)) return (p, q);
        }
        catch (JsonException) { /* not JSON — try the plain form below */ }
        string? priv = null, pub = null;
        foreach (var raw in output.Split('\n'))
        {
            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            var k = raw[..i].Trim().Replace("_", "").ToUpperInvariant();
            var v = raw[(i + 1)..].Trim();
            if (k == "PRIVATEKEY") priv = v;
            else if (k == "PUBLICKEY") pub = v;
        }
        return (IsKey(priv) ? priv!.Trim() : null, IsKey(pub) ? pub!.Trim() : null);
    }

    /// <summary>Replaces any WireGuard-shaped 44-char base64 token (43 chars + '=') with a marker — used before
    /// keygen output is written to the log so key material can never leak through a failure path.</summary>
    public static string ScrubKeys(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"[A-Za-z0-9+/]{43}=", "[redacted]");
}

/// <summary>Settings-backed options of the chain feature. v0.4.47.</summary>
public sealed record ChainOptions
{
    public bool Enabled { get; init; }
    public ChainMode Mode { get; init; } = ChainMode.WarpGaming;
    public WarpIdentity? Warp { get; init; }
    public int WarpMtu { get; init; } = 1280;
    public int PsiphonPort { get; init; } = 1080;

    /// <summary>v0.4.48 — Aether chain mode: the local SOCKS5 proxy of KOALIX's own Aether section
    /// (default 127.0.0.1:1819). No identity needed — the Aether engine registers itself, so IsRunnable is true.</summary>
    public int AetherPort { get; init; } = 1819;

    public bool NeedsWarp => Mode is not (ChainMode.Psiphon or ChainMode.Aether);
    public bool IsRunnable => !NeedsWarp || Warp is not null;

    public static ChainMode ParseMode(string? s) => s switch
    {
        "warpExit" => ChainMode.WarpExit,
        "warpOverProxy" => ChainMode.WarpOverProxy,
        "psiphon" => ChainMode.Psiphon,
        "aether" => ChainMode.Aether,
        _ => ChainMode.WarpGaming,
    };

    public static string ModeKey(ChainMode m) => m switch
    {
        ChainMode.WarpExit => "warpExit",
        ChainMode.WarpOverProxy => "warpOverProxy",
        ChainMode.Psiphon => "psiphon",
        ChainMode.Aether => "aether",
        _ => "warpGaming",
    };

    /// <summary>Reads the options from settings. <paramref name="get"/> receives the raw stored value — the caller
    /// (App layer) is responsible for unprotecting <c>chain.warp.priv</c> before it lands here.</summary>
    public static ChainOptions FromSettings(Func<string, string?> get)
    {
        WarpIdentity? warp = null;
        var priv = get("chain.warp.priv");
        if (WarpIdentity.IsKey(priv))
        {
            var reservedS = get("chain.warp.reserved");
            var reserved = reservedS is { Length: > 0 } ? reservedS.Split(',').Select(s => int.TryParse(s.Trim(), out var i) ? i : -1).Where(i => i >= 0).ToArray() : null;
            warp = new WarpIdentity
            {
                PrivateKey = priv!.Trim(),
                PeerPublicKey = WarpIdentity.IsKey(get("chain.warp.peer")) ? get("chain.warp.peer")!.Trim() : WarpIdentity.WarpPeerKey,
                AddressV4 = get("chain.warp.v4") is { Length: > 0 } v4 ? v4.Trim() : "172.16.0.2",
                AddressV6 = get("chain.warp.v6") is { Length: > 0 } v6 ? v6.Trim() : null,
                Reserved = reserved,
                EndpointHost = get("chain.warp.host") is { Length: > 0 } h ? h.Trim() : WarpIdentity.DefaultEndpointHost,
                EndpointPort = int.TryParse(get("chain.warp.port"), out var p) && p is >= 1 and <= 65535 ? p : 2408,
            };
        }
        return new ChainOptions
        {
            Enabled = get("chain.enabled") == "1",
            Mode = ParseMode(get("chain.mode")),
            Warp = warp,
            WarpMtu = int.TryParse(get("chain.warp.mtu"), out var mtu) && mtu is >= 1000 and <= 9500 ? mtu : 1280,
            PsiphonPort = int.TryParse(get("chain.psiphon.port"), out var pp) && pp is >= 1 and <= 65535 ? pp : 1080,
            AetherPort = int.TryParse(get("chain.aether.port"), out var ap) && ap is >= 1 and <= 65535 ? ap : 1819,
        };
    }
}

/// <summary>
/// Applies the chain (config combination) onto an ALREADY-BUILT sing-box configuration — generated (share link) or
/// merged (panel JSON) — right before validation/launch. Everything is additive and reversible: when the chain is off
/// the config is returned untouched, when a mode cannot run (no WARP identity) it degrades to plain single-config
/// behavior with a log line instead of failing the connect. v0.4.47.
/// </summary>
public static class ChainConfig
{
    public const string WarpTag = "warp-out";
    public const string PsiphonTag = "psiphon-out";
    public const string AetherTag = "aether-out";

    /// <summary>Applies the chain. Never throws — a chain problem must not kill a connect that works without it.</summary>
    public static JsonObject Apply(JsonObject root, string proxyTag, ChainOptions opts, SplitPolicy policy, Action<string>? log = null)
    {
        try { return ApplyCore(root, proxyTag, opts, policy, log); }
        catch (Exception ex) { log?.Invoke("chain: " + Redactor.Redact(ex.Message) + " — chain disabled for this connect"); return root; }
    }

    private static JsonObject ApplyCore(JsonObject root, string proxyTag, ChainOptions opts, SplitPolicy policy, Action<string>? log)
    {
        if (!opts.Enabled || !opts.IsRunnable)
        {
            if (opts.Enabled && !opts.IsRunnable) log?.Invoke("chain: WARP identity missing — running the plain config (set up WARP in the dashboard chain card)");
            return root;
        }

        var route = root["route"] as JsonObject ?? (JsonObject)(root["route"] = new JsonObject());
        var rules = route["rules"] as JsonArray ?? (JsonArray)(route["rules"] = new JsonArray());

        if (opts.Mode == ChainMode.Psiphon)
        {
            var outbounds = root["outbounds"] as JsonArray ?? (JsonArray)(root["outbounds"] = new JsonArray());
            if (!outbounds.OfType<JsonObject>().Any(o => Str(o["tag"]) == PsiphonTag))
                outbounds.Add(new JsonObject { ["type"] = "socks", ["tag"] = PsiphonTag, ["server"] = "127.0.0.1", ["server_port"] = opts.PsiphonPort, ["version"] = "5" });
            var udpRejected = RemovePureUdpRejects(rules);
            if (udpRejected > 0) log?.Invoke($"chain: removed {udpRejected} UDP-reject rule(s) — the chain now carries UDP");
            PointHealthTo(rules, PsiphonTag);
            route["final"] = PsiphonTag;
            log?.Invoke($"chain: PSIPHON mode — all traffic exits through the local Psiphon SOCKS proxy (127.0.0.1:{opts.PsiphonPort}); start Psiphon BEFORE connecting");
            return root;
        }

        if (opts.Mode == ChainMode.Aether)
        {
            // v0.4.48 — AETHER mode: the Aether section's own local SOCKS5 proxy (default 127.0.0.1:1819) is the
            // exit for everything. The engine is started by the dashboard BEFORE this connect, so by the time
            // sing-box dials it, the port answers. Like Psiphon mode: no identity, no endpoint, health rides the chain.
            var outbounds = root["outbounds"] as JsonArray ?? (JsonArray)(root["outbounds"] = new JsonArray());
            if (!outbounds.OfType<JsonObject>().Any(o => Str(o["tag"]) == AetherTag))
                outbounds.Add(new JsonObject { ["type"] = "socks", ["tag"] = AetherTag, ["server"] = "127.0.0.1", ["server_port"] = opts.AetherPort, ["version"] = "5" });
            var udpRejectedA = RemovePureUdpRejects(rules);
            if (udpRejectedA > 0) log?.Invoke($"chain: removed {udpRejectedA} UDP-reject rule(s) — Aether (WireGuard/MASQUE) carries UDP");
            PointHealthTo(rules, AetherTag);
            route["final"] = AetherTag;
            log?.Invoke($"chain: AETHER mode — all traffic exits through the built-in Aether tunnel (127.0.0.1:{opts.AetherPort}); no external app needed");
            return root;
        }

        // WARP modes — the identity rides as a sing-box 1.11+ ENDPOINT (the outbound form is removed in 1.13).
        var endpoints = root["endpoints"] as JsonArray ?? (JsonArray)(root["endpoints"] = new JsonArray());
        if (endpoints.OfType<JsonObject>().Any(o => Str(o["tag"]) == WarpTag))
            endpoints.Remove(endpoints.OfType<JsonObject>().First(o => Str(o["tag"]) == WarpTag));
        var detour = opts.Mode == ChainMode.WarpOverProxy ? proxyTag : null;
        endpoints.Add(opts.Warp!.ToSingBoxEndpoint(WarpTag, detour, FindLocalDnsTag(root), opts.WarpMtu));
        var udpRejectedW = RemovePureUdpRejects(rules);
        if (udpRejectedW > 0) log?.Invoke($"chain: removed {udpRejectedW} UDP-reject rule(s) — panel configs reject UDP because workers cannot carry it; WARP can");

        if (opts.Mode == ChainMode.WarpGaming)
        {
            // Only the UDP flows that would otherwise enter the tunnel switch to WARP; TCP keeps the selected config.
            if (policy.Mode == SplitMode.Include)
            {
                var tunneled = policy.Rules.Where(r => r.Action == AppAction.Tunnel).ToList();
                var idx = rules.OfType<JsonObject>().ToList().FindIndex(r => Str(r["outbound"]) == proxyTag);
                var before = idx >= 0 ? idx : rules.Count;
                var lanDirect = ProcessRules(tunneled, direct: true);   // LAN servers of tunneled games stay direct
                foreach (var r in lanDirect) rules.Insert(before++, r);
                foreach (var r in ProcessRules(tunneled, direct: false)) rules.Insert(before++, r);
            }
            else
            {
                // FullTunnel/Exclude: everything that is not already direct by an earlier rule (LAN, direct apps,
                // blocked apps) and reaches the final switches to WARP — first-match routing, so appended rules only
                // catch flows that were headed to the tunnel anyway.
                rules.Add(new JsonObject { ["network"] = "udp", ["outbound"] = WarpTag });
            }
            // The health probe now exercises the actual gaming path: TUN → WARP → internet (handshake included).
            PointHealthTo(rules, WarpTag);
            log?.Invoke("chain: WARP-GAMING mode — UDP exits through WARP (direct), TCP keeps the selected config");
            return root;
        }

        if (opts.Mode == ChainMode.WarpExit)
        {
            PointHealthTo(rules, WarpTag);
            // The config is no longer the exit: resolve tunneled names through WARP too, otherwise a dead worker
            // (the exact reason this mode exists) would keep answering DNS with nothing.
            SwapRemoteDnsDetour(root, proxyTag, WarpTag);
            route["final"] = WarpTag;
            log?.Invoke("chain: WARP-EXIT mode — all traffic exits through WARP; the selected config is unused");
            return root;
        }

        // WarpOverProxy — the friend's suggestion: WARP dialed THROUGH the config. Honest by design: with a
        // TCP-only worker upstream the WireGuard handshake cannot travel (vless-ws has no UDP), the endpoint stays
        // dead, and the health probe (routed through WARP below) reports it instead of pretending success.
        PointHealthTo(rules, WarpTag);
        route["final"] = WarpTag;
        log?.Invoke("chain: WARP-OVER-PROXY mode — WARP dialed through the selected config; requires an upstream that carries UDP (real server / hysteria2 / tuic), NOT a Cloudflare worker");
        return root;
    }

    /// <summary>Panel configs (BPB & co.) reject UDP wholesale because workers cannot carry it — that rule must die
    /// when the chain is on, or it eats the game's UDP before the WARP rule ever sees it. Only PURE udp-rejects are
    /// removed (no port/ip/process conditions, so QUIC blocking and app rules stay).</summary>
    public static int RemovePureUdpRejects(JsonArray rules)
    {
        var gone = 0;
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is not JsonObject r) continue;
            if (Str(r["action"]) != "reject" || Str(r["network"]) != "udp") continue;
            if (r.Count > 2) continue; // {"network":"udp","action":"reject"} and nothing else
            rules.RemoveAt(i); gone++;
        }
        return gone;
    }

    /// <summary>The health inbound's explicit rule defines what "connected" means for the UI — point it at the exit
    /// the user actually chose, never keep it on the config when WARP/Psiphon is the real exit.</summary>
    private static void PointHealthTo(JsonArray rules, string tag)
    {
        foreach (var r in rules.OfType<JsonObject>())
            if (Str(r["inbound"]) == "health-in") r["outbound"] = tag;
    }

    private static void SwapRemoteDnsDetour(JsonObject root, string proxyTag, string warpTag)
    {
        if (root["dns"]?["servers"] is not JsonArray servers) return;
        foreach (var s in servers.OfType<JsonObject>())
            if (Str(s["detour"]) == proxyTag || (Str(s["tag"]) is { } t && t.Contains("remote") && Str(s["detour"]) is not null && Str(s["detour"]) != "direct"))
                s["detour"] = warpTag;
    }

    /// <summary>A local (non-detoured) resolver for the endpoint's <c>domain_resolver</c> — merged panel configs
    /// bring their own (dns-direct, koalix variants…), generated configs have dns-local.</summary>
    private static string? FindLocalDnsTag(JsonObject root)
    {
        foreach (var s in (root["dns"]?["servers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var detour = Str(s["detour"]);
            var tag = Str(s["tag"]);
            if (tag is not null && (detour is null || detour == "direct") && Str(s["type"]) != "https") return tag;
        }
        return (root["dns"]?["servers"] as JsonArray ?? []).OfType<JsonObject>().Select(s => Str(s["tag"])).FirstOrDefault(t => t is not null);
    }

    /// <summary>UDP rules for the Include split, exact paths and folder regexes, like SplitRuleCompiler.AddMatch.
    /// direct=true emits ONLY the LAN-guarded variants (game's LAN servers stay direct); direct=false emits ONLY the
    /// tunnel variants — otherwise the direct rule would swallow the game's UDP before the WARP rule ever matched.</summary>
    private static List<JsonObject> ProcessRules(IReadOnlyList<AppRule> apps, bool direct)
    {
        var list = new List<JsonObject>();
        var exact = apps.Where(a => !a.IncludeFolder && !a.IsFolderRule).Select(a => a.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = apps.Where(a => a.IncludeFolder || a.IsFolderRule).Select(a => SplitRuleCompiler.FolderPrefixRegex(a.FolderPrefix)).Distinct().ToList();
        var action = direct ? "direct" : WarpTag;
        JsonObject New(bool lan, bool folder)
        {
            var o = new JsonObject();
            o[folder ? "process_path_regex" : "process_path"] = new JsonArray((folder ? folders : exact).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            o["network"] = "udp";
            if (lan) o["ip_is_private"] = true;
            o["outbound"] = action;
            return o;
        }
        if (exact.Count > 0) list.Add(New(lan: direct, folder: false));
        if (folders.Count > 0) list.Add(New(lan: direct, folder: true));
        return list;
    }

    /// <summary>
    /// Builds a STANDALONE sing-box config combining the selected profile with the chain — the "combo" file to import
    /// into sing-box / Hiddify / v2rayNG(NekoBox) on another device (phone). Share links become one proxy outbound;
    /// sing-box JSON configs are carried over whole. Psiphon mode is refused (it needs the desktop Psiphon client).
    /// </summary>
    public static JsonObject? BuildStandalone(TunnelProfile profile, ChainOptions opts, out string? error)
    {
        error = null;
        if (!opts.Enabled) { error = "Chain.Error.Disabled"; return null; }
        if (opts.Mode == ChainMode.Psiphon) { error = "Chain.Error.PsiphonExport"; return null; }
        if (opts.NeedsWarp && opts.Warp is null) { error = "Chain.Error.NoWarp"; return null; }

        string proxyTag;
        var root = new JsonObject();
        switch (profile.Source)
        {
            case ProfileSource.ShareLink:
            {
                var node = ShareLinkParser.Parse(profile.Content);
                var errors = node.Validate();
                if (errors.Count > 0) { error = string.Join(", ", errors); return null; }
                if (!SingBoxConfigBuilder.IsNativelySupported(node, out var why)) { error = why; return null; }
                proxyTag = "proxy";
                root["outbounds"] = new JsonArray { SingBoxConfigBuilder.Outbound(node, proxyTag), new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
                break;
            }
            case ProfileSource.SingBoxJson:
            {
                var user = JsonNode.Parse(profile.Content) as JsonObject;
                if (user is null) { error = "Root must be a JSON object"; return null; }
                var candidates = SingBoxConfigBuilder.ProxyCandidates(user);
                if (candidates.Count == 0) { error = "No proxy outbound found in configuration"; return null; }
                proxyTag = SingBoxConfigBuilder.PickTunnelTarget(user, candidates);
                foreach (var key in new[] { "outbounds", "endpoints" })
                    if (user[key] is JsonArray arr) root[key] = (JsonArray)arr.DeepClone();
                var obs = root["outbounds"] as JsonArray ?? (JsonArray)(root["outbounds"] = new JsonArray());
                if (!obs.OfType<JsonObject>().Any(o => Str(o["tag"]) == "direct"))
                    obs.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
                break;
            }
            default:
                error = "Chain.Error.UnsupportedSource";
                return null;
        }

        root["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true };
        root["dns"] = new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject { ["type"] = "https", ["tag"] = "dns-remote", ["server"] = "8.8.8.8", ["detour"] = proxyTag },
                new JsonObject { ["type"] = "udp", ["tag"] = "dns-local", ["server"] = "8.8.8.8" },
            },
            ["final"] = "dns-remote",
            ["strategy"] = "ipv4_only",
        };
        root["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tun", ["tag"] = "tun-in", ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["mtu"] = 9000, ["auto_route"] = true, ["strict_route"] = true, ["stack"] = "mixed",
            },
        };
        root["route"] = new JsonObject
        {
            ["rules"] = new JsonArray
            {
                new JsonObject { ["action"] = "sniff" },
                new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
            },
            ["final"] = proxyTag,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "dns-local",
        };
        // The compiled rules above only cover FullTunnel-style routing (a portable export has no per-app split);
        // the chain modes then land exactly as on the desktop.
        return Apply(root, proxyTag, opts, SplitPolicy.Full, null);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
