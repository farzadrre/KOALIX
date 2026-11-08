namespace Koalix.Core;

public enum SplitMode
{
    /// <summary>All traffic goes through the tunnel (no per-app routing).</summary>
    FullTunnel,
    /// <summary>Mode A: only selected applications use the tunnel.</summary>
    Include,
    /// <summary>Mode B: everything uses the tunnel except selected applications.</summary>
    Exclude,
}

public enum AppAction { Tunnel, Direct, Block }

public sealed record AppRule
{
    public long Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Full, validated executable path.</summary>
    public required string ExecutablePath { get; init; }
    public AppAction Action { get; init; } = AppAction.Tunnel;
    /// <summary>When true, every executable under the application's folder matches (games that spawn helper/child processes).</summary>
    public bool IncludeFolder { get; init; }
    public string? Group { get; init; }
    public string ExecutableName => Path.GetFileName(ExecutablePath.Replace('\\', '/'));
    /// <summary>A folder rule targets every executable under a directory (ExecutablePath holds the folder itself).</summary>
    public bool IsFolderRule => !ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    /// <summary>Directory prefix (with trailing backslash) matched when <see cref="IncludeFolder"/> is set.</summary>
    public string FolderPrefix
    {
        get
        {
            var p = ExecutablePath.Replace('/', '\\');
            if (!IsFolderRule) { var i = p.LastIndexOf('\\'); p = i > 0 ? p[..i] : p; }
            return p.TrimEnd('\\') + "\\";
        }
    }
    public bool Matches(string exePath) =>
        (!IsFolderRule && string.Equals(ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase))
        || ((IncludeFolder || IsFolderRule) && exePath.Replace('/', '\\').StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase));
}

public sealed record SplitPolicy(SplitMode Mode, IReadOnlyList<AppRule> Rules, bool TunnelDns = true, bool BlockIpv6Leaks = false, bool IranDirectPreset = false)
{
    public static SplitPolicy Full { get; } = new(SplitMode.FullTunnel, []);

    /// <summary>v0.4.32 — domain rules: these sites ALWAYS go through the tunnel, whatever app opens them
    /// (matched on the sniffed TLS SNI / HTTP Host, suffix match: "google.com" also covers "www.google.com").</summary>
    public IReadOnlyList<string> ProxyDomains { get; init; } = [];

    /// <summary>v0.4.32 — domain rules: these sites ALWAYS go direct (and resolve through the local resolver).</summary>
    public IReadOnlyList<string> DirectDomains { get; init; } = [];

    /// <summary>v0.4.33 — refuse QUIC (UDP 443) of tunneled traffic so browsers use TCP+TLS at once. Most proxy
    /// transports (every Cloudflare-worker VLESS/Trojan, any WS/gRPC/HTTPUpgrade path) cannot carry UDP, so QUIC
    /// to Google/YouTube vanished into the tunnel and Chrome waited on it. On by default.</summary>
    public bool BlockQuic { get; init; } = true;

    /// <summary>Parses a user-typed list (one per line, or comma/space separated; URLs, "*.x" and "." prefixes
    /// accepted) into normalised lowercase domain suffixes. Junk entries are dropped, duplicates removed.</summary>
    public static IReadOnlyList<string> ParseDomains(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<string>();
        foreach (var raw in text.Split(['\n', '\r', ',', ' ', '\t', ';', '،'], StringSplitOptions.RemoveEmptyEntries))
        {
            var d = raw.Trim().ToLowerInvariant();
            if (d.StartsWith('#')) continue;
            var scheme = d.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) d = d[(scheme + 3)..];
            var slash = d.IndexOfAny(['/', '?', '#']);
            if (slash >= 0) d = d[..slash];
            var colon = d.LastIndexOf(':');
            if (colon > 0) d = d[..colon];
            d = d.TrimStart('*').Trim('.');
            if (d.Length < 2 || d.Length > 253 || !d.Contains('.')) continue;
            if (!d.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.')) continue;
            if (!list.Contains(d)) list.Add(d);
        }
        return list;
    }

    /// <summary>Human-readable preview: which apps are tunneled, direct and blocked.</summary>
    public (IReadOnlyList<AppRule> Tunneled, IReadOnlyList<AppRule> Direct, IReadOnlyList<AppRule> Blocked, string DefaultRoute) Preview()
    {
        var blocked = Rules.Where(r => r.Action == AppAction.Block).ToList();
        return Mode switch
        {
            SplitMode.Include => (Rules.Where(r => r.Action == AppAction.Tunnel).ToList(), Rules.Where(r => r.Action == AppAction.Direct).ToList(), blocked, "direct"),
            SplitMode.Exclude => (Rules.Where(r => r.Action == AppAction.Tunnel).ToList(), Rules.Where(r => r.Action == AppAction.Direct).ToList(), blocked, "tunnel"),
            _ => ([], [], blocked, "tunnel"),
        };
    }

    /// <summary>Validates that the policy can be enforced. Returns error codes (localization keys).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Mode == SplitMode.Include && !Rules.Any(r => r.Action == AppAction.Tunnel))
            errors.Add("Split.Error.IncludeNeedsApp");
        foreach (var r in Rules)
            if (!PathValidator.IsValidRulePath(r)) errors.Add("Split.Error.InvalidPath:" + r.ExecutablePath);
        return errors;
    }
}

public static class PathValidator
{
    private static readonly char[] Forbidden = ['"', '<', '>', '|', '*', '?', '\0', '\n', '\r'];

    /// <summary>Accepts absolute Windows paths to .exe files only, without wildcards or control characters.</summary>
    public static bool IsValidExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) return false;
        if (path.IndexOfAny(Forbidden) >= 0) return false;
        var isDrive = path.Length > 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        var isUnc = path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
        if (!isDrive && !isUnc) return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accepts absolute Windows folder paths (not a drive root, no wildcards / traversal).</summary>
    public static bool IsValidFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) return false;
        if (path.IndexOfAny(Forbidden) >= 0 || path.Contains("..", StringComparison.Ordinal)) return false;
        var t = path.Replace('/', '\\').TrimEnd('\\');
        var isDrive = t.Length > 3 && char.IsLetter(t[0]) && t[1] == ':' && t[2] == '\\';
        var isUnc = t.StartsWith(@"\\", StringComparison.Ordinal) && !t.StartsWith(@"\\?\", StringComparison.Ordinal) && t.Count(c => c == '\\') >= 4;
        if (!isDrive && !isUnc) return false;
        return !t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidRulePath(AppRule r) => r.IsFolderRule ? r.IncludeFolder && IsValidFolderPath(r.ExecutablePath) : IsValidExecutablePath(r.ExecutablePath);
}
