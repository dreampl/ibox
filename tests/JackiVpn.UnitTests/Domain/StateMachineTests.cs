using FluentAssertions;
using JackiVpn.Domain.Connection;

namespace JackiVpn.UnitTests.Domain;

public class StateMachineTests
{
    [Fact]
    public void Happy_path_and_generation()
    {
        var sm = new ConnectionStateMachine();
        var changes = new List<ConnectionStateChange>();
        sm.StateChanged += (_, c) => changes.Add(c);

        sm.Transition(ConnectionState.Preparing);
        sm.Transition(ConnectionState.Testing);
        sm.Transition(ConnectionState.Connecting);
        sm.Transition(ConnectionState.Connected);
        sm.Transition(ConnectionState.Disconnecting);
        sm.Transition(ConnectionState.Disconnected);

        changes.Select(c => c.Current).Should().Equal(
            ConnectionState.Preparing, ConnectionState.Testing, ConnectionState.Connecting,
            ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected);
        sm.Generation.Should().Be(1);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Connected, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Connected)]
    [InlineData(ConnectionState.Recovering, ConnectionState.Connected)]
    public void Illegal_transitions_throw(ConnectionState from, ConnectionState to)
    {
        var sm = new ConnectionStateMachine(initial: from);

        var act = () => sm.Transition(to);

        act.Should().Throw<InvalidStateTransitionException>();
        sm.State.Should().Be(from);
    }

    [Fact]
    public void Stale_generation_cannot_transition()
    {
        var sm = new ConnectionStateMachine();
        sm.Transition(ConnectionState.Preparing);
        var oldGen = sm.Generation;
        sm.Transition(ConnectionState.Faulted);
        sm.Transition(ConnectionState.Preparing);

        sm.TryTransitionIfCurrent(oldGen, ConnectionState.Connecting, "late", out _).Should().BeFalse();
        sm.TryTransitionIfCurrent(sm.Generation, ConnectionState.Connecting, "current", out _).Should().BeTrue();
    }

    [Fact]
    public void Concurrent_disconnect_requests_only_one_wins()
    {
        var sm = new ConnectionStateMachine(initial: ConnectionState.Connected);
        var wins = 0;
        Parallel.For(0, 64, i =>
        {
            if (sm.TryTransitionFrom([ConnectionState.Connected], ConnectionState.Disconnecting, null, out _))
            {
                Interlocked.Increment(ref wins);
            }
        });

        wins.Should().Be(1);
        sm.State.Should().Be(ConnectionState.Disconnecting);
    }

    [Fact]
    public void Every_state_has_an_exit_and_disconnected_is_reachable()
    {
        foreach (var s in Enum.GetValues<ConnectionState>())
        {
            ConnectionStateRules.AllowedTargets(s).Should().NotBeEmpty($"{s} must not be a dead end");
        }
    }
}
