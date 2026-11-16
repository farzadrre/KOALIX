using System.IO;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;
using Koalix.Tunneling;

namespace Koalix.Aether;

/// <summary>
/// v0.4.48 — the app-level face of the Aether section (the equivalent of Aether-GUI's tauri state + commands):
/// owns the singleton <see cref="AetherEngine"/>, resolves/downloads the aether.exe engine, and runs the
/// split-aware system-proxy session around it, plus the one-click presets (GAMING / STEALTH / DEFAULT).
///
/// v0.4.49 (field report: «تو قسمت تونل زنی فایرفاکس گذاشتم مستقیم و کروم تونل، و جفتشون تونل شدن با حالت اتر»):
/// the OLD "system proxy" checkbox pointed WinINET at Aether's HTTP port — a WHOLE-OS hijack that ignored the
/// per-app rules in تونل‌زنی entirely, so even apps the user marked Direct were tunneled. Now the system-proxy
/// toggle runs <see cref="AetherSplitTunnel"/> — a sing-box TUN session compiled from the same SplitPolicy every
/// other engine uses, with Aether's SOCKS5 as the proxy exit. Rules behave exactly like a normal tunnel:
/// Include mode tunnels ONLY the chosen apps; Exclude leaves the chosen apps direct; domain rules apply.
/// </summary>
public sealed class AetherService : IAsyncDisposable
{
    private readonly AetherEngineManager _manager;
    private readonly EnginePaths _paths;
    private readonly SettingsRepository _settings;
    private readonly SystemProxyManager _proxy;
    private readonly AetherEngine _engine;
    private AetherSplitTunnel? _split;
    private SystemProxyState? _proxyBackup;
    private bool _proxyApplied;

    /// <summary>Supplies the current تونل‌زنی policy + engine-always-direct list to the split session.
    /// Set by AppHost at startup (the dashboard owns the rule repository + engine paths).</summary>
    public Func<(SplitPolicy Policy, IReadOnlyList<string> AlwaysDirect)>? PolicySource { get; set; }

    public AetherService(AetherEngineManager manager, EnginePaths paths, SettingsRepository settings, SystemProxyManager proxy)
    {
        _manager = manager;
        _paths = paths;
        _settings = settings;
        _proxy = proxy;
        _engine = new AetherEngine(paths);
        _engine.StateChanged += (s, d) => OnState?.Invoke(s, d);
        _engine.Log += l => LogLine?.Invoke(l);
    }

    /// <summary>Aether engine stdout (redacted) + section state lines — wired into the Logs page.</summary>
    public event Action<string>? LogLine;
    /// <summary>Fires on every AetherState transition.</summary>
    public event Action<AetherState, string>? OnState;
    public AetherEngine Engine => _engine;
    public AetherEngineManager Manager => _manager;
    public bool IsConnected => _engine.IsConnected;
    public string PrimaryAddr => _engine.PrimaryAddr;
    /// <summary>True while the split-aware TUN session rides on top of the Aether engine (v0.4.49).</summary>
    public bool SplitActive => _split is { IsRunning: true };

    /// <summary>Resolves aether.exe: manual override → engines\aether\ → portable beside KOALIX.exe → Aether-GUI install.</summary>
    public string? FindEngine() => _manager.FindExe(_settings.Get("engine.aether.path"));

    /// <summary>Connects with the current aether.* settings. Downloads the engine first when missing (progress
    /// reported through <paramref name="progress"/>). Throws localizable codes on failure.</summary>
    public async Task ConnectAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var exe = FindEngine();
        if (exe is null)
        {
            LogLine?.Invoke("[aether] engine missing — downloading Aether v" + AetherEngineManager.PinnedVersion + " (official GitHub release, SHA-256 verified)");
            var (ok, error) = await _manager.InstallAsync(progress, ct);
            if (!ok) throw new InvalidOperationException(error ?? "Aether.Error.Download");
            exe = FindEngine();
            LogLine?.Invoke("[aether] engine installed at engines\\aether\\aether.exe");
        }
        var profile = AetherProfile.FromSettings(key => _settings.Get(key));
        LogLine?.Invoke("[aether] connecting — protocol " + profile.Protocol + ", scan " + profile.ScanMode +
            ", network " + profile.NetworkMode + ", socks " + profile.BindAddress);
        await _engine.StartAsync(exe!, profile, ct);

