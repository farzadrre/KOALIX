using System.Diagnostics;
using System.Runtime.InteropServices;
using Koalix.Core;

namespace Koalix.Tunneling;

/// <summary>
/// Hosts one engine executable (sing-box / xray): redirects output, assigns it to a Job Object with KILL_ON_JOB_CLOSE
/// (so engines never outlive a crashed GUI), and performs graceful Ctrl+C shutdown with a forced fallback.
/// </summary>
public sealed class EngineProcess : IAsyncDisposable
{
    private Process? _process;
    private nint _job;
    private bool _stopping;
    private readonly LogCategory _category;

    public EngineProcess(LogCategory category) => _category = category;

    public event Action<LogCategory, string>? Output;
    /// <summary>v0.4.18 — the SAME lines as <see cref="Output"/>, but BEFORE redaction. Only consumers that must
    /// read structured values out of engine output (OpenVPN pushed DNS addresses) subscribe; they never log the
    /// raw text, so the redaction guarantee for the log file stays intact.</summary>
    public event Action<string>? RawLine;
    public event Action<int>? UnexpectedExit;
    public int? Pid => _process is { HasExited: false } p ? p.Id : null;
    public bool IsRunning => _process is { HasExited: false };

    public void Start(string exe, IReadOnlyList<string> args, string workingDir)
    {
        if (IsRunning) throw new InvalidOperationException("Engine already running");
        if (!File.Exists(exe)) throw new FileNotFoundException("Engine binary not found", exe);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workingDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) { RawLine?.Invoke(d); Output?.Invoke(_category, Redactor.Redact(d)); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) { RawLine?.Invoke(d); Output?.Invoke(_category, Redactor.Redact(d)); } };
        p.Exited += (_, _) => { if (!_stopping) UnexpectedExit?.Invoke(SafeExitCode(p)); };
        _stopping = false;
        p.Start();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        _process = p;
        if (OperatingSystem.IsWindows()) AssignToKillOnCloseJob(p);
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    public async Task StopAsync(TimeSpan grace)
    {
        var p = _process;
        if (p is null || p.HasExited) return;
        _stopping = true;
        if (OperatingSystem.IsWindows()) SendCtrlC(p.Id);
        using var cts = new CancellationTokenSource(grace);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync();
        }
    }

    public static async Task<ProcessResult> RunToEndAsync(string exe, IReadOnlyList<string> args, string workingDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workingDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var so = p.StandardOutput.ReadToEndAsync(cts.Token);
        var se = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return new ProcessResult(p.ExitCode, Redactor.Redact(await so), Redactor.Redact(await se));
    }

    public EngineResourceUsage? SampleUsage(ref TimeSpan lastCpu, ref DateTime lastAt)
    {
        var p = _process;
        if (p is null || p.HasExited) return null;
        p.Refresh();
        var now = DateTime.UtcNow; var cpu = p.TotalProcessorTime;
        var pct = lastAt == default ? 0 : (cpu - lastCpu).TotalMilliseconds / ((now - lastAt).TotalMilliseconds * Environment.ProcessorCount) * 100;
        lastCpu = cpu; lastAt = now;
        return new EngineResourceUsage(p.Id, Math.Max(0, pct), p.WorkingSet64);
    }

    // ---------------- Win32 ----------------
    private void AssignToKillOnCloseJob(Process p)
    {
        _job = CreateJobObject(0, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 } };
        var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(_job, 9, ptr, (uint)len);
            AssignProcessToJobObject(_job, p.Handle);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static readonly object ConsoleGate = new();
    /// <summary>Delivers CTRL_C to a console child so the engine can remove its routes and TUN adapter cleanly.</summary>
    private static void SendCtrlC(int pid)
    {
        lock (ConsoleGate)
        {
            if (!AttachConsole((uint)pid)) return;
            SetConsoleCtrlHandler(0, true);
            try { GenerateConsoleCtrlEvent(0, 0); Thread.Sleep(100); }
            finally { FreeConsole(); SetConsoleCtrlHandler(0, false); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5));
        _process?.Dispose();
        if (_job != 0) { CloseHandle(_job); _job = 0; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint a, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int cls, nint info, uint len);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(nint handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
}
