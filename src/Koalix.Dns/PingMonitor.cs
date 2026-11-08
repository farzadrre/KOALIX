using System.Net;

namespace Koalix.Dns;

public sealed record PingSample(DateTimeOffset At, double? Ms);

/// <summary>Continuous ICMP monitor for one target with a bounded rolling window (constant memory).</summary>
public sealed class PingMonitor(IIcmpProber prober, IPAddress target, int window = 120) : IAsyncDisposable
{
    private readonly LinkedList<PingSample> _samples = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    public IPAddress Target { get; } = target;
    public event Action<PingSample, LatencyStats>? Sampled;

    public void Start(TimeSpan interval, TimeSpan timeout)
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            do
            {
                var ms = await prober.PingOnceAsync(Target, timeout, ct);
                var s = new PingSample(DateTimeOffset.Now, ms);
                LatencyStats stats;
                lock (_samples)
                {
                    _samples.AddLast(s);
                    while (_samples.Count > window) _samples.RemoveFirst();
                    stats = Stats();
                }
                Sampled?.Invoke(s, stats);
            } while (await timer.WaitForNextTickAsync(ct));
        }, ct);
    }

    private LatencyStats Stats() => LatencyStats.From(_samples.Count, _samples.Where(x => x.Ms is not null).Select(x => x.Ms!.Value).ToList());

    public IReadOnlyList<PingSample> Snapshot() { lock (_samples) return _samples.ToList(); }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose(); _cts = null; _loop = null;
    }
}
