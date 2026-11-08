namespace Koalix.Core;

/// <summary>
/// v0.4.36 — sudden-ping-spike detector for the focus overlay / sound / tray. Fed one sample per second.
/// A sample is a spike when it is at least 2× the rolling median of the previous samples AND at least 80 ms
/// above it (so 20→45 ms is not "a spike", 90→250 ms is). Needs 10 warm-up samples, then a 20 s cool-down
/// between alerts so a jittery link doesn't nag.
/// </summary>
public sealed class PingSpikeDetector
{
    private readonly Queue<double> _window = new();
    private readonly int _size;
    private int _cooldown;

    public PingSpikeDetector(int windowSize = 30) => _size = Math.Max(5, windowSize);

    public const double Ratio = 2.0;
    public const double MinJumpMs = 80;
    public const int WarmUp = 10;
    public const int CooldownSamples = 20;

    /// <summary>Median of the current window, or null before any sample.</summary>
    public double? Baseline => _window.Count == 0 ? null : Median(_window);

    /// <summary>Adds one sample (null = no measurement, ignored) and returns true when it is a spike.</summary>
    public bool Add(double? ms)
    {
        if (_cooldown > 0) _cooldown--;
        if (ms is not { } v || v <= 0 || double.IsNaN(v)) return false;
        var spike = false;
        if (_window.Count >= WarmUp)
        {
            var med = Median(_window);
            spike = v >= med * Ratio && v - med >= MinJumpMs && _cooldown == 0;
            if (spike) _cooldown = CooldownSamples;
        }
        _window.Enqueue(v);
        while (_window.Count > _size) _window.Dequeue();
        return spike;
    }

    public void Reset() { _window.Clear(); _cooldown = 0; }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
}
