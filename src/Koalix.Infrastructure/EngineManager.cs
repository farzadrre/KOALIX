using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Koalix.Infrastructure;

public sealed record GitHubAsset([property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);
public sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

public sealed record EngineSpec(string Name, string Repo, Func<string, bool> AssetMatcher, string ExeName);

public sealed record EngineInstallResult(bool Ok, string? Version, string? Error);

/// <summary>
/// Downloads engines from official GitHub releases only. The SHA-256 is verified against GitHub's asset "digest"
/// (or Xray's .dgst file). If no trustworthy hash is available, installation is refused.
/// </summary>
public sealed class EngineManager(HttpClient http, string enginesDir)
{
    public static EngineSpec SingBoxWindows { get; } = new("sing-box", "SagerNet/sing-box",
        n => n.EndsWith("-windows-amd64.zip", StringComparison.Ordinal) && !n.Contains("legacy"), "sing-box.exe");
    public static EngineSpec XrayWindows { get; } = new("xray", "XTLS/Xray-core", n => n == "Xray-windows-64.zip", "xray.exe");

    public async Task<GitHubRelease> LatestAsync(EngineSpec spec, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{spec.Repo}/releases/latest");
        req.Headers.UserAgent.ParseAdd("KOALIX-Gaming-Suite");
        using var res = await http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<GitHubRelease>(ct))!;
    }

    public async Task<EngineInstallResult> InstallAsync(EngineSpec spec, IProgress<double>? progress, CancellationToken ct)
    {
        var rel = await LatestAsync(spec, ct);
        var asset = rel.Assets.FirstOrDefault(a => spec.AssetMatcher(a.Name));
        if (asset is null) return new(false, rel.Tag, "Engine.Error.NoAsset");
        if (!asset.Url.StartsWith($"https://github.com/{spec.Repo}/releases/download/", StringComparison.Ordinal))
            return new(false, rel.Tag, "Engine.Error.UntrustedUrl");

        var expected = await ExpectedSha256Async(rel, asset, ct);
        if (expected is null) return new(false, rel.Tag, "Engine.Error.NoChecksum");

        var tmp = Path.Combine(Path.GetTempPath(), "koalix-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DownloadAsync(asset.Url, tmp, asset.Size, progress, ct);
            await using (var f = File.OpenRead(tmp))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return new(false, rel.Tag, "Engine.Error.ChecksumMismatch");
            }
            var target = Path.Combine(enginesDir, spec.Name);
            var staging = target + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(tmp, staging);
            var exe = Directory.GetFiles(staging, spec.ExeName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null) return new(false, rel.Tag, "Engine.Error.ExeMissing");
            // flatten folder (sing-box zips contain a versioned sub folder)
            var exeDir = Path.GetDirectoryName(exe)!;
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(exeDir, target);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await File.WriteAllTextAsync(Path.Combine(target, "VERSION"), rel.Tag, ct);
            return new(true, rel.Tag, null);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public string? InstalledVersion(EngineSpec spec)
    {
        var f = Path.Combine(enginesDir, spec.Name, "VERSION");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
    }

    private async Task<string?> ExpectedSha256Async(GitHubRelease rel, GitHubAsset asset, CancellationToken ct)
    {
        if (asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.Ordinal)) return d[7..];
        var dgst = rel.Assets.FirstOrDefault(a => a.Name == asset.Name + ".dgst");
        if (dgst is null) return null;
        var text = await http.GetStringAsync(dgst.Url, ct);
        return ParseDgst(text);
    }

    /// <summary>Parses Xray .dgst ("SHA2-256= hex" or "SHA256 (file) = hex").</summary>
    public static string? ParseDgst(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (!(l.StartsWith("SHA2-256", StringComparison.OrdinalIgnoreCase) || l.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase))) continue;
            var hex = l[(l.LastIndexOf('=') + 1)..].Trim();
            if (hex.Length == 64 && hex.All(Uri.IsHexDigit)) return hex.ToLowerInvariant();
        }
        return null;
    }

    private async Task DownloadAsync(string url, string file, long size, IProgress<double>? progress, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); res.EnsureSuccessStatusCode();
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buf = new byte[81920]; long total = 0; int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct); total += n;
            if (size > 0) progress?.Report((double)total / size);
            if (total > 300_000_000) throw new InvalidDataException("Download too large");
        }
    }
}
