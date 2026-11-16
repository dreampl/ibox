using FluentAssertions;
using JackiVpn.Application.Smart;
using JackiVpn.Domain.Health;

namespace JackiVpn.UnitTests.Application;

/// <summary>Game mode: one move to a clearly lower-ping, healthy server when a game starts.</summary>
public class GameModeTests
{
    private static NodeHealthSnapshot Node(string id, double? ms, double jitter = 5, double success = 1, int failures = 0,
        HealthLabel label = HealthLabel.Good) => new()
    {
        ProfileId = id,
        MedianLatencyMs = ms,
        JitterMs = jitter,
        SuccessRate = success,
        ConsecutiveFailures = failures,
        Label = label,
        SampleCount = 5,
        SuccessCount = 5,
    };

    [Fact]
    public void Moves_to_the_clearly_lowest_ping_server()
    {
        var best = GameModeEngine.PickLowerPing(Node("now", 180), [Node("a", 120), Node("b", 90), Node("c", 175)]);

        best!.ProfileId.Should().Be("b");
    }

    [Theory]
    [InlineData(150)]   // only 17% lower
    [InlineData(80)]    // 20% lower but only 20 ms: not worth a reconnect
    public void Small_gains_do_not_move(double candidateMs)
    {
        var currentMs = candidateMs == 80 ? 100 : 180;
        GameModeEngine.PickLowerPing(Node("now", currentMs), [Node("a", candidateMs)]).Should().BeNull();
    }

    [Fact]
    public void Unhealthy_or_unmeasured_servers_are_never_chosen()
    {
        GameModeEngine.PickLowerPing(Node("now", 200),
        [
            Node("failed", 50, label: HealthLabel.Failed),
            Node("unknown", 50, label: HealthLabel.Unknown),
            Node("flaky", 50, success: 0.6),
            Node("failing", 50, failures: 1),
            Node("unmeasured", null),
        ]).Should().BeNull();
    }

    [Fact]
    public void A_shaky_server_loses_even_with_lower_ping()
    {
        var best = GameModeEngine.PickLowerPing(Node("now", 200, jitter: 5), [Node("shaky", 60, jitter: 40), Node("steady", 110, jitter: 6)]);

        best!.ProfileId.Should().Be("steady", "jitter more than 15 ms above the current server is excluded");
    }

    [Fact]
    public void Ping_plus_jitter_decides_between_good_candidates()
    {
        // a: 100 + 2*12 = 124, b: 110 + 2*2 = 114 -> b feels smoother in a game
        var best = GameModeEngine.PickLowerPing(Node("now", 200, jitter: 10), [Node("a", 100, jitter: 12), Node("b", 110, jitter: 2)]);

        best!.ProfileId.Should().Be("b");
    }

    [Fact]
    public void Nothing_to_compare_with_means_no_move()
    {
        GameModeEngine.PickLowerPing(Node("now", null), [Node("a", 40)]).Should().BeNull();
        GameModeEngine.PickLowerPing(null, [Node("a", 40)]).Should().BeNull();
        GameModeEngine.PickLowerPing(Node("now", 200), []).Should().BeNull();
    }

    [Fact]
    public void The_current_server_is_not_a_candidate()
    {
        GameModeEngine.PickLowerPing(Node("now", 200), [Node("now", 50)]).Should().BeNull();
    }
}
