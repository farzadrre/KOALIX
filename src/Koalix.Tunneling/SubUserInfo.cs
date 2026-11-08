namespace Koalix.Tunneling;

/// <summary>
/// The panel's `subscription-userinfo` response header (v0.4.17): `upload=123; download=456; total=1024; expire=1700000000`.
/// Every field is optional; `expire` is a Unix timestamp in seconds. This is how subscription panels (x-ui, Marzban,
/// V2Board, RemNet…) tell the client how much volume is left and when the account expires.
/// </summary>
public sealed record SubUserInfo
{
    public long? UploadBytes { get; init; }
    public long? DownloadBytes { get; init; }
    public long? TotalBytes { get; init; }
    /// <summary>Unix seconds; null when the panel does not set an expiry.</summary>
    public long? ExpireUnix { get; init; }

    public long UsedBytes => (UploadBytes ?? 0) + (DownloadBytes ?? 0);

    /// <summary>Parses the header value. Tolerates missing fields, unknown fields, quoted values, spaces and
    /// trailing separators — panels are notoriously inconsistent. Returns null for a completely empty/invalid value.</summary>
    public static SubUserInfo? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        long? upload = null, download = null, total = null, expire = null;
        var sawAny = false;
        foreach (var raw in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var key = raw[..eq].Trim().ToLowerInvariant();
            var val = raw[(eq + 1)..].Trim().Trim('"');
            if (!long.TryParse(val, out var n)) continue; // floats/empty values are ignored, not fatal
            sawAny = true;
            switch (key)
            {
                case "upload": upload = n; break;
                case "download": download = n; break;
                case "total": total = n; break;
                case "expire": expire = n; break;
            }
        }
        return sawAny ? new SubUserInfo { UploadBytes = upload, DownloadBytes = download, TotalBytes = total, ExpireUnix = expire } : null;
    }

    /// <summary>True when the account's volume is exhausted (panel-reported, not a guess).</summary>
    public bool VolumeExhausted => TotalBytes is > 0 && UsedBytes >= TotalBytes;
    /// <summary>True when the account's expiry is in the past (panel-reported).</summary>
    public bool Expired => ExpireUnix is { } e && DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime <= DateTimeOffset.UtcNow.UtcDateTime;
}

/// <summary>One subscription fetch (v0.4.17): the parsed links plus the panel's userinfo header, when present.</summary>
public sealed record SubFetchResult(LinkParseResult Links, SubUserInfo? Info)
{
    public static SubFetchResult From(LinkParseResult links) => new(links, null);
}
