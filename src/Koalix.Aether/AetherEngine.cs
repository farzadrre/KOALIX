using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.Aether;

/// <summary>Lifecycle states of the Aether tunnel (Aether-GUI's ConnectionState).</summary>
public enum AetherState
{
    /// <summary>Not running.</summary>
    Idle,
    /// <summary>Process spawned, waiting for its banner/first output.</summary>
    Launching,
    /// <summary>Prompts answered / flags taken, Aether is scanning for a route.</summary>
    Connecting,
    /// <summary>SOCKS5 port (and overlay port in chain modes) answers — traffic flows.</summary>
    Connected,
    /// <summary>Unexpected drop; auto-retry attempt N of 3 is starting after backoff.</summary>
    Reconnecting,
    /// <summary>Gave up (retries exhausted / unrecoverable launch failure).</summary>
    Failed,
    /// <summary>User asked to stop; waiting for the process to exit before Idle.</summary>
    Disconnecting,
}

/// <summary>
/// v0.4.48 — drives the real `aether` binary (github.com/CluvexStudio/Aether) the way Aether-GUI does (its
/// mod.rs/pty.rs): spawn with the whole profile as flags, watch stdout for the banner and Tor/Psiphon readiness,
/// poll the local SOCKS5 port as the ground-truth "connected" signal, auto-retry unexpected drops with backoff
/// (never a user-requested disconnect), kill the whole tree on stop. KOALIX additions over the stock GUI: the
/// process is bound to a Windows Job Object (KILL_ON_JOB_CLOSE — it can never outlive a crashed KOALIX), every
/// line is redacted through the app's Redactor, and "AETHER chain mode" lets the sing-box TUN use the tunnel
/// without any external app.
/// </summary>
public sealed class AetherEngine : IAsyncDisposable
{
    /// <summary>Auto-retry policy for unexpected drops/timeouts — Aether-GUI's MAX_AUTO_RETRIES/RETRY_BACKOFF.</summary>
    public const int MaxAutoRetries = 3;
    private static readonly TimeSpan[] RetryBackoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    /// <summary>How long to wait after Ctrl-C-style stop before force-killing the tree (Aether-GUI: 3s).</summary>
    private static readonly TimeSpan GracefulGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MonitorTick = TimeSpan.FromMilliseconds(400);

    private readonly EngineProcess _proc = new(LogCategory.Application);
    private readonly string _workDir;
    private readonly object _gate = new();
    private CancellationTokenSource? _monitorCts;
    private AetherProfile _profile = new();
    private string? _exePath;
    private bool _userStop;
    private int _retry;
    private AetherState _state = AetherState.Idle;
    private string _failReason = "";
    private bool _engineStarted, _overlayReady;

    public AetherEngine(EnginePaths paths)
    {
        _workDir = Path.Combine(paths.RuntimeDir, "aether");
        Directory.CreateDirectory(_workDir);
        _proc.Output += (c, l) => OnEngineLine(l);
        _proc.UnexpectedExit += _ => OnProcessExit("process exited unexpectedly");
    }

    /// <summary>Aether's stdout lines (already redacted) + the engine's own state lines.</summary>
    public event Action<string>? Log;
    /// <summary>Fires on every state change.</summary>
    public event Action<AetherState, string>? StateChanged;
    public AetherState State { get { lock (_gate) return _state; } }
    public AetherProfile Profile { get { lock (_gate) return _profile; } }
    /// <summary>The SOCKS5 address apps should point at while connected (the Tor/Psiphon proxy in chain modes).</summary>
    public string PrimaryAddr => Profile.PrimaryAddr();
    public bool IsConnected => State == AetherState.Connected;

    private void SetState(AetherState s, string detail = "")
    {
        AetherState prev;
        lock (_gate) { prev = _state; _state = s; if (s == AetherState.Failed) _failReason = detail; }
        if (prev != s) { Log?.Invoke($"[aether] state: {s}{(detail.Length > 0 ? " — " + detail : "")}"); StateChanged?.Invoke(s, detail); }
    }

