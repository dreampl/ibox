using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Smart;

public enum FailoverDecisionKind
{
    Stay = 0,

    /// <summary>The current node keeps failing and a healthier node exists.</summary>
    SwitchForFailure,

    /// <summary>Hysteresis-guarded proactive move to a clearly better node (Jacki Smart Connect only).</summary>
    SwitchProactive,

    /// <summary>The current node failed but there is no better candidate: retry it.</summary>
    Reconnect,
}

public sealed record FailoverDecision(FailoverDecisionKind Kind, string? TargetProfileId, string Reason);

public sealed record FailoverContext
{
    public required string CurrentProfileId { get; init; }
    public required NodeHealthSnapshot Current { get; init; }
    public required int ConsecutiveFailures { get; init; }
    public required DateTimeOffset ConnectedSinceUtc { get; init; }
    public required IReadOnlyList<NodeHealthSnapshot> Candidates { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset> CooldownUntil { get; init; } = new Dictionary<string, DateTimeOffset>();
    public int SwitchesInLastHour { get; init; }
    public bool ProactiveAllowed { get; init; }
    public required DateTimeOffset NowUtc { get; init; }
}

/// <summary>
/// Failover with hysteresis so the client never flip-flops between two similar nodes:
/// <list type="bullet">
/// <item>Failure switch only after N consecutive failures of the current node.</item>
/// <item>Proactive switch only if the candidate beats the current score by a ratio <b>and</b> an absolute gap,
/// after a minimum dwell time on the current node, within an hourly switch budget.</item>
/// <item>Nodes abandoned for failures are on cooldown and not reconsidered until it expires.</item>
/// </list>
/// </summary>
public static class FailoverEngine
{
    public const int MinSamplesForProactive = 3;

    public static FailoverDecision Decide(FailoverContext ctx, FailoverPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(policy);
        var eligible = ctx.Candidates
            .Where(c => c.ProfileId != ctx.CurrentProfileId)
            .Where(c => !ctx.CooldownUntil.TryGetValue(c.ProfileId, out var until) || until <= ctx.NowUtc)
            .Where(c => c.Label is not HealthLabel.Failed)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.MedianLatencyMs ?? double.MaxValue)
            .ToList();

        if (ctx.ConsecutiveFailures >= policy.ConsecutiveFailuresToFailover)
        {
            var target = eligible.FirstOrDefault(c => c.Label != HealthLabel.Unknown) ?? eligible.FirstOrDefault();
            return target is null
                ? new FailoverDecision(FailoverDecisionKind.Reconnect, ctx.CurrentProfileId, $"{ctx.ConsecutiveFailures} consecutive failures and no healthy alternative; retrying the current node.")
                : new FailoverDecision(FailoverDecisionKind.SwitchForFailure, target.ProfileId, $"{ctx.ConsecutiveFailures} consecutive failures; switching to a node scoring {target.Score:0}.");
        }

        if (!ctx.ProactiveAllowed)
        {
            return new FailoverDecision(FailoverDecisionKind.Stay, null, "Proactive switching disabled.");
        }

        if (ctx.NowUtc - ctx.ConnectedSinceUtc < policy.MinDwell)
        {
            return new FailoverDecision(FailoverDecisionKind.Stay, null, "Minimum dwell time not reached.");
        }

        if (ctx.SwitchesInLastHour >= policy.MaxSwitchesPerHour)
        {
            return new FailoverDecision(FailoverDecisionKind.Stay, null, "Hourly switch budget exhausted.");
        }

        var best = eligible.FirstOrDefault(c => c.SampleCount >= MinSamplesForProactive && c.Label != HealthLabel.Unknown);
        if (best is null)
        {
            return new FailoverDecision(FailoverDecisionKind.Stay, null, "No sufficiently measured alternative.");
        }

        var threshold = Math.Max(ctx.Current.Score * (1 + policy.SwitchMarginRatio), ctx.Current.Score + policy.MinScoreGap);
        return best.Score >= threshold
            ? new FailoverDecision(FailoverDecisionKind.SwitchProactive, best.ProfileId, $"Candidate scores {best.Score:0} vs current {ctx.Current.Score:0} (threshold {threshold:0}).")
            : new FailoverDecision(FailoverDecisionKind.Stay, null, $"Best candidate {best.Score:0} does not clear the hysteresis threshold {threshold:0}.");
    }

    /// <summary>Initial pick: highest score among measured nodes; unmeasured nodes only if nothing is measured.</summary>
    public static NodeHealthSnapshot? SelectBest(IEnumerable<NodeHealthSnapshot> nodes, ISet<string>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var list = nodes.Where(n => excluded is null || !excluded.Contains(n.ProfileId)).ToList();
        return list.Where(n => n.Label is not (HealthLabel.Failed or HealthLabel.Unknown))
                .OrderByDescending(n => n.Score)
                .ThenBy(n => n.MedianLatencyMs ?? double.MaxValue)
                .FirstOrDefault()
            ?? list.FirstOrDefault(n => n.Label == HealthLabel.Unknown);
    }
}
