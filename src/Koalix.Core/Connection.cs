namespace Koalix.Core;

public enum ConnectionState { Disconnected, Connecting, Connected, Degraded, Reconnecting, Disconnecting, Error }

/// <summary>OpenVpn (v0.4.15): profiles are .ovpn files run by the official OpenVPN client (openvpn.exe).</summary>
public enum EngineKind { SingBox, Xray, WireGuard, OpenVpn }

public sealed record StateChange(ConnectionState From, ConnectionState To, string? Reason, DateTimeOffset At);

/// <summary>
/// Explicit, thread-safe connection state machine. Invalid transitions throw so that
/// bugs cannot silently report "Connected" without passing through Connecting + health checks.
/// </summary>
public sealed class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> Allowed = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Connecting],
        [ConnectionState.Connecting] = [ConnectionState.Connected, ConnectionState.Degraded, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.Connected] = [ConnectionState.Degraded, ConnectionState.Reconnecting, ConnectionState.Disconnecting, ConnectionState.Error],
        [ConnectionState.Degraded] = [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnecting, ConnectionState.Error],
        [ConnectionState.Reconnecting] = [ConnectionState.Connected, ConnectionState.Degraded, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.Disconnecting] = [ConnectionState.Disconnected, ConnectionState.Error],
        [ConnectionState.Error] = [ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Connecting, ConnectionState.Reconnecting],
    };

    private readonly object _gate = new();
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public event Action<StateChange>? Changed;

    /// <summary>
    /// v0.4.26 — raised when a Changed-subscriber throws. The 0.4.25 incident: a cosmetic tray-icon bug threw
    /// INSIDE the Connected transition, the exception sailed straight into ConnectionManager.ConnectAsync's
    /// catch and the (already healthy!) tunnel was torn down — «دیگه هیچ کانفیگی وصل نمیشه». Since then the
    /// rule is: state listeners are ISOLATED — one broken listener can never abort the connection flow again.
    /// </summary>
    public event Action<Exception>? SubscriberException;

    public bool CanTransition(ConnectionState to)
    {
        lock (_gate) return Allowed[State].Contains(to);
    }

    public void Transition(ConnectionState to, string? reason = null)
    {
        StateChange change;
        lock (_gate)
        {
            if (State == to) return;
            if (!Allowed[State].Contains(to))
                throw new InvalidOperationException($"Invalid connection state transition {State} -> {to}");
            change = new StateChange(State, to, reason, DateTimeOffset.Now);
            State = to;
            if (to == ConnectionState.Error) LastError = reason;
        }
        // v0.4.26 — per-subscriber isolation (see SubscriberException): the state is already committed,
        // so a throwing listener must not roll it back nor bubble into the connect/teardown flow.
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<StateChange>>())
        {
            try { h(change); }
            catch (Exception ex) { SubscriberException?.Invoke(ex); }
        }
    }

    public bool TryTransition(ConnectionState to, string? reason = null)
    {
        try { Transition(to, reason); return true; } catch (InvalidOperationException) { return false; }
    }
}
