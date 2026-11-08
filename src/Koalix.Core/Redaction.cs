using System.Text.RegularExpressions;

namespace Koalix.Core;

/// <summary>Removes secrets (private keys, passwords, UUIDs, tokens, share links) from text before it is logged or exported.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\b((PrivateKey|PresharedKey)\s*=\s*)[A-Za-z0-9+/=]{16,}")] private static partial Regex WgKey();
    [GeneratedRegex(@"(?i)(""(private_key|pre_shared_key|password|uuid|secret|auth_str|token|psk|id)""\s*:\s*"")[^""]*("")")] private static partial Regex JsonSecret();
    [GeneratedRegex(@"(?i)\b(vmess|vless|trojan|ss|ssr|hysteria2|hy2|tuic|wireguard)://\S+")] private static partial Regex ShareLink();
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")] private static partial Regex Uuid();
    [GeneratedRegex(@"(?i)([?&](token|key|secret|password|auth)=)[^&\s]+")] private static partial Regex QuerySecret();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex Ipv4();

    public static string Redact(string? text, bool redactIpAddresses = false)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var s = WgKey().Replace(text, "$1[REDACTED]");
        s = JsonSecret().Replace(s, "$1[REDACTED]$3");
        s = ShareLink().Replace(s, m => m.Groups[1].Value + "://[REDACTED]");
        s = Uuid().Replace(s, "[UUID]");
        s = QuerySecret().Replace(s, "$1[REDACTED]");
        if (redactIpAddresses) s = Ipv4().Replace(s, "x.x.x.x");
        return s;
    }
}
