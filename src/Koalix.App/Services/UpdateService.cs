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