        // v0.4.49 — the "system proxy" toggle no longer hijacks WinINET (which tunneled EVERY app and ignored
        // تونل‌زنی). Instead a split-aware sing-box TUN session routes flows per the user's per-app rules, with
        // Aether's SOCKS5 as the proxy exit. Apps marked Direct in تونل‌زنی stay direct — like any tunnel.
        if (profile.SystemProxy)
            await StartSplitSessionAsync(profile);
    }

    /// <summary>Starts the split-aware TUN session over the running Aether engine (v0.4.49). The policy comes
    /// from the dashboard's live تونل‌زنی state; without a PolicySource a safe FullTunnel is used.</summary>
    public async Task StartSplitSessionAsync(AetherProfile? profile = null)
    {
        if (_split is { IsRunning: true }) return;
        profile ??= AetherProfile.FromSettings(key => _settings.Get(key));
        var (policy, alwaysDirect) = PolicySource?.Invoke() ?? (SplitPolicy.Full, []);
        var port = AetherProfile.ParseBind(profile.BindAddress).Port;
        LogLine?.Invoke("[aether] split-aware system proxy ON — mode " + policy.Mode + ", " + policy.Rules.Count +
            " app rules (تونل‌زنی applies: Direct apps stay direct)");
        _split = new AetherSplitTunnel(_paths, l => LogLine?.Invoke("[aether-split] " + l));
        try { await _split.StartAsync(policy, port, alwaysDirect, CancellationToken.None); }
        catch (Exception ex)
        {
            LogLine?.Invoke("[aether] split session failed: " + ex.Message);
            await _split.DisposeAsync();
            _split = null;
        }
    }

    /// <summary>Stops the split-aware TUN session (called on disconnect and when the toggle goes off).</summary>
    public async Task StopSplitSessionAsync()
    {
        if (_split is null) return;
        var s = _split; _split = null;
        await s.StopAsync();
        await s.DisposeAsync();
        LogLine?.Invoke("[aether] split-aware system proxy OFF");
    }

    /// <summary>Disconnects and stops any split session from this Aether connection.</summary>
    public async Task DisconnectAsync()
    {
        await StopSplitSessionAsync();
        await _engine.StopAsync();
        if (_proxyApplied && OperatingSystem.IsWindows())
        {
            try { if (_proxyBackup is { } b) _proxy.Apply(b); }
            catch { /* the safety-net restore in KOALIX's Repair Center also covers a dead proxy */ }
            _proxyApplied = false;
            LogLine?.Invoke("[aether] system proxy restored");
        }
    }

    /// <summary>Startup safety net: if a previous run died with the system proxy pointing at Aether's local HTTP
    /// port, put the stored setting back (Aether-GUI's restore_stale).</summary>
    public void RestoreStaleProxy()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var cur = _proxy.Read();
            if (cur.Enabled && cur.Server is { } s && (s.StartsWith("127.0.0.1:182", StringComparison.Ordinal) || s.StartsWith("localhost:182", StringComparison.Ordinal)))
            {
                _proxy.Apply(new SystemProxyState(false, null, null, null));
                LogLine?.Invoke("[aether] stale system proxy from a crashed session removed");
            }
        }
        catch { }
    }

    // ── One-click presets (settings writes only — the card re-reads them) ──

    /// <summary>GAMING: WireGuard (lightest, real UDP, lowest overhead), Turbo scan (fastest route pick), IPv4 only
    /// (game traffic is IPv4; skipping v6 halves probe traffic), noize OFF (padding adds latency), quick reconnect
    /// (re-uses the last working gateway in ~1s instead of a fresh scan), no Tor/Psiphon overlay. THE gaming preset.</summary>
    public void ApplyGamingPreset()
    {
        Set("aether.protocol", "Wireguard");
        Set("aether.scan", "Turbo");
        Set("aether.ip", "V4");
        Set("aether.quickReconnect", "1");
        Set("aether.wgNoize", "Off");
        Set("aether.network", "Warp");
        LogLine?.Invoke("[aether] GAMING preset — WireGuard + Turbo + IPv4 + noize off + quick reconnect");
    }

    /// <summary>STEALTH: MASQUE over HTTP/2 (looks like ordinary HTTPS over TCP — for networks that block/throttle
    /// UDP or DPI-fingerprint QUIC), Thorough scan, GFW noize (heaviest handshake padding), both IP versions.</summary>
    public void ApplyStealthPreset()
    {
        Set("aether.protocol", "Masque");
        Set("aether.masqueHttp2", "1");
        Set("aether.scan", "Thorough");
        Set("aether.ip", "Both");
        Set("aether.masqueNoize", "Gfw");
        Set("aether.network", "Warp");
        LogLine?.Invoke("[aether] STEALTH preset — MASQUE/HTTP2 + Thorough + GFW noize");
    }

    /// <summary>DEFAULT: Aether's own defaults, the way Aether-GUI ships (auto protocol → MASQUE, Balanced, both IPs).</summary>
    public void ApplyDefaultPreset()
    {
        Set("aether.protocol", "Auto");
        Set("aether.scan", "Balanced");
        Set("aether.ip", "Both");
        Set("aether.masqueHttp2", "0");
        Set("aether.masqueNoize", "Firewall");
        Set("aether.wgNoize", "Balanced");
        Set("aether.network", "Warp");
        LogLine?.Invoke("[aether] DEFAULT preset restored");
    }

    private void Set(string key, string value) => _settings.Set(key, value);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _engine.DisposeAsync();
    }
}