    /// <summary>Starts a connect attempt. Returns as soon as the process is spawned; the Launching→Connecting→
    /// Connected transitions happen on the monitor task. Throws with an app-localizable code on precondition failures.</summary>
    public async Task StartAsync(string exePath, AetherProfile profile, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_state is not (AetherState.Idle or AetherState.Failed))
                throw new InvalidOperationException("Aether.Error.Running");
        }
        if (!File.Exists(exePath)) throw new InvalidOperationException("Aether.Error.Missing");
        _exePath = exePath;
        _profile = profile;
        _userStop = false;
        _retry = 0;
        _engineStarted = _overlayReady = false;

        // Ports that must be free before launching (a stale aether from a crashed run would hold them).
        foreach (var addr in profile.ListenAddrs())
            if (PortIsLive(addr))
                throw new InvalidOperationException($"Aether.Error.PortInUse:{addr.Port}");
        foreach (var addr in profile.ListenAddrs())
            if (!System.Net.IPEndPoint.TryParse(addr.ToString(), out _)) { } // parsed by construction

        SetState(AetherState.Launching);
        await SpawnAsync(ct);
    }

    private async Task SpawnAsync(CancellationToken ct)
    {
        var p = Profile;
        var env = p.EnvVars();
        var argv = new List<string>(p.AsArgs()) { "--bind", AetherProfile.ParseBind(p.BindAddress).ToString() };
        try
        {
            await Task.Run(() => _proc.Start(_exePath!, argv, _workDir), ct);
            foreach (var (k, v) in env) _proc.SetEnvironment(k, v);
        }
        catch (Exception ex)
        {
            SetState(AetherState.Failed, "spawn: " + ex.Message);
            throw new InvalidOperationException("Aether.Error.Spawn");
        }
        _engineStarted = _overlayReady = false;
        _monitorCts = new CancellationTokenSource();
        _ = MonitorAsync(_monitorCts.Token);
    }

    /// <summary>Parses one raw engine line for the banner / overlay signals, then forwards it (redacted by EngineProcess).</summary>
    private void OnEngineLine(string line)
    {
        if (line.Contains("Aether v")) { _engineStarted = true; }
        if (line.Contains("tor is ready;") || line.Contains("psiphon is ready;")) _overlayReady = true;
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Profile.ConnectTimeout();
        var ready = Profile.ReadyAddrs();
        var announcedConnecting = false;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(MonitorTick, ct);
            lock (_gate) if (_userStop) return;

            if (!_proc.IsRunning) { OnProcessExit("aether exited before connecting"); return; }

            if (!announcedConnecting && _engineStarted)
            { SetState(AetherState.Connecting); announcedConnecting = true; }

            // Tor/Psiphon open their port while still bootstrapping, so every mode that carries them (chain AND
            // reverse — both open the overlay proxy, Aether-GUI's ready_addrs) also waits for the ready line.
            var overlayOk = !Profile.IsTor && !Profile.IsPsiphon || _overlayReady;
            if (overlayOk && ready.All(PortIsLive))
            {
                SetState(AetherState.Connected, "socks " + Profile.PrimaryAddr());
                _retry = 0; // proven working — a future drop earns a fresh full retry budget
                return;
            }
            if (DateTime.UtcNow >= deadline)
            {
                await StopAsync();
                OnProcessExit("timed out waiting for a working route");
                return;
            }
        }
    }

    private void OnProcessExit(string reason)
    {
        int attempt;
        lock (_gate)
        {
            if (_userStop) return; // the stop path owns the transition to Idle
            _retry++;
            attempt = _retry;
            if (attempt > MaxAutoRetries)
            {
                SetState(AetherState.Failed, $"{reason} (gave up after {MaxAutoRetries} retries)");
                return;
            }
        }
        SetState(AetherState.Reconnecting, $"attempt {attempt} of {MaxAutoRetries} — {reason}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(RetryBackoff[Math.Min(attempt, RetryBackoff.Length) - 1]);
            lock (_gate) if (_userStop) return;
            _engineStarted = _overlayReady = false;
            SetState(AetherState.Launching);
            try { await SpawnAsync(CancellationToken.None); }
            catch (InvalidOperationException) { /* Failed state already set + logged */ }
        });
    }

    /// <summary>User-requested stop: never retried, always ends in Idle.</summary>
    public async Task StopAsync()
    {
        lock (_gate) _userStop = true;
        _monitorCts?.Cancel();
        SetState(AetherState.Disconnecting);
        await _proc.StopAsync(GracefulGrace);
        SetState(AetherState.Idle);
    }

    /// <summary>TCP connect probe — the ground-truth "is the tunnel up" signal (Aether-GUI's port_is_live).</summary>
    public static bool PortIsLive(System.Net.IPEndPoint ep)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            var task = c.ConnectAsync(ep.Address, ep.Port);
            return task.Wait(300) && c.Connected;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _monitorCts?.Cancel();
        await _proc.StopAsync(GracefulGrace);
        _monitorCts?.Dispose();
    }
}
