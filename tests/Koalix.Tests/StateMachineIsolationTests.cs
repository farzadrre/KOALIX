using Koalix.Core;

namespace Koalix.Tests;

/// <summary>
/// v0.4.26 regression guard — state listeners are ISOLATED.
///
/// The 0.4.25 field incident («دیگه هیچ کانفیگی وصل نمیشه»): a cosmetic tray-icon bug
/// (TrayService._baseIcon left unassigned → Overlay(null) → ArgumentNullException('original'))
/// threw INSIDE the Connected-state event chain. ConnectionStateMachine.Transition was a raw
/// multicast invoke, so the exception sailed into ConnectionManager.ConnectAsync's catch — which
/// tore down the ALREADY HEALTHY tunnel and marked the attempt as failed. Every config, every
/// engine: the connect flow died at the moment of victory.
///
/// Contract now: a throwing listener can never abort the flow — the state stays committed,
/// remaining listeners still run, and the failure surfaces on SubscriberException for logging.
/// </summary>
public class StateMachineIsolationTests
{
    [Fact]
    public void A_throwing_listener_does_not_propagate_and_state_is_committed()
    {
        var sm = new ConnectionStateMachine();
        sm.Transition(ConnectionState.Connecting, "test");
        sm.Changed += _ => throw new InvalidOperationException("cosmetic subscriber boom");
        var reached = false;

        var ex = Record.Exception(() =>
        {
            sm.Changed += _ => reached = true; // added AFTER the throwing one — must still run
            sm.Transition(ConnectionState.Connected, "test");
        });

        Assert.Null(ex);                       // the tunnel flow must not even notice
        Assert.True(reached);                  // healthy listeners still get the news
        Assert.Equal(ConnectionState.Connected, sm.State); // state stays committed
    }

    [Fact]
    public void SubscriberException_reports_the_failure_for_logging()
    {
        var sm = new ConnectionStateMachine();
        sm.Transition(ConnectionState.Connecting, "test");
        var boom = new ArgumentNullException("original");
        Exception? reported = null;
        sm.SubscriberException += e => reported = e;
        sm.Changed += _ => throw boom;

        sm.Transition(ConnectionState.Connected, "test");

        Assert.Same(boom, reported);
        Assert.Equal(ConnectionState.Connected, sm.State);
    }

    /// <summary>The exact 0.4.25 trap, replayed at the architectural level: the connect-flow equivalent of
    /// Overlay(null) throwing inside the Connected transition must leave a usable, connected state machine.</summary>
    [Fact]
    public void Connected_transition_survives_a_cosmetic_listener_crash()
    {
        var sm = new ConnectionStateMachine();
        sm.Transition(ConnectionState.Connecting, "test");
        sm.Changed += _ => throw new ArgumentNullException("original"); // the 0.4.25 tray bug, embodied

        sm.Transition(ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, sm.State);
        // and the machine keeps working afterwards (disconnect etc.):
        sm.Transition(ConnectionState.Disconnecting, "user stop");
        sm.Transition(ConnectionState.Disconnected);
        Assert.Equal(ConnectionState.Disconnected, sm.State);
    }

    /// <summary>The tray must never again hold a null base icon — source-level guard for the WinForms field the
    /// compiler could only warn about (CS0649), because the crash class is UI-only and untestable on Linux.</summary>
    [Fact]
    public void TrayService_initializes_its_base_icon()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "repo root not found");
        var src = File.ReadAllText(Path.Combine(dir.FullName, "src", "Koalix.App", "Services", "TrayService.cs"));
        Assert.Matches(@"_baseIcon\s*\?\?=|_baseIcon\s*=", src); // assigned in Initialize (v0.4.26) — never null again
        Assert.DoesNotContain("Overlay(_baseIcon!)", src);        // the null-forgiving lie is gone
    }
}
