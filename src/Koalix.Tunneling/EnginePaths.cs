namespace Koalix.Tunneling;

/// <summary>Engine binaries live in their own versioned folder, separate from user configuration and runtime files.</summary>
public sealed class EnginePaths
{
    public EnginePaths(string root) { Root = root; Directory.CreateDirectory(RuntimeDir); }
    public string Root { get; }
    public string EnginesDir => Path.Combine(Root, "engines");
    public string RuntimeDir => Path.Combine(Root, "runtime");
    public string SingBoxExe => Path.Combine(EnginesDir, "sing-box", "sing-box.exe");
    public string XrayExe => Path.Combine(EnginesDir, "xray", "xray.exe");

    /// <summary>v0.4.47.1 — manual sing-box.exe override (setting "engine.singbox.path"). Empty = auto-detect.</summary>
    public string? SingBoxPathOverride { get; set; }

    /// <summary>
    /// v0.4.47.1 fix — the sing-box CLI used to be looked up ONLY in KOALIX's own engines folder, so a
    /// system-wide sing-box (PATH, winget, scoop, a hand-copied exe…) was invisible and the WARP identity
    /// button wrongly claimed "not installed". Resolution order: manual override → KOALIX-managed
    /// engines\sing-box\sing-box.exe → the system PATH → the usual install locations. Null when nothing exists.
    /// </summary>
    public string? FindSingBoxExe()
    {
        var candidates = new List<string>(8);
        if (!string.IsNullOrWhiteSpace(SingBoxPathOverride)) candidates.Add(SingBoxPathOverride!.Trim());
        candidates.Add(SingBoxExe);
        var exeName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(dir, exeName));
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.AddRange(new[]
        {
            Path.Combine(la, "Microsoft", "WinGet", "Links", exeName),
            Path.Combine(la, "Programs", "sing-box", exeName),
            Path.Combine(pf, "sing-box", exeName),
            Path.Combine(pf, "sing-box", "bin", exeName),
            Path.Combine(up, "scoop", "shims", exeName),
        });
        return candidates.FirstOrDefault(File.Exists);
    }
    public static string WireGuardExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");
    /// <summary>WireSock CLI (optional, user-installed). Null when not installed.</summary>
    public static string? WireSockExe
    {
        get
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] c =
            [
                Path.Combine(pf, "WireSock Secure Connect", "bin", "wiresock-client.exe"),
                Path.Combine(pf, "WireSock VPN Client", "bin", "wiresock-client.exe"),
                Path.Combine(pf, "WireSock Secure Connect", "wiresock-client.exe"),
                Path.Combine(pf, "WireSock VPN Client", "wiresock-client.exe"),
            ];
            return c.FirstOrDefault(File.Exists);
        }
    }
    public static string WgExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wg.exe");

    /// <summary>
    /// OpenVPN client (v0.4.15): the official OpenVPN for Windows install ships openvpn.exe in its bin folder.
    /// KOALIX also accepts a manually dropped binary under engines\openvpn\. Null when neither exists — the
    /// Settings page then offers the official download page instead of downloading an unverifiable binary.
    /// </summary>
    public static string? OpenVpnExe
    {
        get
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] c =
            [
                Path.Combine(pf, "OpenVPN", "bin", "openvpn.exe"),
                Path.Combine(pf86, "OpenVPN", "bin", "openvpn.exe"),
            ];
            return c.FirstOrDefault(File.Exists);
        }
    }

    public static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return port;
    }
}
