using System.Diagnostics;
using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.Tests;

/// <summary>
/// v0.4.29 — the field regression from log 353: sing-box FATALed on a bad config (engine exit code 1, right after
/// start) while the composite engine's IsRunning (xray.exe) stayed true — so the connect's health wait kept
/// probing the corpse for the FULL ConnectTimeout before reporting the error, six connects in a row. The
/// UnexpectedExit handler now ends the health wait immediately: fail fast, with the engine's FATAL lines
/// already in the log as the real reason.
/// </summary>
public class ConnectFailFastTests
{
    /// <summary>Mimics the field shape: StartAsync succeeds, then the front-end dies (UnexpectedExit) while
    /// IsRunning stays true (the upstream process lives on) and nothing listens on the health port.</summary>
    private sealed class FatalOnStartEngine : ITunnelEngine
    {
        public EngineKind Kind => EngineKind.Xray;
        public bool SupportsProcessRouting(TunnelProfile profile) => true;
        public bool IsRunning => true;
        public IReadOnlyList<int> ProcessIds => [];
        public int? HealthProxyPort => 1; // nothing listens there — a probe would be refused instantly
        public (Uri Url, string Secret)? StatsApi => null;
        public event Action<LogCategory, string>? LogLine;
        public event Action<int>? UnexpectedExit;

        public Task<EngineValidation> ValidateAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct) =>
            Task.FromResult(EngineValidation.Success);

        public Task StartAsync(TunnelProfile profile, SplitPolicy policy, CancellationToken ct)
        {
            UnexpectedExit?.Invoke(1); // the FATAL — dies during/right after start
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoConflicts : INetworkConflictDetector
    {
        public Task<IReadOnlyList<NetworkConflict>> DetectAsync(IReadOnlyCollection<int> requiredLocalPorts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NetworkConflict>>([]);
    }

    private sealed class NoKillSwitch : IKillSwitch
    {
        public bool IsActive => false;
        public Task ActivateAsync(IReadOnlyList<AppRule> tunneledApps, bool blockAll, CancellationToken ct) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static TunnelProfile Profile() => new()
    {
        Name = "fatal-on-start", Engine = EngineKind.Xray, Source = ProfileSource.ShareLink, Content = "vless://test",
    };

    [Fact]
    public async Task Connect_fails_immediately_when_the_engine_dies_right_after_start()
    {
        var logs = new List<string>();
        var manager = new ConnectionManager(
            new Dictionary<EngineKind, Func<ITunnelEngine>> { [EngineKind.Xray] = () => new FatalOnStartEngine() },
            new NoConflicts(), new NoKillSwitch(), new HealthChecker())
        {
            Options = new ConnectionOptions { AutoReconnect = false, ConnectTimeout = TimeSpan.FromSeconds(8) },
        };
        manager.Log += (_, line) => logs.Add(line);

        var sw = Stopwatch.StartNew();
        var ok = await manager.ConnectAsync(Profile(), new SplitPolicy(SplitMode.FullTunnel, []), CancellationToken.None);
        sw.Stop();

        Assert.False(ok);
        // The concurrent OnEngineExitAsync may finish its own cleanup to Disconnected after the connect released
        // the gate — what it must NEVER be is Connected/Degraded (a corpse is never healthy).
        Assert.Contains(manager.StateMachine.State, new[] { ConnectionState.Error, ConnectionState.Disconnected });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"connect took {sw.Elapsed.TotalSeconds:0.0}s — the health wait probed a corpse instead of failing fast");
        Assert.Contains(logs, l => l.Contains("Health wait aborted: engine died right after start"));
    }
}
