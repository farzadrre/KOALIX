using Koalix.Core;

namespace Koalix.Tunneling;

/// <summary>OpenVpnConf (v0.4.15): a self-contained .ovpn file (referenced ca/cert/key/tls-auth files are inlined at import).</summary>
public enum ProfileSource { ShareLink, SingBoxJson, XrayJson, WireGuardConf, OpenVpnConf }

public sealed record TunnelProfile
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required EngineKind Engine { get; init; }
    public required ProfileSource Source { get; init; }
    /// <summary>Share link, JSON or .conf content. Stored encrypted at rest; never logged.</summary>
    public required string Content { get; init; }
    public long? SubscriptionId { get; init; }
    /// <summary>Free-form group label (v0.4.12): shown as a chip and filterable — "work", "gaming", "fast"…</summary>
    public string? Tag { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset? LastConnected { get; init; }
    public double? LastLatencyMs { get; init; }
    /// <summary>Manual position from drag-and-drop reordering (v0.4.19): 0 = never dragged (alphabetical tail), then 10, 20, 30 …</summary>
    public int SortOrder { get; init; }
}

public sealed record EngineValidation(bool Ok, IReadOnlyList<string> Errors)
{
    public static EngineValidation Success { get; } = new(true, []);
    public static EngineValidation Fail(params string[] e) => new(false, e);
}

/// <summary>
/// Engines whose traffic KOALIX cannot probe reliably (e.g. WireSock: no local proxy port, per-app capture by an
/// external driver). They are considered healthy while their process runs; probe failures only mark the link degraded.
/// v0.4.17 adds two OPTIONAL members (default interface implementations, so WireGuard/WireSock are untouched):
/// an engine that can report its own connection state (OpenVPN via the management &gt;STATE: channel) gets
/// state-based health instead — a direct internet probe answers from the UNTUNNELED network and would happily
/// report "connected" while the tunnel process is already dead (the v0.4.16 flapping bug).
/// </summary>
public interface IProcessHealthEngine
{
    bool ProcessHealthOnly { get; }

    /// <summary>True when the engine reports its real connection state (OpenVPN management interface, v0.4.17).</summary>
    bool HasConnectionStateSignal => false;

    /// <summary>Engine-reported tunnel state: true = CONNECTED, false = down/reconnecting, null = nothing reported yet.</summary>
    bool? ReportedConnected => null;
}

public interface ITunnelEngine : IAsyncDisposable
{
    EngineKind Kind { get; }
    /// <summary>True when the engine can enforce per-application routing with the given profile.</summary>
    bool SupportsProcessRouting(TunnelProfile profile);
    bool IsRunning { get; }
    IReadOnlyList<int> ProcessIds { get; }
    /// <summary>Local HTTP/SOCKS port used only by KOALIX for health checks through the tunnel (bound to 127.0.0.1).</summary>
    int? HealthProxyPort { get; }
    /// <summary>Clash-compatible API endpoint for traffic/connection statistics, when available.</summary>
    (Uri Url, string Secret)? StatsApi { get; }

    Task<EngineValidation> ValidateAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct);
    Task StartAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    event Action<LogCategory, string>? LogLine;
    /// <summary>Raised when the engine exits without StopAsync being called.</summary>
    event Action<int>? UnexpectedExit;
}
