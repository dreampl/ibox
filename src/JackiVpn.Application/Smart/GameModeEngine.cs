using JackiVpn.Domain.Health;

namespace JackiVpn.Application.Smart;

/// <summary>How much better a server must be before a game start moves to it.</summary>
public sealed record GamePingPolicy
{
    /// <summary>Candidate latency must be at most this share below the current one (0.25 = 25% lower).</summary>
    public double MinRelativeGain { get; init; } = 0.25;

    /// <summary>…and at least this many milliseconds lower: a move costs a reconnect, small gains are not worth it.</summary>
    public double MinAbsoluteGainMs { get; init; } = 30;

    /// <summary>Only servers that have been reliable lately.</summary>
    public double MinSuccessRate { get; init; } = 0.8;

    /// <summary>Jitter hurts games as much as ping: a candidate may not be noticeably shakier than the current server.</summary>
    public double MaxExtraJitterMs { get; init; } = 15;
}

/// <summary>
/// Game mode: when a game starts, move once to a clearly lower-ping healthy server. Never during a game: the caller
/// only asks at the transition into Gaming, and while gaming every optional move is blocked (ActivityRules.IsQuiet),
/// because a move drops every open connection of the match.
/// </summary>
public static class GameModeEngine
{
    public static NodeHealthSnapshot? PickLowerPing(NodeHealthSnapshot? current, IEnumerable<NodeHealthSnapshot> candidates, GamePingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        policy ??= new GamePingPolicy();
        if (current?.MedianLatencyMs is not { } now)
        {
            return null;                                       // nothing measured to compare with
        }

        var jitterNow = current.JitterMs ?? 0;
        return candidates
            .Where(c => c.ProfileId != current.ProfileId
                && c.Label is not (HealthLabel.Failed or HealthLabel.Unknown)
                && c.ConsecutiveFailures == 0
                && c.SuccessRate >= policy.MinSuccessRate
                && c.MedianLatencyMs is { } ms
                && ms <= now * (1 - policy.MinRelativeGain)
                && now - ms >= policy.MinAbsoluteGainMs
                && (c.JitterMs ?? 0) <= jitterNow + policy.MaxExtraJitterMs)
            .OrderBy(c => c.MedianLatencyMs!.Value + 2 * (c.JitterMs ?? 0))   // what a game feels: ping plus its swings
            .FirstOrDefault();
    }
}
