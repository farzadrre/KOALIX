namespace Koalix.Tunneling;

public sealed record OpenVpnRemote(string Host, int Port, string Proto);

/// <summary>
/// Parsed .ovpn profile (v0.4.15). KOALIX stores ONE self-contained string: at import time every external file
/// reference (ca/cert/key/tls-auth/tls-crypt/crl-verify/dh) that can be found next to the .ovpn file is inlined as
/// the matching OpenVPN block, so the profile keeps working no matter where later runs resolve files from.
/// Secrets (key/cert/tls-auth/tls-crypt/auth-user-pass payloads) are only ever stored DPAPI-encrypted inside the
/// profile — never logged, only shown redacted.
/// </summary>
public sealed record OpenVpnConfig
{
    public IReadOnlyList<OpenVpnRemote> Remotes { get; init; } = [];
    public string? Device { get; init; }
    public string? Cipher { get; init; }
    public bool HasCa { get; init; }
    public bool HasAuthUserPass { get; init; }
    /// <summary>auth-user-pass with the username/password already inside the config (injected at import).</summary>
    public bool HasInlineCredentials { get; init; }
    /// <summary>tls-auth/tls-crypt static key present (inline or referenced).</summary>
    public bool HasTlsKey { get; init; }
    /// <summary>Client certificate present (inline &lt;cert&gt; block or a cert directive) — v0.4.18.</summary>
    public bool HasCert { get; init; }
    /// <summary>Client private key present (inline &lt;key&gt; block or a key directive) — v0.4.18.</summary>
    public bool HasKey { get; init; }
    /// <summary>The profile sets its own --data-ciphers list (v0.4.18 — KOALIX only appends one when it doesn't).</summary>
    public bool HasDataCiphers { get; init; }
    /// <summary>The profile sets its own --pull-filter rules (v0.4.18 — KOALIX only appends one when it doesn't).</summary>
    public bool HasPullFilter { get; init; }
    public bool IsPkcs12 { get; init; }
    /// <summary>True when the .ovpn sets its own "verb" log level (v0.4.17 — KOALIX adds --verb 3 only when it doesn't).</summary>
    public bool HasVerb { get; init; }
    public IReadOnlyList<string> ExtraFlags { get; init; } = [];

    public string DisplayEndpoint => Remotes.Count == 0 ? "?" : $"{Remotes[0].Host}:{Remotes[0].Port}";

