using Koalix.SystemIntegration;

namespace Koalix.Tests;

/// <summary>v0.4.37 — per-game auto-activation: start/stop EDGES only, and KOALIX closes only a tunnel it opened.</summary>
public class GameLaunchTrackerTests
{
    private const string Cs = @"C:\Games\CS2\cs2.exe";
    private const string Valo = @"C:\Riot\VALORANT.exe";
    private static readonly GameProfile CsAuto = new() { Id = 1, Name = "CS2", ExecutablePath = Cs, AutoActivate = true };
    private static readonly GameProfile ValoAuto = new() { Id = 2, Name = "Valorant", ExecutablePath = Valo, AutoActivate = true };
    private static HashSet<string> Run(params string[] p) => new(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Reports_a_start_once_and_a_stop_once()
    {
        var t = new GameLaunchTracker();
        Assert.True(t.Update([CsAuto], Run()).IsEmpty);
        Assert.Equal("CS2", Assert.Single(t.Update([CsAuto], Run(Cs)).Started).Name);
        Assert.True(t.Update([CsAuto], Run(Cs)).IsEmpty); // still running → no re-trigger
        Assert.Equal("CS2", Assert.Single(t.Update([CsAuto], Run()).Stopped).Name);
        Assert.True(t.Update([CsAuto], Run()).IsEmpty);
    }

    [Fact]
    public void Ignores_games_without_auto_and_matches_paths_case_insensitively()
    {
        var t = new GameLaunchTracker();
        Assert.True(t.Update([CsAuto with { AutoActivate = false }], Run(Cs)).IsEmpty);
        Assert.Single(t.Update([CsAuto], Run(Cs.ToUpperInvariant())).Started);
    }

    [Fact]
    public void Switching_auto_off_while_running_counts_as_stopped()
    {
        var t = new GameLaunchTracker();
        t.Update([CsAuto], Run(Cs));
        Assert.Single(t.Update([], Run(Cs)).Stopped);
    }

    [Fact]
    public void Connects_only_when_down_and_disconnects_only_what_it_opened()
    {
        var t = new GameLaunchTracker();
        t.Update([CsAuto], Run(Cs));
        Assert.True(t.ShouldConnect(tunnelUp: false, autoConnectEnabled: true));
        t.Update([CsAuto], Run());
        Assert.True(t.ShouldDisconnect(tunnelUp: true));
        Assert.False(t.ConnectedForGame);
    }

    [Fact]
    public void Never_disconnects_a_tunnel_the_user_opened()
    {
        var t = new GameLaunchTracker();
        t.Update([CsAuto], Run(Cs));
        Assert.False(t.ShouldConnect(tunnelUp: true, autoConnectEnabled: true));
        t.Update([CsAuto], Run());
        Assert.False(t.ShouldDisconnect(tunnelUp: true));
    }

    [Fact]
    public void Waits_for_the_last_auto_game_before_disconnecting()
    {
        var t = new GameLaunchTracker();
        t.Update([CsAuto, ValoAuto], Run(Cs, Valo));
        Assert.True(t.ShouldConnect(false, true));
        t.Update([CsAuto, ValoAuto], Run(Valo));
        Assert.False(t.ShouldDisconnect(true)); // Valorant still running
        t.Update([CsAuto, ValoAuto], Run());
        Assert.True(t.ShouldDisconnect(true));
    }

    [Fact]
    public void Manual_takeover_releases_ownership()
    {
        var t = new GameLaunchTracker();
        t.Update([CsAuto], Run(Cs));
        t.ShouldConnect(false, true);
        t.ReleaseOwnership();
        t.Update([CsAuto], Run());
        Assert.False(t.ShouldDisconnect(true));
    }

    [Fact]
    public void Candidate_names_are_image_names_without_extension()
    {
        var n = GameLaunchTracker.CandidateNames([CsAuto, ValoAuto]);
        Assert.Contains("cs2", n);
        Assert.Contains("valorant", n); // case-insensitive set
    }
}
