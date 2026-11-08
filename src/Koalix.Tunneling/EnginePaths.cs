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
