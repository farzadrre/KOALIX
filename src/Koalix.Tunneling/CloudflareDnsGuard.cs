namespace Koalix.Tunneling;

/// <summary>
/// v0.4.24 (user-reported ZEUS panel configs, diagnosed externally): when the tunnel endpoint is a
/// Cloudflare <b>Worker</b> (<c>*.workers.dev</c> / <c>*.pages.dev</c> — no real origin server behind it),
/// every byte that leaves the tunnel exits INSIDE Cloudflare's own network. Cloudflare Workers cannot
/// open connections to Cloudflare's own addresses, so a tunnel DNS of <c>1.1.1.1</c> (or <c>1.0.0.1</c>)
/// is sent into the worker and the answer NEVER comes back — the tunnel "connects" but no site loads.
/// The guard swaps the tunnel DNS for <c>8.8.8.8</c> (reachable from inside Cloudflare) for exactly
/// those profiles, and ONLY while the user's configured resolver is still a Cloudflare one (a custom
/// resolver is left untouched — it is not a Cloudflare address, so the worker can reach it).
/// </summary>
public static class CloudflareDnsGuard
{
    /// <summary>The resolver KOALIX falls back to for worker-fronted profiles — Google public DNS, reachable from inside Cloudflare Workers.</summary>
    public const string FallbackDns = "8.8.8.8";

    /// <summary>Cloudflare's public resolvers — the addresses a Worker cannot connect back to.</summary>
    private static readonly HashSet<string> CloudflareResolvers = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001", "one.one.one.one", "cloudflare-dns.com",
    };

    /// <summary>True when the configured tunnel DNS is a Cloudflare resolver (the default <c>1.1.1.1</c> counts).</summary>
    public static bool IsCloudflareDns(string? dns) =>
        !string.IsNullOrWhiteSpace(dns) && CloudflareResolvers.Contains(dns.Trim());

    /// <summary>
    /// True when the profile's tunnel endpoint is a Cloudflare Worker / Pages function. Detection is deliberately
    /// token-based across the WHOLE config: worker panels appear as share links (<c>vless://…@sub.workers.dev…</c>),
    /// sing-box JSON and Xray JSON, and in every shape the literal <c>workers.dev</c> / <c>pages.dev</c> token is
    /// present in the endpoint host, the SNI or the Host header. A false positive only swaps 1.1.1.1 → 8.8.8.8
    /// (harmless); a false negative reproduces the "connected but nothing loads" bug, so err on detecting.
    /// </summary>
    public static bool IsWorkerFronted(TunnelProfile profile, out string matched)
    {
        matched = "";
        if (string.IsNullOrEmpty(profile.Content)) return false;
        if (ContainsToken(profile.Content))
        {
            matched = TokenIn(profile.Content);
            return true;
        }
        // Share links can hide the worker behind an IP endpoint with the host/SNI carrying the token — the content
        // scan above already catches that (the token is in the query string), so nothing more is needed here; the
        // structured pass below only adds the "worker" name hint that the raw scan intentionally does not trust.
        try
        {
            if (profile.Source == ProfileSource.ShareLink)
            {
                var n = ShareLinkParser.Parse(profile.Content);
                if (MentionsWorker(n.Server) || MentionsWorker(n.Sni) || MentionsWorker(n.Host) || MentionsWorker(n.Name) || MentionsWorker(profile.Name))
                {
                    matched = "worker";
                    return true;
                }
            }
        }
        catch { /* not a parseable link — the token scan above already decided */ }
        return false;
    }

    private static bool ContainsToken(string s) => TokenIn(s).Length > 0;

    private static string TokenIn(string s) =>
        s.Contains("workers.dev", StringComparison.OrdinalIgnoreCase) ? "workers.dev" :
        s.Contains("pages.dev", StringComparison.OrdinalIgnoreCase) ? "pages.dev" : "";

    private static bool MentionsWorker(string? s) =>
        s is not null && s.Contains("worker", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The connect-time decision: the profile is worker-fronted AND the configured tunnel DNS is a Cloudflare
    /// resolver (or unset). Returns the replacement DNS; null = keep what the user configured.
    /// </summary>
    public static string? OverrideFor(TunnelProfile profile, string? configuredDns, out string reason)
    {
        if (!IsWorkerFronted(profile, out var token)) { reason = ""; return null; }
        if (!IsCloudflareDns(configuredDns)) { reason = ""; return null; }
        reason = token;
        return FallbackDns;
    }
}
