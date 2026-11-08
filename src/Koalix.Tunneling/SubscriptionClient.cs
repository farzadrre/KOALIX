using System.Net;

namespace Koalix.Tunneling;

/// <summary>
/// Downloads and parses a subscription. Each fetch builds its own short-lived HttpClient on purpose (v0.4.10):
/// the DI singleton rides the system proxy, so a stale WinINet entry from a previous session could break imports —
/// and when a tunnel is connected we want the request to go THROUGH it, because on Iranian ISPs the direct TLS
/// handshake to panel hosts (*.pages.dev & co.) is regularly reset by DPI ("The SSL connection could not be established").
/// </summary>
public static class SubscriptionClient
{
    public const int MaxBytes = 5 * 1024 * 1024;
    // A real browser UA: Cloudflare-fronted panels sometimes challenge obviously non-browser agents.
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Rejects non-HTTP(S) schemes, credentials in URLs, and loopback/link-local hosts (prevents SSRF against local services).</summary>
    public static bool IsValidSubscriptionUrl(string url, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { error = "Sub.Error.InvalidUrl"; return false; }
        // https only: subscription URLs and bodies contain credentials and must not travel in plaintext.
        if (u.Scheme != Uri.UriSchemeHttps) { error = "Sub.Error.Scheme"; return false; }
        if (!string.IsNullOrEmpty(u.UserInfo)) { error = "Sub.Error.Credentials"; return false; }
        if (u.IsLoopback || (IPAddress.TryParse(u.Host, out var ip) && (ip.IsIPv6LinkLocal || ip.ToString().StartsWith("169.254.")))) { error = "Sub.Error.LocalHost"; return false; }
        return true;
    }

    /// <summary>
    /// Fetches the subscription. <paramref name="viaProxyPort"/>: the active tunnel's local proxy — the download then
    /// travels through the tunnel and DPI never sees the panel host. Null = direct, with the system proxy ignored.
    /// The panel's <c>subscription-userinfo</c> response header (volume/expiry, v0.4.20) is captured alongside the links.
    /// </summary>
    public static async Task<SubFetchResult> FetchAsync(string url, int? viaProxyPort, CancellationToken ct)
    {
        if (!IsValidSubscriptionUrl(url, out var err)) throw new ArgumentException(err);
        using var h = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
        if (viaProxyPort is { } port)
        {
            h.Proxy = new WebProxy($"http://127.0.0.1:{port}");
            h.UseProxy = true;
        }
        using var http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(40) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Sub.Error.TooLarge");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) throw new InvalidDataException("Sub.Error.TooLarge");
        }
        var links = ShareLinkParser.ParseMany(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        return new SubFetchResult(links, SubUserInfo.Parse(resp.Headers.TryGetValues("subscription-userinfo", out var v) ? v.FirstOrDefault() : null));
    }
}
