namespace Koalix.Tunneling;

public enum ProxyProtocol { VMess, VLess, Trojan, Shadowsocks, Hysteria2, Tuic, Socks, Http }

/// <summary>uTLS fingerprint normalization shared by BOTH cores (v0.4.22).
/// ZEUS/BPB-style panels emit <c>fp=unsafe</c> on every link — Xray-core FATALs on it
/// («initialize outbound[0]: unknown uTLS fingerprint: unsafe», the v0.4.21 field log: the user's
/// whole subscription died here while the same links worked in other clients, which silently drop
/// the field). Any unknown/empty value falls back to chrome: uTLS-chrome is accepted by every
/// standard TLS server and is already the builders' DPI-safe default.</summary>
public static class Utls
{
    /// <summary>Fingerprints valid in BOTH Xray-core and sing-box (case-insensitive).</summary>
    public static readonly string[] KnownFingerprints =
        ["chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized"];

    public static string Normalize(string? fingerprint)
    {
        var f = (fingerprint ?? "").Trim().ToLowerInvariant();
        return KnownFingerprints.Contains(f) ? f : "chrome";
    }
}

/// <summary>Engine-neutral description of one proxy server parsed from a share link or subscription.</summary>
public sealed record ProxyNode
{
    public required ProxyProtocol Protocol { get; init; }
    public required string Name { get; init; }
    public required string Server { get; init; }
    public required int Port { get; init; }
    /// <summary>Original share link (stored encrypted as the profile content).</summary>
    public string? RawLink { get; init; }
    public string? Uuid { get; init; }
    public string? Password { get; init; }
    public string? Username { get; init; }
    public string? Method { get; init; }          // shadowsocks cipher
    public string? VmessSecurity { get; init; }   // auto / aes-128-gcm / chacha20-poly1305 / none
    public int AlterId { get; init; }
    public string? Flow { get; init; }            // xtls-rprx-vision
    public string Transport { get; init; } = "tcp"; // tcp, ws, grpc, http, httpupgrade, xhttp, quic
    public string? Path { get; init; }
    public string? Host { get; init; }
    public string? ServiceName { get; init; }
    public string? HeaderType { get; init; }
    public string Security { get; init; } = "none"; // none, tls, reality
    public string? Sni { get; init; }
    public string[] Alpn { get; init; } = [];
    public string? Fingerprint { get; init; }
    /// <summary>Sanitized uTLS fingerprint for core configs — never an unknown value (see <see cref="Utls"/>).</summary>
    public string SafeFingerprint => Utls.Normalize(Fingerprint);
    public string? RealityPublicKey { get; init; }
    public string? RealityShortId { get; init; }
    public bool AllowInsecure { get; init; }
    public string? Obfs { get; init; }
    public string? ObfsPassword { get; init; }
    public string? CongestionControl { get; init; }
    public string? Plugin { get; init; }
    public string? PluginOpts { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(Server) || Server.Length > 253 || Server.Any(char.IsWhiteSpace)) e.Add("Proxy.Error.Server");
        if (Port is < 1 or > 65535) e.Add("Proxy.Error.Port");
        switch (Protocol)
        {
            case ProxyProtocol.VMess or ProxyProtocol.VLess when !System.Guid.TryParse(Uuid, out _) && string.IsNullOrEmpty(Uuid):
                e.Add("Proxy.Error.Uuid"); break;
            case ProxyProtocol.Trojan or ProxyProtocol.Hysteria2 when string.IsNullOrEmpty(Password):
                e.Add("Proxy.Error.Password"); break;
            case ProxyProtocol.Shadowsocks when string.IsNullOrEmpty(Method) || string.IsNullOrEmpty(Password):
                e.Add("Proxy.Error.ShadowsocksCredentials"); break;
            case ProxyProtocol.Tuic when string.IsNullOrEmpty(Uuid) || string.IsNullOrEmpty(Password):
                e.Add("Proxy.Error.TuicCredentials"); break;
        }
        if (Security == "reality" && string.IsNullOrEmpty(RealityPublicKey)) e.Add("Proxy.Error.RealityKey");
        return e;
    }

    public string Summary => $"{Protocol} {Server}:{Port} [{Transport}/{Security}]";
}
