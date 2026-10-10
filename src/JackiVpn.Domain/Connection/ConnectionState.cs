using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Connection;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionState>))]
public enum ConnectionState
{
    Disconnected = 0,
    Preparing,
    Testing,
    Connecting,
    Connected,
    Reconnecting,
    SwitchingServer,
    Disconnecting,
    Faulted,
    Recovering,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionMode>))]
public enum ConnectionMode
{
    /// <summary>Local mixed inbound + Windows system proxy. Not every application honours it.</summary>
    SystemProxy = 0,

    /// <summary>Wintun virtual adapter driven by sing-box; captures all traffic.</summary>
    Tun,
}

[JsonConverter(typeof(JsonStringEnumConverter<CoreKind>))]
public enum CoreKind
{
    SingBox = 0,
    Xray,
}

[JsonConverter(typeof(JsonStringEnumConverter<CorePreference>))]
public enum CorePreference
{
    Auto = 0,
    SingBox,
    Xray,
}

public sealed class InvalidStateTransitionException(ConnectionState from, ConnectionState to)
    : InvalidOperationException($"Illegal connection state transition {from} -> {to}.")
{
    public ConnectionState From { get; } = from;
    public ConnectionState To { get; } = to;
}

public sealed record ConnectionStateChange(
    ConnectionState Previous,
    ConnectionState Current,
    long Generation,
    string? Reason,
    DateTimeOffset TimestampUtc);

/// <summary>The single authoritative transition table.</summary>
public static class ConnectionStateRules
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> Allowed = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Preparing, ConnectionState.Recovering],
        [ConnectionState.Preparing] = [ConnectionState.Testing, ConnectionState.Connecting, ConnectionState.Disconnecting, ConnectionState.Faulted],
        [ConnectionState.Testing] = [ConnectionState.Connecting, ConnectionState.Disconnecting, ConnectionState.Faulted],
        [ConnectionState.Connecting] = [ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Faulted],
        [ConnectionState.Connected] = [ConnectionState.Reconnecting, ConnectionState.SwitchingServer, ConnectionState.Disconnecting, ConnectionState.Faulted],
        [ConnectionState.Reconnecting] = [ConnectionState.Connected, ConnectionState.SwitchingServer, ConnectionState.Disconnecting, ConnectionState.Faulted],
        [ConnectionState.SwitchingServer] = [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnecting, ConnectionState.Faulted],
        [ConnectionState.Disconnecting] = [ConnectionState.Disconnected, ConnectionState.Faulted],
        [ConnectionState.Faulted] = [ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Recovering, ConnectionState.Preparing],
        [ConnectionState.Recovering] = [ConnectionState.Disconnected, ConnectionState.Faulted],
    };

    public static bool CanTransition(ConnectionState from, ConnectionState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyList<ConnectionState> AllowedTargets(ConnectionState from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];

    /// <summary>States in which a tunnel/proxy may be carrying traffic and must be torn down on disconnect.</summary>
    public static bool IsActive(ConnectionState s) =>
        s is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting or ConnectionState.SwitchingServer;

    /// <summary>States from which the user-facing "Connect" command is meaningful.</summary>
    public static bool IsIdle(ConnectionState s) => s is ConnectionState.Disconnected or ConnectionState.Faulted;
}

/// <summary>
/// Thread-safe state holder. Every transition is validated against <see cref="ConnectionStateRules"/>;
/// illegal transitions throw instead of silently corrupting state. <see cref="Generation"/> increments on
/// each new session (<see cref="ConnectionState.Preparing"/>) so late events from an older session can be
/// detected and ignored.
/// </summary>
public sealed class ConnectionStateMachine
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private ConnectionState _state;
    private long _generation;

    public ConnectionStateMachine(TimeProvider? time = null, ConnectionState initial = ConnectionState.Disconnected)
    {
        _time = time ?? TimeProvider.System;
        _state = initial;
    }

    public event EventHandler<ConnectionStateChange>? StateChanged;

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public ConnectionStateChange Transition(ConnectionState to, string? reason = null)
    {
        if (!TryTransition(to, reason, out var change))
        {
            throw new InvalidStateTransitionException(State, to);
        }

        return change!;
    }

    public bool TryTransition(ConnectionState to, string? reason, out ConnectionStateChange? change) =>
        TryTransitionCore(expectedGeneration: null, from: null, to, reason, out change);

    /// <summary>Transitions only if the current generation matches (i.e. the caller's session is still current).</summary>
    public bool TryTransitionIfCurrent(long generation, ConnectionState to, string? reason, out ConnectionStateChange? change) =>
        TryTransitionCore(generation, from: null, to, reason, out change);

    /// <summary>Atomically transitions only when the current state is one of <paramref name="from"/>.</summary>
    public bool TryTransitionFrom(IReadOnlyCollection<ConnectionState> from, ConnectionState to, string? reason, out ConnectionStateChange? change) =>
        TryTransitionCore(expectedGeneration: null, from, to, reason, out change);

    private bool TryTransitionCore(
        long? expectedGeneration,
        IReadOnlyCollection<ConnectionState>? from,
        ConnectionState to,
        string? reason,
        out ConnectionStateChange? change)
    {
        lock (_gate)
        {
            if ((expectedGeneration is { } g && g != _generation)
                || (from is not null && !from.Contains(_state))
                || !ConnectionStateRules.CanTransition(_state, to))
            {
                change = null;
                return false;
            }

            var previous = _state;
            _state = to;
            if (to == ConnectionState.Preparing)
            {
                _generation++;
            }

            change = new ConnectionStateChange(previous, to, _generation, reason, _time.GetUtcNow());
        }

        StateChanged?.Invoke(this, change);
        return true;
    }
}
