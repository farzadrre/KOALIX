using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;
using Koalix.Tunneling;
using Serilog.Events;

namespace Koalix.App.Services;

/// <summary>
/// v0.4.36 — owns the optional LAN proxy relay (<see cref="LanProxyShare"/>): settings (lan.share / lan.port /
/// lan.pass — the password DPAPI-protected), the Windows Firewall inbound rule (admin only; without admin
/// Windows asks once itself), and the live status line the Gaming page shows.
/// </summary>
public sealed class LanShareService(SettingsRepository settings, ISecretProtector secrets, ConnectionManager cm, IProcessRunner runner)
{
    public const int DefaultPort = 10809;
    private const string RuleName = "KOALIX LAN proxy share";
    private LanProxyShare? _share;

    public bool Enabled => settings.Get("lan.share") == "1";
    public int Port => int.TryParse(settings.Get("lan.port"), out var p) && p is > 1024 and < 65536 ? p : DefaultPort;
    public string Password
    {
        get
        {
            var raw = settings.Get("lan.pass");
            if (string.IsNullOrEmpty(raw)) return "";
            try { return secrets.Unprotect(raw); } catch { return ""; }
        }
    }

    public bool IsRunning => _share?.IsRunning == true;
    public int ActiveClients => _share?.ActiveClients ?? 0;
    public int DevicesSeen => _share?.SeenDevices.Count ?? 0;
    public string? LastError { get; private set; }
    public event Action? Changed;

    public async Task SaveAsync(bool enabled, int port, string password)
    {
        settings.Set("lan.share", enabled ? "1" : "0");
        settings.Set("lan.port", port.ToString());
        settings.Set("lan.pass", string.IsNullOrEmpty(password) ? "" : secrets.Protect(password));
        await ApplyAsync();
    }

    /// <summary>(Re)starts or stops the relay to match the saved settings.</summary>
    public async Task ApplyAsync()
    {
        if (_share is not null) { await _share.DisposeAsync(); _share = null; }
        LastError = null;
        if (Enabled)
        {
            var s = new LanProxyShare(Port, Password, () => cm.Active?.Engine.HealthProxyPort);
            s.Log += m => AppHost.Log(LogCategory.Application, m);
            try
            {
                s.Start();
                _share = s;
                await EnsureFirewallAsync(Port);
            }
            catch (Exception ex)
            {
                LastError = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse } ? "port-in-use" : ex.Message;
                AppHost.Log(LogCategory.Application, "LAN share: could not start — " + ex.Message, LogEventLevel.Warning);
            }
        }
        else await RemoveFirewallAsync();
        Changed?.Invoke();
    }

    private async Task EnsureFirewallAsync(int port)
    {
        if (!ProcessRunner.IsElevated) return; // Windows shows its own "allow access" prompt once instead
        try
        {
            await RemoveFirewallAsync();
            await runner.RunAsync("netsh", ["advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=in", "action=allow",
                "protocol=TCP", $"localport={port}", "remoteip=LocalSubnet,192.168.137.0/24", "profile=any"], timeout: TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { AppHost.Log(LogCategory.Application, "LAN share firewall rule: " + ex.Message, LogEventLevel.Warning); }
    }

    private async Task RemoveFirewallAsync()
    {
        if (!ProcessRunner.IsElevated) return;
        try { await runner.RunAsync("netsh", ["advfirewall", "firewall", "delete", "rule", $"name={RuleName}"], timeout: TimeSpan.FromSeconds(15)); }
        catch { }
    }

    public async Task StopAsync()
    {
        if (_share is not null) { await _share.DisposeAsync(); _share = null; }
    }
}
