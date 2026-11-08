using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Koalix.WireGuard;

public sealed record WireGuardPeer
{
    public required string PublicKey { get; init; }
    public string? PresharedKey { get; init; }
    public IReadOnlyList<string> AllowedIps { get; init; } = [];
    public string? Endpoint { get; init; }
    public int? PersistentKeepalive { get; init; }
    public (string Host, int Port)? EndpointParts
    {
        get
        {
            if (Endpoint is null) return null;
            var i = Endpoint.LastIndexOf(':');
            return i > 0 && int.TryParse(Endpoint[(i + 1)..], out var p) ? (Endpoint[..i].Trim('[', ']'), p) : null;
        }
    }
}

public sealed record WireGuardConfig
{
    public required string PrivateKey { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    /// <summary>True when the interface has an IPv6 address, i.e. IPv6 destinations can actually be carried by the tunnel.</summary>
    public bool HasIpv6Address => Addresses.Any(a => a.Contains(':'));
    /// <summary>Union of all peers' AllowedIPs (empty = not specified, treated as everything).</summary>
    public IReadOnlyList<string> AllowedPrefixes => Peers.SelectMany(p => p.AllowedIps).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    /// <summary>
    /// True only when IPv6 can really travel through the tunnel: the interface has an IPv6 address AND the peers accept
    /// IPv6 destinations. Many configs ship an IPv6 Address but an IPv4-only AllowedIPs list; IPv6 then fails with
    /// "network is unreachable" and browsers stall.
    /// </summary>
    public bool CarriesIpv6
    {
        get
        {
            if (!HasIpv6Address) return false;
            var allowed = AllowedPrefixes;
            return allowed.Count == 0 || allowed.Any(a => a.Contains(':'));
        }
    }
    /// <summary>
    /// Destinations the peers accept when AllowedIPs is a partial list (e.g. "all except Iran"); null when everything is
    /// allowed. Anything outside this list must go DIRECT, otherwise the WireGuard endpoint drops it.
    /// </summary>
    /// <summary>True when <paramref name="address"/> (an IP literal) lies inside one of <paramref name="cidrs"/>. Host names return true.</summary>
    public static bool IsCovered(string address, IReadOnlyList<string> cidrs)
    {
        if (!IPAddress.TryParse(address.Trim('[', ']'), out var ip)) return true;
        foreach (var c in cidrs)
        {
            var t = c.Contains('/') ? c : c + (c.Contains(':') ? "/128" : "/32");
            if (IPNetwork.TryParse(t, out var net) && net.Contains(ip)) return true;
        }
        return false;
    }

    public IReadOnlyList<string>? PartialTunnelCidrs
    {
        get
        {
            var allowed = AllowedPrefixes;
            if (allowed.Count == 0) return null;
            bool v4Full = allowed.Any(a => a is "0.0.0.0/0" || a == "0.0.0.0/1" && allowed.Contains("128.0.0.0/1"));
            bool hasV6 = allowed.Any(a => a.Contains(':'));
            bool v6Full = allowed.Any(a => a is "::/0" || a == "::/1" && allowed.Contains("8000::/1"));
            if (v4Full && (!hasV6 || v6Full)) return null;
            return allowed;
        }
    }
    public IReadOnlyList<string> Dns { get; init; } = [];
    public int? Mtu { get; init; }
    public int? ListenPort { get; init; }
    public IReadOnlyList<WireGuardPeer> Peers { get; init; } = [];
    /// <summary>AmneziaWG / WireSock obfuscation keys found in [Interface] (Jc, Jmin, Jmax, S1-S4, H1-H4, I1-I5...).</summary>
    public IReadOnlyList<string> ObfuscationKeys { get; init; } = [];
    public bool HasObfuscation => ObfuscationKeys.Count > 0;
    private static readonly HashSet<string> ObfKeys = new(StringComparer.OrdinalIgnoreCase)
    { "jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5", "j1", "j2", "j3", "itime" };

    /// <summary>
    /// WireSock config: the original file (incl. obfuscation keys) with KOALIX's per-app lists in every [Peer]
    /// (AllowedApps = tunnel only these, DisallowedApps = everything except these). Existing app lists are replaced.
    /// </summary>
    public static string ToWireSock(string original, IReadOnlyList<string>? allowedApps, IReadOnlyList<string>? disallowedApps)
    {
        var sb = new StringBuilder();
        bool inPeer = false;
        void Emit()
        {
            if (!inPeer) return;
            if (allowedApps is { Count: > 0 }) sb.Append("AllowedApps = ").AppendLine(string.Join(", ", allowedApps));
            if (disallowedApps is { Count: > 0 }) sb.Append("DisallowedApps = ").AppendLine(string.Join(", ", disallowedApps));
        }
        foreach (var raw in original.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith('[')) { Emit(); inPeer = t.Trim('[', ']').Trim().Equals("peer", StringComparison.OrdinalIgnoreCase); sb.AppendLine(raw); continue; }
            var eq = t.IndexOf('=');
            if (eq > 0)
            {
                var k = t[..eq].Trim();
                if (k.Equals("AllowedApps", StringComparison.OrdinalIgnoreCase) || k.Equals("DisallowedApps", StringComparison.OrdinalIgnoreCase)) continue;
            }
            sb.AppendLine(raw);
        }
        Emit();
        return sb.ToString();
    }