    /// <summary>
    /// Replaces relative external file references (ca/cert/key/dh/crl-verify/tls-auth/tls-crypt) with the matching
    /// inline blocks, reading the files from <paramref name="baseDir"/>. Missing files leave their directive in
    /// place so validation can report the problem. pkcs12 bundles are binary and cannot be inlined.
    /// </summary>
    public static string InlineExternalFiles(string content, string baseDir)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith(';')) continue;
            var sp = raw.IndexOf(' ');
            var directive = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant();
            var args = sp < 0 ? "" : raw[(sp + 1)..].Trim();
            switch (directive)
            {
                case "ca": lines[i] = Inline(args, baseDir, "ca"); break;
                case "cert": lines[i] = Inline(args, baseDir, "cert"); break;
                case "key": lines[i] = Inline(args, baseDir, "key"); break;
                case "dh": lines[i] = Inline(args, baseDir, "dh"); break;
                case "crl-verify" when !args.StartsWith("dir", StringComparison.Ordinal):
                    lines[i] = Inline(args, baseDir, "crl-verify"); break;
                case "tls-auth":
                    // tls-auth ta.key 1 — the direction argument becomes a key-direction directive.
                    var p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var dir = p.Length > 1 ? $"key-direction {p[1]}\n" : "";
                    lines[i] = dir + Inline(p[0], baseDir, "tls-auth");
                    break;
                case "tls-crypt": lines[i] = Inline(args, baseDir, "tls-crypt"); break;
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>One reference → one inline block; untouched directive when the file is absent/unreadable/rooted.</summary>
    private static string Inline(string args, string baseDir, string blockName)
    {
        var path = args.Split(' ', 2)[0].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (path.Contains("..")) return $"{blockName} {args}";
        if (Path.IsPathRooted(path)) return $"{blockName} {args}";
        var full = Path.Combine(baseDir, path);
        if (!File.Exists(full)) return $"{blockName} {args}";
        string body;
        try { body = File.ReadAllText(full); } catch { return $"{blockName} {args}"; }
        if (string.IsNullOrWhiteSpace(body)) return $"{blockName} {args}";
        return $"<{blockName}>\n{body.TrimEnd()}\n</{blockName}>";
    }

    /// <summary>Parses (already-inlined) .ovpn content. Errors are localization keys (Ovpn.Error.*) for translated UI messages.
    /// v0.4.16: external file references that survived the import (the file was not found next to the .ovpn) are now
    /// reported instead of failing opaquely at runtime — openvpn.exe resolves relative paths against its own working
    /// directory, where those files never are.</summary>
    public static OpenVpnConfig Parse(string content, out List<string> errors)
    {
        errors = [];
        var lines = (content ?? "").Replace("\r\n", "\n").Split('\n');
        var remotes = new List<OpenVpnRemote>();
        string? device = null, cipher = null;
        var hasCa = false; var hasAuthUserPass = false; var inlineCreds = false; var hasTlsKey = false; var isPkcs12 = false; var hasVerb = false;
        var hasCert = false; var hasKey = false; var hasDataCiphers = false; var hasPullFilter = false;
        var externalRefs = new List<string>();
        var extra = new List<string>();
        // Remember the raw index of the last "remote" so a later global "proto" can also apply to it.
        var lastRemoteIsGlobalProto = new List<bool>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith(';')) continue;
            var sp = raw.IndexOf(' ');
            var directive = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant();
            var args = sp < 0 ? "" : raw[(sp + 1)..].Trim();

            switch (directive)
            {
                case "remote":
                {
                    var p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (p.Length < 2 || !int.TryParse(p[1], out var port) || port is < 1 or > 65535)
                        errors.Add("Ovpn.Error.RemoteInvalid");
                    else
                    {
                        remotes.Add(new OpenVpnRemote(p[0], port, p.Length >= 3 ? NormalizeProto(p[2]) : "udp"));
                        lastRemoteIsGlobalProto.Add(p.Length < 3);
                    }
                    break;
                }
                case "proto":
                {
                    var proto = NormalizeProto(args);
                    for (var r = 0; r < lastRemoteIsGlobalProto.Count; r++)
                        if (lastRemoteIsGlobalProto[r]) remotes[r] = remotes[r] with { Proto = proto };
                    break;
                }
                case "dev": device = args.Split(' ')[0]; break;
                case "cipher" or "data-ciphers-fallback": cipher ??= args.Split(' ')[0]; break;
                case "data-ciphers": cipher ??= args.Split(' ')[0].Split(':')[0]; hasDataCiphers = true; break;
                case "<ca>": hasCa = true; SkipBlock(lines, ref i, "ca"); break;
                case "<cert>": hasCert = true; SkipBlock(lines, ref i, "cert"); break;
                case "<extra-certs>": SkipBlock(lines, ref i, directive.Trim('<', '>')); break;
                case "<key>": hasKey = true; SkipBlock(lines, ref i, "key"); break;
                case "<tls-auth>": hasTlsKey = true; SkipBlock(lines, ref i, "tls-auth"); break;
                case "<tls-crypt>": hasTlsKey = true; SkipBlock(lines, ref i, "tls-crypt"); break;
                // External file references left after import (file missing next to the .ovpn at import time):
                // KOALIX never ships those files, so openvpn.exe would die with "Cannot open ..." — say so up front.
                case "ca": hasCa = true; if (args.Length > 0) externalRefs.Add("ca"); break;
                case "cert": hasCert = true; if (args.Length > 0) externalRefs.Add(directive); break;
                case "key": hasKey = true; if (args.Length > 0) externalRefs.Add(directive); break;
                case "dh" or "crl-verify": if (args.Length > 0) externalRefs.Add(directive); break;
                case "tls-auth" or "tls-crypt": hasTlsKey = true; if (args.Length > 0) externalRefs.Add(directive); break;
                case "pkcs12": isPkcs12 = true; break;      // binary bundle — cannot be inlined
                case "pull-filter": hasPullFilter = true; break;
                case "verb": hasVerb = true; break;
                case "config": errors.Add("Ovpn.Error.NestedConfig"); break;
                case "auth-user-pass":
                    hasAuthUserPass = true;
                    // Inline credentials form: the next two non-empty lines are username and password.
                    inlineCreds = i + 2 < lines.Length
                        && !IsDirective(lines[i + 1]) && lines[i + 1].Trim().Length > 0
                        && !IsDirective(lines[i + 2]) && lines[i + 2].Trim().Length > 0;
                    break;
                default:
                    // Unknown directives are tolerated (OpenVPN itself decides); track a few for the log.
                    if (extra.Count < 12) extra.Add(directive);
                    break;
            }
        }

        if (remotes.Count == 0) errors.Add("Ovpn.Error.NoRemote");
        if (!hasCa && !isPkcs12) errors.Add("Ovpn.Error.MissingCa");
        if (externalRefs.Count > 0) errors.Add("Ovpn.Error.ExternalFiles");
        return new OpenVpnConfig
        {
            Remotes = remotes, Device = device, Cipher = cipher, HasCa = hasCa,
            HasAuthUserPass = hasAuthUserPass, HasInlineCredentials = inlineCreds,
            HasTlsKey = hasTlsKey, HasCert = hasCert, HasKey = hasKey,
            HasDataCiphers = hasDataCiphers, HasPullFilter = hasPullFilter,
            IsPkcs12 = isPkcs12, HasVerb = hasVerb, ExtraFlags = extra,
        };
    }

    /// <summary>
    /// Cheap structural sniff: does this text look like an .ovpn profile? Used by the import chain (v0.4.16) so a
    /// config with reportable problems (missing remote, external file references…) gets the honest OpenVPN error
    /// message instead of falling through to share-link parsing and a confusing "unsupported scheme" report.
    /// </summary>
    public static bool LooksLikeOpenVpn(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith(';')) continue;
            if (t.Equals("client", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("remote ", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("<ca>", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("pkcs12 ", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("auth-user-pass", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsDirective(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith('<') || t.StartsWith('#') || t.StartsWith(';');
    }

    private static string NormalizeProto(string p) => p.ToLowerInvariant() switch
    {
        "udp4" or "udp6" or "udp" or "udp-client" => "udp",
        "tcp4" or "tcp6" or "tcp" or "tcp-client" => "tcp",
        _ => p.ToLowerInvariant().StartsWith("udp") ? "udp" : "tcp",
    };

    /// <summary>
    /// v0.4.18 — the field bug behind «select certificate → continue»: profiles that carry a client
    /// <c>cert</c> but NO <c>key</c> (or the reverse) make openvpn.exe die at option parsing with
    /// “Options error: If you use one of --cert or --key, you must use them both” — exit code 1 before
    /// a single packet leaves, which became the connect/disconnect flapping. The official OpenVPN
    /// Connect app handles the same profile by showing “Select Certificate” and connecting happily when
    /// the user just presses Continue (server-side auth works via user-pass or no client cert at all).
    /// KOALIX now does the equivalent automatically: an UNPAIRED cert/key is dropped from the config that
    /// reaches openvpn.exe; a real cert+key pair is kept untouched.
    /// </summary>
    public static string SanitizeClientCertPair(string content, out string note)
    {
        note = "";
        var cfg = Parse(content, out _);
        if (cfg.HasCert == cfg.HasKey) return content; // pair complete (or neither) — nothing to do
        var dropCert = cfg.HasCert;                    // cert without key → drop the cert
        var lines = (content ?? "").Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t = raw.Trim();
            var opener = t.StartsWith('<') ? t.Trim('<', '>').ToLowerInvariant() : null;
            if (dropCert && opener is "cert" or "extra-certs") { SkipBlock(lines, ref i, opener); continue; }
            if (!dropCert && opener is "key") { SkipBlock(lines, ref i, "key"); continue; }
            var sp = t.IndexOf(' ');
            var directive = (sp < 0 ? t : t[..sp]).ToLowerInvariant();
            if (directive == (dropCert ? "cert" : "key") && !t.StartsWith('#') && !t.StartsWith(';')) continue;
            outLines.Add(raw);
        }
        note = dropCert
            ? "profile has a client cert but no key — dropped the cert (auth falls back to user-pass, like OpenVPN Connect's “Continue” without a certificate)"
            : "profile has a client key but no cert — dropped the key (openvpn.exe refuses one without the other)";
        return string.Join("\n", outLines);
    }

    /// <summary>Skips an inline &lt;block&gt;…&lt;/block&gt; — the opener sits at index i.</summary>
    private static void SkipBlock(string[] lines, ref int i, string name)
    {
        i++;
        while (i < lines.Length && !lines[i].Trim().Equals($"</{name}>", StringComparison.OrdinalIgnoreCase)) i++;
    }

    /// <summary>Redacted preview for the Tunnels page: secret blocks show as markers, payloads are never printed.</summary>
    public static string ToRedacted(string content)
    {
        var sb = new System.Text.StringBuilder();
        var inSecret = false;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith('<'))
            {
                var closing = t.StartsWith("</");
                var secretBlock = t.Contains("key") || t.Contains("cert") || t.Contains("tls-auth") || t.Contains("tls-crypt") || t.Contains("extra-certs");
                if (secretBlock) { inSecret = !closing; sb.AppendLine(t); continue; }
                inSecret = false;
                sb.AppendLine(t);
                continue;
            }
            if (inSecret || t.StartsWith("-----")) continue; // PEM bodies
            var sp = t.IndexOf(' ');
            var d = sp < 0 ? t : t[..sp].ToLowerInvariant();
            sb.AppendLine(d is "key" or "tls-auth" or "tls-crypt" or "pkcs12" or "auth-user-pass" ? d + " ***" : t);
        }
        return sb.ToString().TrimEnd();
    }
}
