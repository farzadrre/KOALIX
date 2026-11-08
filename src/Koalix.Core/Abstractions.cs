namespace Koalix.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Safe process execution. Arguments are always passed as a list (never concatenated into a shell string),
/// which prevents command injection.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, bool elevated = false, TimeSpan? timeout = null, CancellationToken ct = default);
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

public sealed record TrafficSample(long UpBytesPerSec, long DownBytesPerSec, long TotalUp, long TotalDown, DateTimeOffset At);

public sealed record EngineResourceUsage(int ProcessId, double CpuPercent, long WorkingSetBytes);

public enum LogCategory { Application, Dns, SingBox, Xray, WireGuard, OpenVpn, Routing, WindowsNetwork, Repair, Gaming }

public sealed record NetworkConflict(string Code, string Detail, bool Blocking);

/// <summary>Detects other VPNs, TUN adapters, system proxies and port conflicts before a tunnel starts.</summary>
public interface INetworkConflictDetector
{
    Task<IReadOnlyList<NetworkConflict>> DetectAsync(IReadOnlyCollection<int> requiredLocalPorts, CancellationToken ct);
}

/// <summary>Blocks traffic of tunneled applications when the tunnel drops unexpectedly (configurable, reversible).</summary>
public interface IKillSwitch
{
    bool IsActive { get; }
    Task ActivateAsync(IReadOnlyList<AppRule> tunneledApps, bool blockAll, CancellationToken ct);
    Task DeactivateAsync(CancellationToken ct);
}
