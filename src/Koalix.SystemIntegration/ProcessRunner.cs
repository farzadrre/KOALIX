using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Koalix.Core;

namespace Koalix.SystemIntegration;

/// <summary>
/// Runs external tools with an argument list (never a shell string). Elevated execution is only allowed
/// for an allow-list of system tools, and every argument is validated against a strict character set.
/// </summary>
public sealed partial class ProcessRunner : IProcessRunner
{
    private static readonly HashSet<string> ElevatedAllowList = new(StringComparer.OrdinalIgnoreCase)
    { "netsh.exe", "ipconfig.exe", "arp.exe", "route.exe", "wireguard.exe", "sc.exe", "icacls.exe", "powershell.exe" };

    [GeneratedRegex(@"^[\p{L}\p{N} _\-\.:=/\\,@{}()\[\]+%#'""]*$")]
    private static partial Regex SafeArg();

    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, bool elevated = false, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        foreach (var a in arguments)
            if (a.Contains('\n') || a.Contains('\r') || a.Contains('\0') || !SafeArg().IsMatch(a))
                throw new ArgumentException("Rejected unsafe argument.");

        timeout ??= TimeSpan.FromSeconds(60);
        if (elevated && !IsElevated)
            return await RunElevatedAsync(fileName, arguments, timeout.Value, ct);

        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + fileName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout.Value);
        var so = p.StandardOutput.ReadToEndAsync(cts.Token);
        var se = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return new ProcessResult(-1, "", "timeout"); }
        return new ProcessResult(p.ExitCode, await so, await se);
    }

    /// <summary>UAC elevation through ShellExecute "runas". Output cannot be captured, only the exit code.</summary>
    private static async Task<ProcessResult> RunElevatedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (!ElevatedAllowList.Contains(Path.GetFileName(fileName)))
            throw new UnauthorizedAccessException("Executable is not allowed to run elevated: " + fileName);
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await p.WaitForExitAsync(cts.Token);
            return new ProcessResult(p.ExitCode, "", "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ProcessResult(1223, "", "UAC cancelled by user");
        }
    }

    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>Restarts the current application elevated (needed for TUN / Wintun).</summary>
    public static bool RestartElevated(string? extraArg = null)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        if (extraArg is not null) psi.ArgumentList.Add(extraArg);
        try { Process.Start(psi); return true; } catch { return false; }
    }
}
