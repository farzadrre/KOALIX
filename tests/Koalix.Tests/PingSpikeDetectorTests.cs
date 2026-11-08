using Koalix.Core;

namespace Koalix.Tests;

/// <summary>v0.4.36 — the focus-mode ping-spike alert: 2× median AND +80 ms, warm-up, cool-down.</summary>
public class PingSpikeDetectorTests
{
    private static PingSpikeDetector Warm(double ms)
    {
        var d = new PingSpikeDetector();
        for (var i = 0; i < 12; i++) Assert.False(d.Add(ms));
        return d;
    }

    [Fact]
    public void Big_jump_over_a_stable_baseline_is_a_spike() => Assert.True(Warm(90).Add(260));

    [Fact]
    public void Doubling_a_low_ping_is_not_a_spike_without_80ms() => Assert.False(Warm(20).Add(55));

    [Fact]
    public void Plus_80ms_without_doubling_is_not_a_spike() => Assert.False(Warm(200).Add(320));

    [Fact]
    public void No_alert_during_warm_up()
    {
        var d = new PingSpikeDetector();
        for (var i = 0; i < 5; i++) d.Add(80);
        Assert.False(d.Add(900));
    }

    [Fact]
    public void Cool_down_suppresses_back_to_back_alerts_then_rearms()
    {
        var d = Warm(80);
        Assert.True(d.Add(400));
        for (var i = 0; i < 5; i++) Assert.False(d.Add(400 + i)); // still cooling (and window rising)
        var d2 = Warm(80);
        Assert.True(d2.Add(400));
        for (var i = 0; i < PingSpikeDetector.CooldownSamples; i++) d2.Add(80);
        Assert.True(d2.Add(400));
    }

    [Fact]
    public void Missing_samples_are_ignored()
    {
        var d = Warm(100);
        Assert.False(d.Add(null));
        Assert.False(d.Add(double.NaN));
        Assert.Equal(100, d.Baseline);
    }
}
