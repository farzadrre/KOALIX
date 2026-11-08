using System.Text;
using System.Text.Json;
using System.Web;

namespace Koalix.Tunneling;

/// <summary>Parse outcome: real servers + parse errors + how many placeholder NOTICE entries were skipped.</summary>
public sealed record LinkParseResult(IReadOnlyList<ProxyNode> Nodes, IReadOnlyList<string> Errors, int Notices = 0);

/// <summary>Parses vmess://, vless://, trojan://, ss://, hysteria2:// / hy2://, tuic://, socks:// and http(s):// proxy share links.</summary>
public static class ShareLinkParser
{
    public static LinkParseResult ParseMany(string text)
    {
        var nodes = new List<ProxyNode>(); var errors = new List<string>(); var notices = 0;
        var content = text.Trim();
        if (!content.Contains("://") && TryBase64(content, out var decoded)) content = decoded; // base64 subscription body
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#') || raw.Length == 0) continue;
            try
            {
                var node = Parse(raw);
                // v0.4.22 (user: ZEUS-panel links «کانفیگاش اینشکلی ان»): panels pad subscriptions with
                // placeholder rows — volume reminders, ads, bot promos — pointing at 0.0.0.0:1. They can never
                // connect; other clients import them as junk rows. They are counted as notices and skipped.
                if (IsPlaceholderNotice(node)) { notices++; continue; }
                nodes.Add(node);
            }
            catch (Exception ex) when (ex is FormatException or UriFormatException or JsonException or ArgumentException or KeyNotFoundException)
            {
                errors.Add($"{Scheme(raw)}: {ex.Message}");
            }
        }
        return new LinkParseResult(nodes, errors, notices);
    }

    /// <summary>True for the ad/reminder rows panels inject (unroutable 0.0.0.0 / port 0–1 placeholders).</summary>
    public static bool IsPlaceholderNotice(ProxyNode n) =>
        n.Server is "0.0.0.0" or "127.0.0.1" or "" || n.Port is 0 or 1;

    private static string Scheme(string link) { var i = link.IndexOf("://", StringComparison.Ordinal); return i > 0 ? link[..i] : "?"; }

    public static ProxyNode Parse(string link)
    {
        var scheme = Scheme(link).ToLowerInvariant();
        var node = scheme switch
        {
            "vmess" => ParseVmess(link),
            "vless" => ParseUriStyle(link, ProxyProtocol.VLess),
            "trojan" => ParseUriStyle(link, ProxyProtocol.Trojan),
            "ss" => ParseShadowsocks(link),
            "hysteria2" or "hy2" => ParseUriStyle(link, ProxyProtocol.Hysteria2),
            "tuic" => ParseUriStyle(link, ProxyProtocol.Tuic),
            "socks" or "socks5" => ParseUriStyle(link, ProxyProtocol.Socks),
            "http" or "https" => ParseUriStyle(link, ProxyProtocol.Http),
            _ => throw new FormatException($"Unsupported link scheme '{scheme}'"),
        };
        return node with { RawLink = link.Trim() };
    }

    private static ProxyNode ParseVmess(string link)
    {
        var body = link["vmess://".Length..];
        var hash = body.IndexOf('#'); if (hash >= 0) body = body[..hash];
        if (!TryBase64(body, out var json)) throw new FormatException("vmess payload is not valid base64");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;
        var net = S("net") ?? "tcp";
        var type = S("type");
        return new ProxyNode
        {
            Protocol = ProxyProtocol.VMess,
            Name = S("ps") ?? "vmess",
            Server = S("add") ?? throw new FormatException("vmess: missing add"),
            Port = int.Parse(S("port") ?? throw new FormatException("vmess: missing port")),
            Uuid = S("id"),
            AlterId = int.TryParse(S("aid"), out var aid) ? aid : 0,
            VmessSecurity = S("scy") ?? "auto",
            Transport = net == "h2" ? "http" : net,
            HeaderType = type is "none" or "" ? null : type,
            Host = NullIfEmpty(S("host")),
            Path = NullIfEmpty(S("path")),
            ServiceName = net == "grpc" ? NullIfEmpty(S("path")) : null,
            Security = S("tls") is "tls" ? "tls" : "none",
            Sni = NullIfEmpty(S("sni")),
            Alpn = SplitList(S("alpn")),
            Fingerprint = NullIfEmpty(S("fp")),
        };
    }

    private static ProxyNode ParseUriStyle(string link, ProxyProtocol protocol)
    {
        var uri = new Uri(link);
        var q = HttpUtility.ParseQueryString(uri.Query);
        string? Q(params string[] keys) { foreach (var k in keys) if (!string.IsNullOrEmpty(q[k])) return q[k]; return null; }
        var user = Uri.UnescapeDataString(uri.UserInfo);
        string? uuid = null, password = null, username = null;
        switch (protocol)
        {
            case ProxyProtocol.VLess: uuid = user; break;
            case ProxyProtocol.Trojan or ProxyProtocol.Hysteria2: password = user; break;
            case ProxyProtocol.Tuic: { var p = user.Split(':', 2); uuid = p[0]; password = p.ElementAtOrDefault(1); break; }
            case ProxyProtocol.Socks or ProxyProtocol.Http:
                {
                    var u = user;
                    if (protocol == ProxyProtocol.Socks && !u.Contains(':') && TryBase64(u, out var dec)) u = dec;
                    var p = u.Split(':', 2); username = NullIfEmpty(p[0]); password = p.ElementAtOrDefault(1); break;
                }
        }
        var security = Q("security") ?? (protocol is ProxyProtocol.Trojan or ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic ? "tls" : "none");
        if (protocol == ProxyProtocol.Http && uri.Scheme == "https") security = "tls";
        var type = Q("type") ?? "tcp";
        var host = uri.IdnHost.Trim('[', ']');
        if (uri.Port <= 0) throw new FormatException("missing port");
        return new ProxyNode
        {
            Protocol = protocol,
            Name = string.IsNullOrEmpty(uri.Fragment) ? $"{protocol} {host}" : Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Server = host,
            Port = uri.Port,
            Uuid = uuid, Password = password, Username = username,
            Flow = Q("flow"),
            Transport = type == "h2" ? "http" : type,
            Path = Q("path"),
            Host = Q("host"),
            ServiceName = Q("serviceName", "servicename"),
            HeaderType = Q("headerType") is { } h && h != "none" ? h : null,
            Security = security,
            Sni = Q("sni", "peer", "servername"),
            Alpn = SplitList(Q("alpn")),
            Fingerprint = Q("fp"),
            RealityPublicKey = Q("pbk"),
            RealityShortId = Q("sid"),
            AllowInsecure = Q("allowInsecure", "insecure", "allow_insecure") is "1" or "true",
            Obfs = Q("obfs"),
            ObfsPassword = Q("obfs-password", "obfs_password"),
            CongestionControl = Q("congestion_control", "congestion"),
        };
    }

    private static ProxyNode ParseShadowsocks(string link)
    {
        var body = link["ss://".Length..];
        string name = "shadowsocks";
        var hash = body.IndexOf('#');
        if (hash >= 0) { name = Uri.UnescapeDataString(body[(hash + 1)..]); body = body[..hash]; }
        string? plugin = null;
        var qi = body.IndexOf('?');
        if (qi >= 0) { plugin = HttpUtility.ParseQueryString(body[qi..])["plugin"]; body = body[..qi]; }
        body = body.TrimEnd('/');

        string userInfo, hostPort;
        var at = body.LastIndexOf('@');
        if (at < 0)
        {
            // Legacy: ss://base64(method:password@host:port)
            if (!TryBase64(body, out var dec)) throw new FormatException("ss: invalid base64");
            at = dec.LastIndexOf('@');
            if (at < 0) throw new FormatException("ss: missing host");
            userInfo = dec[..at]; hostPort = dec[(at + 1)..];
        }
        else
        {
            userInfo = Uri.UnescapeDataString(body[..at]); hostPort = body[(at + 1)..];
            if (!userInfo.Contains(':') && TryBase64(userInfo, out var dec)) userInfo = dec; // SIP002 base64url userinfo
        }
        var mp = userInfo.Split(':', 2);
        if (mp.Length != 2) throw new FormatException("ss: missing method or password");
        var hp = hostPort.LastIndexOf(':');
        if (hp < 0 || !int.TryParse(hostPort[(hp + 1)..], out var port)) throw new FormatException("ss: invalid port");
        string? pluginName = null, pluginOpts = null;
        if (!string.IsNullOrEmpty(plugin)) { var sp = plugin.Split(';', 2); pluginName = sp[0]; pluginOpts = sp.ElementAtOrDefault(1); }
        return new ProxyNode
        {
            Protocol = ProxyProtocol.Shadowsocks, Name = name, Server = hostPort[..hp].Trim('[', ']'), Port = port,
            Method = mp[0], Password = mp[1], Plugin = pluginName, PluginOpts = pluginOpts,
        };
    }

    public static bool TryBase64(string s, out string decoded)
    {
        decoded = "";
        var t = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
        if (t.Length == 0 || t.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))) return false;
        t = t.TrimEnd('=');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(t)); return true; }
        catch (FormatException) { return false; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string[] SplitList(string? s) => string.IsNullOrWhiteSpace(s) ? [] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
