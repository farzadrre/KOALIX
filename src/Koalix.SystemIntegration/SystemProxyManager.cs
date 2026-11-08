using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Koalix.SystemIntegration;

public sealed record SystemProxyState(bool Enabled, string? Server, string? Bypass, string? AutoConfigUrl);

/// <summary>Reads, sets and restores the per-user WinINet system proxy. Restore is refused if the user changed it meanwhile.</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemProxyManager
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    [DllImport("wininet.dll", SetLastError = true)] private static extern bool InternetSetOption(IntPtr h, int option, IntPtr buf, int len);

    public SystemProxyState Read()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return new SystemProxyState((k?.GetValue("ProxyEnable") as int? ?? 0) == 1, k?.GetValue("ProxyServer") as string,
            k?.GetValue("ProxyOverride") as string, k?.GetValue("AutoConfigURL") as string);
    }

    public void Apply(SystemProxyState s)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue("ProxyEnable", s.Enabled ? 1 : 0, RegistryValueKind.DWord);
        if (s.Server is null) k.DeleteValue("ProxyServer", false); else k.SetValue("ProxyServer", s.Server);
        if (s.Bypass is null) k.DeleteValue("ProxyOverride", false); else k.SetValue("ProxyOverride", s.Bypass);
        if (s.AutoConfigUrl is null) k.DeleteValue("AutoConfigURL", false); else k.SetValue("AutoConfigURL", s.AutoConfigUrl);
        Refresh();
    }

    public void EnableLocal(int port) => Apply(new SystemProxyState(true, $"127.0.0.1:{port}", "localhost;127.*;10.*;172.16.*;192.168.*;<local>", null));

    /// <summary>Restores the snapshot only if the current value is still what KOALIX set (expected).</summary>
    public bool RestoreIfUnchanged(SystemProxyState snapshot, SystemProxyState expectedCurrent)
    {
        if (Read() != expectedCurrent) return false;
        Apply(snapshot);
        return true;
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39 /*SETTINGS_CHANGED*/, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /*REFRESH*/, IntPtr.Zero, 0);
    }
}