    public static WireGuardConfig Parse(string text, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        string? section = null, priv = null; int? mtu = null, listen = null;
        var obf = new List<string>();
        var addrs = new List<string>(); var dns = new List<string>(); var peers = new List<WireGuardPeer>();
        Dictionary<string, string>? peer = null;
        void FlushPeer()
        {
            if (peer is null) return;
            if (!peer.TryGetValue("publickey", out var pk) || !IsKey(pk)) errs.Add("WG.Error.PeerPublicKey");
            else peers.Add(new WireGuardPeer
            {
                PublicKey = pk,
                PresharedKey = peer.GetValueOrDefault("presharedkey"),
                AllowedIps = Split(peer.GetValueOrDefault("allowedips")),
                Endpoint = peer.GetValueOrDefault("endpoint"),
                PersistentKeepalive = int.TryParse(peer.GetValueOrDefault("persistentkeepalive"), out var k) ? k : null,
            });
            peer = null;
        }
        var n = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            var line = rawLine.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                FlushPeer();
                section = line.Trim('[', ']').Trim().ToLowerInvariant();
                if (section == "peer") peer = new(StringComparer.OrdinalIgnoreCase);
                else if (section != "interface") errs.Add($"WG.Error.UnknownSection:{n}");
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) { errs.Add($"WG.Error.Syntax:{n}"); continue; }
            var key = line[..eq].Trim().ToLowerInvariant(); var val = line[(eq + 1)..].Trim();
            if (section == "interface")
                switch (key)
                {
                    case "privatekey": priv = val; break;
                    case "address": addrs.AddRange(Split(val)); break;
                    case "dns": dns.AddRange(Split(val)); break;
                    case "mtu": mtu = int.TryParse(val, out var m) ? m : null; break;
                    case "listenport": listen = int.TryParse(val, out var lp) ? lp : null; break;
                    default: if (ObfKeys.Contains(key)) obf.Add(key); break;
                }
            else if (section == "peer") peer![key] = val;
        }
        FlushPeer();
        if (priv is null || !IsKey(priv)) errs.Add("WG.Error.PrivateKey");
        if (addrs.Count == 0) errs.Add("WG.Error.Address");
        if (peers.Count == 0) errs.Add("WG.Error.NoPeers");
        foreach (var a in addrs) if (!IPNetwork.TryParse(a.Contains('/') ? a : a + (a.Contains(':') ? "/128" : "/32"), out _)) errs.Add("WG.Error.InvalidAddress:" + a);
        errors = errs;
        return new WireGuardConfig { PrivateKey = priv ?? "", Addresses = addrs, Dns = dns, Mtu = mtu, ListenPort = listen, Peers = peers, ObfuscationKeys = obf };
    }

    public static bool IsKey(string s) { try { return Convert.FromBase64String(s).Length == 32; } catch (FormatException) { return false; } }
    private static List<string> Split(string? s) => string.IsNullOrWhiteSpace(s) ? [] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Converts to a sing-box 1.11+ WireGuard endpoint (userspace WireGuard inside sing-box) so process routing can be applied.</summary>
    public JsonObject ToSingBoxEndpoint(string tag = "proxy", string localDnsTag = "dns-local")
    {
        var ep = new JsonObject
        {
            ["type"] = "wireguard", ["tag"] = tag,
            ["address"] = new JsonArray(Addresses.Select(a => (JsonNode)(a.Contains('/') ? a : a + (a.Contains(':') ? "/128" : "/32"))).ToArray()),
            ["private_key"] = PrivateKey,
            ["mtu"] = Mtu ?? 1408,
            ["peers"] = new JsonArray(Peers.Select(p =>
            {
                var (host, port) = p.EndpointParts ?? throw new InvalidDataException("WG.Error.PeerEndpoint");
                var o = new JsonObject
                {
                    ["address"] = host, ["port"] = port, ["public_key"] = p.PublicKey,
                    ["allowed_ips"] = new JsonArray((p.AllowedIps.Count > 0 ? p.AllowedIps : ["0.0.0.0/0", "::/0"]).Select(a => (JsonNode)a).ToArray()),
                };
                if (!string.IsNullOrEmpty(p.PresharedKey)) o["pre_shared_key"] = p.PresharedKey;
                // Keepalive keeps NAT mappings alive and lets the tunnel recover quickly when Windows switches the default interface.
                o["persistent_keepalive_interval"] = p.PersistentKeepalive ?? 25;
                return (JsonNode)o;
            }).ToArray()),
        };
        // Hostname endpoints: prefer IPv4 — many Iranian ISPs / other VPN adapters have broken IPv6 ("address family not supported").
        if (Peers.Any(p => p.EndpointParts is { } e && !System.Net.IPAddress.TryParse(e.Host.Trim('[', ']'), out _)))
            ep["domain_resolver"] = new JsonObject { ["server"] = localDnsTag, ["strategy"] = "prefer_ipv4" };
        return ep;
    }

    /// <summary>Text with private material removed — safe for display, logs and crash reports.</summary>
    public string ToRedactedString()
    {
        var sb = new StringBuilder("[Interface]\nPrivateKey = [REDACTED]\n");
        sb.Append("Address = ").AppendLine(string.Join(", ", Addresses));
        if (Dns.Count > 0) sb.Append("DNS = ").AppendLine(string.Join(", ", Dns));
        foreach (var p in Peers)
            sb.Append("\n[Peer]\nPublicKey = ").AppendLine(p.PublicKey)
              .Append("AllowedIPs = ").AppendLine(string.Join(", ", p.AllowedIps))
              .Append("Endpoint = ").AppendLine(p.Endpoint);
        return sb.ToString();
    }
}
