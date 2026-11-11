using System.Net.Http;
using Koalix.Infrastructure;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace Koalix.App.Services;

/// <summary>
/// In-app updates via Velopack. The feed is either a GitHub repository (releases created with <c>vpk upload github</c>)
/// or any static HTTPS folder that hosts the files produced by <c>vpk pack</c> (releases.win.json + .nupkg).
/// Only works for the installed build (Setup.exe); the portable build reports <see cref="IsInstalled"/> = false.
/// </summary>
public sealed class UpdateService(SettingsRepository settings)
{
    public const string FeedKey = "update.feed", AutoKey = "update.auto";
    /// <summary>Official release feed (GitHub Releases built by .github/workflows/release.yml). Used when the user leaves the setting empty.</summary>
    public const string DefaultFeed = "https://github.com/farzadrre/KOALIX";
    private UpdateInfo? _pending;

    public string Feed => settings.Get(FeedKey) is { Length: > 0 } f && !string.IsNullOrWhiteSpace(f) ? f : DefaultFeed;
    public bool AutoCheck => settings.Get(AutoKey) != "0";
    public UpdateInfo? Pending => _pending;

    /// <summary>v0.4.42 — newest version seen by the last online check; kept so every launch can announce it (even offline)
    /// until the user actually updates.</summary>
    public const string LatestKey = "update.latest";

    /// <summary>The remembered newer version, or null when none / already installed.</summary>
    public string? KnownLatest => settings.Get(LatestKey) is { Length: > 0 } v && IsNewer(v, CurrentVersion) ? v : null;

    private bool FeedIsGithub => Uri.TryCreate(Feed, UriKind.Absolute, out var u) && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a portable user downloads the new build.</summary>
    public string ReleasePage => FeedIsGithub ? Feed.TrimEnd('/') + "/releases/latest" : Feed;

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var a)
        && Version.TryParse(current.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var b) && a > b;

    /// <summary>
    /// v0.4.42 — works for BOTH builds: the installed one asks Velopack, the portable one reads the latest GitHub release
    /// tag. Returns the newer version (and remembers it) or null when up to date. Throws on network errors.
    /// </summary>
    public async Task<string?> CheckLatestAsync(HttpClient http, CancellationToken ct = default)
    {
        string? latest;
        if (IsInstalled) latest = await CheckAsync().ConfigureAwait(false);
        else if (FeedIsGithub)
        {
            var path = new Uri(Feed).AbsolutePath.Trim('/');
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{path}/releases/latest");
            req.Headers.UserAgent.ParseAdd("KOALIX/" + CurrentVersion);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release published yet
            res.EnsureSuccessStatusCode();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            latest = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString()?.Trim().TrimStart('v', 'V') : null;
        }
        else return null; // a custom static feed without Velopack install: nothing we can ask
        if (latest is not null && IsNewer(latest, CurrentVersion))
        {
            settings.Set(LatestKey, latest);
            return latest;
        }
        if (!string.IsNullOrEmpty(settings.Get(LatestKey))) settings.Set(LatestKey, "");
        return null;
    }

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    private UpdateManager? Manager()
    {
        var feed = Feed?.Trim();
        if (string.IsNullOrEmpty(feed) || !Uri.TryCreate(feed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        IUpdateSource source = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(feed, null, false)
            : new SimpleWebSource(feed);
        return new UpdateManager(source);
    }

    public bool IsInstalled
    {
        get { try { return new UpdateManager(new SimpleWebSource("https://localhost/")).IsInstalled; } catch { return false; } }
    }

    /// <returns>New version string, or null when up to date. Throws <see cref="InvalidOperationException"/> with a localization key on setup problems.</returns>
    public async Task<string?> CheckAsync()
    {
        var m = Manager() ?? throw new InvalidOperationException("Update.Error.NoFeed");
        if (!m.IsInstalled) throw new InvalidOperationException("Update.Error.Portable");
        _pending = await m.CheckForUpdatesAsync().ConfigureAwait(false);
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken ct)
    {
        var m = Manager() ?? throw new InvalidOperationException("Update.Error.NoFeed");
        if (_pending is null) throw new InvalidOperationException("Update.Error.NothingPending");
        await m.DownloadUpdatesAsync(_pending, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Restarts into the new version. Caller must tear down the tunnel first (DNS / proxy / firewall restored).</summary>
    public void ApplyAndRestart()
    {
        var m = Manager() ?? throw new InvalidOperationException("Update.Error.NoFeed");
        if (_pending is null) throw new InvalidOperationException("Update.Error.NothingPending");
        m.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>Uninstall hook (runs before files are removed): drop the "start with Windows" entry.</summary>
    public static void CleanupOnUninstall()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("KOALIX", false);
        }
        catch { /* best effort */ }
    }
}
