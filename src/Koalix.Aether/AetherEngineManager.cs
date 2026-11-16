using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.Aether;

/// <summary>v0.4.48 — engine placement + download for the Aether core. Same trust rules as the sing-box/Xray
/// downloader: official GitHub releases only, SHA-256 verified against the release's SHA256SUMS.txt (or the
/// GitHub asset digest), refuse on any mismatch. The pinned version matches Aether-GUI's src-tauri/binaries pin.</summary>
public sealed class AetherEngineManager(HttpClient http, EnginePaths paths)
{
    /// <summary>The engine folder under KOALIX's engines dir: engines\aether\aether.exe (+ pt\ helpers).</summary>
    public string EngineDir => Path.Combine(paths.EnginesDir, "aether");
    public string ExePath => Path.Combine(EngineDir, OperatingSystem.IsWindows() ? "aether.exe" : "aether");
    /// <summary>Pinned release tag (Aether-GUI pins the same in src-tauri/binaries/AETHER_VERSION).</summary>
    public const string PinnedVersion = "v2.1.0";

    /// <summary>Resolve order: manual override (setting "engine.aether.path") → KOALIX's engines\aether\ → aether.exe
    /// beside KOALIX.exe → an Aether-GUI install's bundled binaries folder. Null when nothing exists.</summary>
    public string? FindExe(string? overridePath = null)
    {
        var candidates = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(overridePath)) candidates.Add(overridePath.Trim());
        candidates.Add(ExePath);
        // Portable layout: the app's own folder (like Aether-GUI's current_exe/binaries).
        if (Process.GetCurrentProcess().MainModule?.FileName is { } selfExe)
        {
            var appDir = Path.GetDirectoryName(selfExe)!;
            candidates.Add(Path.Combine(appDir, "binaries", "aether.exe"));
            candidates.Add(Path.Combine(appDir, "aether.exe"));
        }
        // Aether-GUI's resource dir when installed (Tauri puts the core under <install>\binaries).
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(la, "Aether-GUI", "binaries", "aether.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public string? InstalledVersion() =>
        File.ReadAllText(Path.Combine(EngineDir, "VERSION")).Trim() is { Length: > 0 } v && File.Exists(ExePath) ? v : null;

    /// <summary>Downloads the pinned Aether release into engines\aether\ and verifies it end to end.</summary>
    public async Task<(bool Ok, string? Error)> InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        const string asset = "aether-windows-x86_64.zip";
        var baseUrl = $"https://github.com/CluvexStudio/Aether/releases/download/{PinnedVersion}";
        var tmp = Path.Combine(Path.GetTempPath(), "koalix-aether-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (!OperatingSystem.IsWindows()) return (false, "Aether.Error.Platform");
            // SHA256SUMS.txt is fetched and matched by exact filename — never a substring (the release also has
            // per-asset .sha256 files that would otherwise sneak in), exactly like Aether-GUI's fetch script.
            var sums = await http.GetStringAsync($"{baseUrl}/SHA256SUMS.txt", ct);
            string? expected = null;
            foreach (var line in sums.Split('\n'))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (f.Length >= 2 && (f[1] == asset || f[1] == "*" + asset)) { expected = f[0].ToLowerInvariant(); break; }
            }
            if (expected is null) return (false, "Aether.Error.NoChecksum");

            using (var res = await http.GetAsync($"{baseUrl}/{asset}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct); total += n;
                    if (res.Content.Headers.ContentLength is long len and > 0) progress?.Report((double)total / len);
                    if (total > 300_000_000) throw new InvalidDataException("download too large");
                }
            }
            await using (var f = File.OpenRead(tmp))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return (false, "Aether.Error.ChecksumMismatch");

            var staging = EngineDir + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(tmp, staging);
            var exe = Directory.GetFiles(staging, "aether.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null) return (false, "Aether.Error.ExeMissing");
            var exeDir = Path.GetDirectoryName(exe)!; // keep aether.exe AND its pt\ helpers together
            if (Directory.Exists(EngineDir)) Directory.Delete(EngineDir, true);
            Directory.Move(exeDir, EngineDir);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await File.WriteAllTextAsync(Path.Combine(EngineDir, "VERSION"), PinnedVersion, ct);
            return (true, null);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
