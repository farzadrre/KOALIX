using Koalix.Core;

namespace Koalix.Tunneling;

public sealed record ConnectionOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxReconnectAttempts { get; init; } = 5;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public bool AutoReconnect { get; init; } = true;
    public bool KillSwitch { get; init; }
    /// <summary>v0.4.28 — 900 ms (was 600): probes are HTTPS now (same layer as the real traffic), so a healthy\n    /// link's measured latency includes one TLS handshake; 600 called healthy links Degraded and armed the\n    /// auto-failover against them.</summary>
    public int DegradedLatencyMs { get; init; } = 900;
}

public sealed record ActiveConnection(TunnelProfile Profile, SplitPolicy Policy, ITunnelEngine Engine, DateTimeOffset Since);

/// <summary>
/// Central owner of the routing layer. Guarantees a single active engine, never reports Connected before the engine
/// is running AND a connectivity probe through the tunnel succeeded, and performs ordered teardown.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<EngineKind, Func<ITunnelEngine>> _factories;
    private readonly INetworkConflictDetector _conflicts;
    private readonly IKillSwitch _killSwitch;
    private readonly HealthChecker _health;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _monitorCts;
    /// <summary>1 while a reconnect loop is in flight (v0.4.17) — an engine exit during a running recovery is ignored
    /// instead of spawning a SECOND loop. The v0.4.16 field bug: TryTransition(Reconnecting) succeeds as a no-op
    /// self-transition, so every duplicated exit event started its own loop — loops multiplied exponentially
    /// (5,518 "Reconnect attempt" lines in one user log) and KOALIX flapped connect/disconnect endlessly.</summary>
    private int _reconnectFlight;
    /// <summary>Bumped on every teardown/connect (v0.4.17): reconnect loops capture the value at entry and abort as
    /// soon as it changes, so a recovery can never resurrect an engine the user already switched away from or stopped.</summary>
    private int _generation;
    /// <summary>v0.4.27 — the per-connection health plan: worker-fronted profiles probe Cloudflare-free endpoints
    /// (cp.cloudflare.com is unreachable from inside a Worker — see HealthChecker.WorkerProbeUrls) and are judged
    /// with a topology-fair Degraded threshold instead of the flat 600 ms default.</summary>
    private IReadOnlyList<Uri>? _probeUrls;
    private int _degradedLatencyMs = 600;
    /// <summary>v0.4.29 — set by the UnexpectedExit handler while a connect's health wait is in flight: an engine
    /// that dies right after start must end the wait IMMEDIATELY. The field case (log 353): sing-box FATALed on a
    /// bad config while xray.exe kept running, so IsRunning stayed true and the health loop burned its full
    /// ConnectTimeout probing a corpse before reporting the error.</summary>
    private int _engineDiedDuringConnect;

    public ConnectionStateMachine StateMachine { get; } = new();
    public ActiveConnection? Active { get; private set; }
    public ConnectionOptions Options { get; set; } = new();
    public double? LastLatencyMs { get; private set; }

    /// <summary>Live latency from mode-specific probes (e.g. the WireGuard endpoint ping, v0.4.11) for tunnels without a local health port.</summary>
    public void UpdateLatency(double? ms) => LastLatencyMs = ms;
    public event Action<LogCategory, string>? Log;
    public event Action<IReadOnlyList<NetworkConflict>>? ConflictsDetected;

    public ConnectionManager(IReadOnlyDictionary<EngineKind, Func<ITunnelEngine>> factories, INetworkConflictDetector conflicts, IKillSwitch killSwitch, HealthChecker health)
    {
        _factories = factories; _conflicts = conflicts; _killSwitch = killSwitch; _health = health;
        // v0.4.26 — a throwing state listener is LOGGED, never fatal: the tunnel state is already committed
        // and the connect flow must not even notice (see ConnectionStateMachine.SubscriberException).
        StateMachine.SubscriberException += ex =>
            Log?.Invoke(LogCategory.Application, "State listener error (ignored, tunnel unaffected): " + ex.Message);
    }

    public async Task<bool> ConnectAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Active is not null)
            {
                StateMachine.TryTransition(ConnectionState.Disconnecting, "switching profile");
                await TeardownAsync("switching profile", ct);
                StateMachine.TryTransition(ConnectionState.Disconnected);
            }
            StateMachine.Transition(ConnectionState.Connecting, profile.Name);

            var policyErrors = policy.Validate();
            if (policyErrors.Count > 0) return Fail(string.Join("; ", policyErrors));

            var engine = _factories[profile.Engine]();
            if (policy.Mode != SplitMode.FullTunnel && !engine.SupportsProcessRouting(profile))
                return Fail("Split.Error.EngineNoProcessRouting"); // never silently fall back to full tunnel

            var conflicts = await _conflicts.DetectAsync([], ct);
            if (conflicts.Count > 0) ConflictsDetected?.Invoke(conflicts);
            var blocking = conflicts.Where(c => c.Blocking).ToList();
            if (blocking.Count > 0) return Fail(string.Join("\n", blocking.Select(b => b.Code + ":" + b.Detail)));

            var v = await engine.ValidateAsync(profile, policy, ct);
            if (!v.Ok) { await engine.DisposeAsync(); return Fail(string.Join("\n", v.Errors)); }

            engine.LogLine += (c, l) => Log?.Invoke(c, l);
            engine.UnexpectedExit += code =>
            {
                Volatile.Write(ref _engineDiedDuringConnect, 1); // v0.4.29 — fail the health wait fast (see field note)
                _ = OnEngineExitAsync(code);
            };
            Volatile.Write(ref _engineDiedDuringConnect, 0);
            await engine.StartAsync(profile, policy, ct);
            // v0.4.30 — the TUN is up: drop every DNS entry cached while the tunnel was down. On ISPs that
            // poison plain DNS (Iran: blocked domains answered with the 10.10.34.x sinkhole, TTL up to 300 s)
            // those stale entries otherwise keep sending browsers to dead destinations for minutes AFTER the
            // tunnel is already healthy — the exact «وصل میشه، آی‌پی خارجی می‌زنه، ولی هیچ سایت خارجی باز نمی‌شه؛
            // بعد یهو درست میشه» lottery the field log pinned. Without this flush the poison outlives the connect.
            FlushDnsCache();
            Active = new ActiveConnection(profile, policy, engine, DateTimeOffset.Now);
            Interlocked.Increment(ref _generation); // any stale reconnect loop from the previous connection dies here
            // v0.4.27 — worker-fronted profiles must not be probed against Cloudflare (their exit lives inside
            // Cloudflare and can never reach it) nor be called Degraded for normal worker latencies.
            var plan = HealthChecker.ProbePlan(profile, Options.DegradedLatencyMs);
            _probeUrls = plan.WorkerFronted ? plan.Probes : null;
            _degradedLatencyMs = plan.DegradedMs;
            if (plan.WorkerFronted)
                Log?.Invoke(LogCategory.Application, $"Worker-fronted endpoint ({plan.Token}): cp.cloudflare.com probes are unreachable from inside Cloudflare — probing Google instead; Degraded threshold {plan.DegradedMs} ms");

            if (!await WaitHealthyAsync(engine, Options.ConnectTimeout, ct))
            {
                // v0.4.20: when the engine named its fatal reason (OpenVPN AUTH_FAILED — volume/expiry exhausted),
                // that reason replaces the generic health-check wording.
                var reason = engine is IReportedFatalError { FatalErrorKey: { } fatal } ? fatal : "Connect.Error.HealthCheck";
                await TeardownAsync("health check failed", CancellationToken.None);
                StateMachine.TryTransition(ConnectionState.Error, reason);
                return false;
            }
            if (_killSwitch.IsActive) await _killSwitch.DeactivateAsync(ct);
            StateMachine.Transition(LastLatencyMs > _degradedLatencyMs ? ConnectionState.Degraded : ConnectionState.Connected);
            StartMonitor();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke(LogCategory.Application, "Connect failed: " + Redactor.Redact(ex.Message));
            if (Active is not null) await TeardownAsync("error", CancellationToken.None);
            StateMachine.TryTransition(ConnectionState.Error, ex.Message);
            return false;
        }
        finally { _gate.Release(); }

        bool Fail(string reason) { StateMachine.Transition(ConnectionState.Error, reason); Log?.Invoke(LogCategory.Application, reason); return false; }
    }

    private async Task<bool> WaitHealthyAsync(ITunnelEngine engine, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        // v0.4.17 — engines that report their own connection state (OpenVPN management >STATE:): the reported state
        // is the ONLY truth. The old code fell back to a DIRECT connectivity probe, which answers from the
        // UNTUNNELED internet — it succeeded while openvpn.exe had already died of a bad command line, KOALIX
        // reported "connected", and the monitor then restarted the corpse every 20 s: the rapid flapping the
        // user saw. Now: wait for CONNECTED (bounded by the connect timeout); a dead process fails immediately.
        if (engine is IProcessHealthEngine { ProcessHealthOnly: true, HasConnectionStateSignal: true } signal)
        {
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!engine.IsRunning) return false;
                if (signal.ReportedConnected == true) { LastLatencyMs = null; return true; }
                await Task.Delay(400, ct);
            }
            return false;
        }
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!engine.IsRunning) return false;
            if (Volatile.Read(ref _engineDiedDuringConnect) == 1)
            {
                // v0.4.29 — the engine (or its front-end) already raised UnexpectedExit; probing the corpse only
                // burns the connect timeout. Fail now — the FATAL lines are already in the log.
                Log?.Invoke(LogCategory.Application, "Health wait aborted: engine died right after start");
                return false;
            }
            // First probes get more time: a WireGuard handshake + DNS over a congested link can exceed 5 s.
            var probeTimeout = engine is IProcessHealthEngine { ProcessHealthOnly: true }
                ? TimeSpan.FromSeconds(4)
                : TimeSpan.FromSeconds(Math.Clamp((deadline - DateTime.UtcNow).TotalSeconds, 3, 10));
            var r = engine.HealthProxyPort is { } port
                ? await _health.CheckThroughProxyAsync(port, probeTimeout, ct, _probeUrls)
                : await _health.CheckDirectAsync(probeTimeout, ct, _probeUrls); // full-tunnel WireGuard: all traffic is tunneled
            if (r.Ok) { LastLatencyMs = r.LatencyMs; return true; }
            Log?.Invoke(LogCategory.Application, "Health probe: " + r.Error);
            if (engine is IProcessHealthEngine { ProcessHealthOnly: true })
            {
                // Give the external client a moment, then accept it as long as it is alive.
                await Task.Delay(1500, ct);
                if (engine.IsRunning) { Log?.Invoke(LogCategory.Application, "Health: process-based (probe failed, engine running)"); LastLatencyMs = null; return true; }
                return false;
            }
            await Task.Delay(700, ct);
        }
        return false;
    }

    private void StartMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Options.HealthInterval);
            var failures = 0;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var a = Active; if (a is null) return;
                // v0.4.17: process-health engines are monitored by their OWN signals, not by a direct internet probe
                // (which answers from the untunneled network and would report "connected" while the tunnel is dead).
                if (a.Engine is IProcessHealthEngine phe)
                {
                    if (!a.Engine.IsRunning) { _ = OnEngineExitAsync(-3); return; }
                    if (phe.HasConnectionStateSignal)
                    {
                        // OpenVPN: trust >STATE: — RECONNECTING/EXITING degrades the UI, CONNECTED restores it.
                        // OpenVPN retries internally; KOALIX never tears a live client down over a probe.
                        if (phe.ReportedConnected == false) StateMachine.TryTransition(ConnectionState.Degraded, "engine reports tunnel down/reconnecting");
                        else if (phe.ReportedConnected == true) StateMachine.TryTransition(ConnectionState.Connected);
                    }
                    else
                    {
                        var rp = await _health.CheckDirectAsync(TimeSpan.FromSeconds(6), ct, _probeUrls);
                        if (rp.Ok) { LastLatencyMs = rp.LatencyMs; StateMachine.TryTransition(rp.LatencyMs > _degradedLatencyMs ? ConnectionState.Degraded : ConnectionState.Connected); }
                    }
                    continue;
                }
                var r = a.Engine.HealthProxyPort is { } port ? await _health.CheckThroughProxyAsync(port, TimeSpan.FromSeconds(6), ct, _probeUrls) : await _health.CheckDirectAsync(TimeSpan.FromSeconds(6), ct, _probeUrls);
                if (r.Ok)
                {
                    failures = 0; LastLatencyMs = r.LatencyMs;
                    StateMachine.TryTransition(r.LatencyMs > _degradedLatencyMs ? ConnectionState.Degraded : ConnectionState.Connected);
                }
                else if (++failures >= 2) StateMachine.TryTransition(ConnectionState.Degraded, r.Error);
                if (failures >= 4) { _ = OnEngineExitAsync(-2); return; }
            }
        }, ct);
    }

    /// <summary>
    /// v0.4.17 — recovery is SINGLE-FLIGHT and generation-guarded. The v0.4.16 field disaster, from the user's log:
    /// openvpn.exe died instantly (bad command-line option) → its exit event fired → a reconnect loop started →
    /// every loop restart added ANOTHER exit handler on the same engine → one exit raised N events → TryTransition
    /// (Reconnecting) succeeds as a self-transition no-op, so every event entered its OWN loop → exponential loop
    /// multiplication (5,518 "Reconnect attempt" lines, hundreds of openvpn.exe starts per minute, log flooded
    //  with duplicated lines, UI flapping). Now: one loop at a time, aborts when the connection is replaced or
    /// torn down, and an engine that dies within seconds of starting (deterministic failure — bad options, bad
    /// binary) gets ONE retry instead of five, then an honest Error with the real cause in the log.
    /// </summary>
    private async Task OnEngineExitAsync(int code)
    {
        var a = Active;
        if (a is null) return;
        if (Interlocked.Exchange(ref _reconnectFlight, 1) == 1) return; // a recovery is already running — it owns this
        var generation = Volatile.Read(ref _generation);
        try
        {
            // v0.4.20: an engine that KNOWS why it died (OpenVPN AUTH_FAILED → volume/expiry exhausted) names the
            // localized reason — no reconnect loop can fix an exhausted account, so we skip straight to the error.
            if (a.Engine is IReportedFatalError { FatalErrorKey: { } fatal })
            {
                Log?.Invoke(LogCategory.Application, "Engine fatal: " + fatal);
                if (Options.KillSwitch && a.Policy.Mode != SplitMode.Exclude)
                    await _killSwitch.ActivateAsync(a.Policy.Rules.Where(r => r.Action == AppAction.Tunnel).ToList(), a.Policy.Mode == SplitMode.FullTunnel, CancellationToken.None);
                await TeardownAsync("fatal: " + fatal, CancellationToken.None);
                StateMachine.TryTransition(ConnectionState.Error, fatal);
                return;
            }
            var fastDeath = DateTimeOffset.Now - a.Since < TimeSpan.FromSeconds(15);
            Log?.Invoke(LogCategory.Application, $"Engine stopped unexpectedly (code {code})" +
                (fastDeath ? " — died right after start (bad config/binary? see the engine lines above)" : ""));
            if (Options.KillSwitch && a.Policy.Mode != SplitMode.Exclude)
                await _killSwitch.ActivateAsync(a.Policy.Rules.Where(r => r.Action == AppAction.Tunnel).ToList(), a.Policy.Mode == SplitMode.FullTunnel, CancellationToken.None);
            if (!Options.AutoReconnect) { await DisconnectAsync(CancellationToken.None); return; }
            if (!StateMachine.TryTransition(ConnectionState.Reconnecting, $"exit {code}")) return;
            // A death seconds after start is deterministic (options error, missing binary, refused port) — retrying
            // five times with backoff only produces five more corpses and five more log storms. One calm retry.
            var maxAttempts = fastDeath ? 1 : Options.MaxReconnectAttempts;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await Task.Delay(Options.RetryBaseDelay * Math.Pow(2, attempt - 1));
                if (Volatile.Read(ref _generation) != generation)
                { Log?.Invoke(LogCategory.Application, "Reconnect aborted — connection replaced or closed"); return; }
                Log?.Invoke(LogCategory.Application, $"Reconnect attempt {attempt}/{maxAttempts}");
                await _gate.WaitAsync();
                try
                {
                    if (Volatile.Read(ref _generation) != generation || Active is null || !ReferenceEquals(Active.Engine, a.Engine))
                    { Log?.Invoke(LogCategory.Application, "Reconnect aborted — connection replaced or closed"); return; }
                    var engine = a.Engine;
                    try { await engine.StopAsync(CancellationToken.None); } catch { }
                    Volatile.Write(ref _engineDiedDuringConnect, 0); // v0.4.29 — the death that TRIGGERED this loop must not poison the retry
                    await engine.StartAsync(a.Profile, a.Policy, CancellationToken.None);
                    if (await WaitHealthyAsync(engine, Options.ConnectTimeout, CancellationToken.None))
                    {
                        if (_killSwitch.IsActive) await _killSwitch.DeactivateAsync(CancellationToken.None);
                        StateMachine.TryTransition(ConnectionState.Connected);
                        StartMonitor();
                        return;
                    }
                }
                catch (Exception ex) { Log?.Invoke(LogCategory.Application, "Reconnect failed: " + Redactor.Redact(ex.Message)); }
                finally { _gate.Release(); }
            }
            StateMachine.TryTransition(ConnectionState.Error, "Connect.Error.ReconnectExhausted");
        }
        finally { Interlocked.Exchange(ref _reconnectFlight, 0); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Active is null) { StateMachine.TryTransition(ConnectionState.Disconnected); return; }
            StateMachine.TryTransition(ConnectionState.Disconnecting);
            await TeardownAsync("user", ct);
            if (_killSwitch.IsActive) await _killSwitch.DeactivateAsync(ct);
            StateMachine.TryTransition(ConnectionState.Disconnected);
        }
        finally { _gate.Release(); }
    }

    private async Task TeardownAsync(string reason, CancellationToken ct)
    {
        _monitorCts?.Cancel();
        Interlocked.Increment(ref _generation); // v0.4.17: any in-flight reconnect loop for this connection dies here
        _probeUrls = null; // v0.4.27: the per-connection probe plan dies with the connection
        _degradedLatencyMs = Options.DegradedLatencyMs;
        var a = Active; Active = null;
        if (a is null) return;
        Log?.Invoke(LogCategory.Application, $"Stopping {a.Engine.Kind} ({reason})");
        try { await a.Engine.StopAsync(ct); } finally { await a.Engine.DisposeAsync(); }
        // v0.4.30 — a clean cache for the direct world too: entries learned under the tunnel topology must not
        // outlive it (and a broken session's poison must not survive into the next direct session).
        FlushDnsCache();
    }

    /// <summary>
    /// v0.4.30 — best-effort <c>ipconfig /flushdns</c> (bounded to 3 s; failures are logged, never thrown):
    /// clears the OS resolver cache so entries learned before/under a different network topology cannot
    /// outlive it. Called the moment the tunnel is up (the first re-queries then resolve through the hijacked,
    /// poison-proof path instead of a stale sinkhole entry) and right after teardown. Chrome's in-process
    /// cache is untouched by design — it honors each record's TTL on its own.
    /// </summary>
    private void FlushDnsCache()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ipconfig.exe", Arguments = "/flushdns",
                CreateNoWindow = true, UseShellExecute = false,
            });
            if (p is null) return;
            if (p.WaitForExit(3000)) Log?.Invoke(LogCategory.Application, "DNS cache flushed (ipconfig /flushdns)");
        }
        catch (Exception ex)
        {
            Log?.Invoke(LogCategory.Application, "DNS flush skipped (ignored): " + Redactor.Redact(ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(CancellationToken.None); } catch { }
        _gate.Dispose();
    }
}
