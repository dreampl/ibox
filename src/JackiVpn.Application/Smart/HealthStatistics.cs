using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Smart;

/// <summary>
/// Turns a node's rolling sample history into robust statistics and a score. Never decides from a single
/// sample: medians over several samples, Tukey-fence outlier removal (n ≥ 4), Laplace-smoothed success rate.
/// </summary>
public static class HealthStatistics
{
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromHours(24);

    public static NodeHealthSnapshot Compute(
        string profileId,
        IReadOnlyList<HealthSample> samples,
        int recentDisconnects,
        ScoreWeights weights,
        DateTimeOffset nowUtc,
        int window = 30)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(weights);
        var recent = samples
            .Where(s => nowUtc - s.TimestampUtc <= MaxSampleAge && s.Failure != ProbeFailureKind.Cancelled)
            .OrderBy(s => s.TimestampUtc)
            .TakeLast(window)
            .ToList();
        if (recent.Count == 0)
        {
            return NodeHealthSnapshot.Unknown(profileId) with { RecentDisconnects = recentDisconnects };
        }

        var successes = recent.Where(s => s.Success && s.TotalMs is not null).ToList();
        var latencies = successes.Select(s => s.TotalMs!.Value).ToList();
        var (kept, removed) = RemoveOutliers(latencies);

        var median = Median(kept);
        var jitter = Jitter(kept);
        var stability = kept.Count >= 2 ? Math.Clamp(1 - (StdDev(kept) / Math.Max(1, kept.Average())), 0, 1) : (kept.Count == 1 ? 0.5 : 0);
        var successRate = (successes.Count + 1.0) / (recent.Count + 2.0);
        var timeouts = recent.TakeLast(10).Count(s => s.Failure == ProbeFailureKind.Timeout);
        var consecutiveFailures = 0;
        for (var i = recent.Count - 1; i >= 0 && !recent[i].Success; i--)
        {
            consecutiveFailures++;
        }

        var snapshot = new NodeHealthSnapshot
        {
            ProfileId = profileId,
            SampleCount = recent.Count,
            SuccessCount = successes.Count,
            MedianLatencyMs = median,
            JitterMs = jitter,
            MedianTcpMs = Median(successes.Where(s => s.TcpMs is not null).Select(s => s.TcpMs!.Value).ToList()),
            MedianTlsMs = Median(successes.Where(s => s.TlsMs is not null).Select(s => s.TlsMs!.Value).ToList()),
            MedianTtfbMs = Median(successes.Where(s => s.TtfbMs is not null).Select(s => s.TtfbMs!.Value).ToList()),
            SuccessRate = successRate,
            Stability = stability,
            RecentTimeouts = timeouts,
            RecentDisconnects = recentDisconnects,
            ConsecutiveFailures = consecutiveFailures,
            ThroughputKbps = Median(recent.Where(s => s.ThroughputKbps is not null).Select(s => s.ThroughputKbps!.Value).ToList()),
            LastTestedUtc = recent[^1].TimestampUtc,
            LastSuccessUtc = successes.Count > 0 ? successes[^1].TimestampUtc : null,
            LastFailure = recent[^1].Failure,
            OutliersRemoved = removed,
        };

        var score = ScoreCalculator.Score(snapshot, weights);
        return snapshot with { Score = score, Label = Label(snapshot, score) };
    }

    public static HealthLabel Label(NodeHealthSnapshot s, double score)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.SampleCount == 0)
        {
            return HealthLabel.Unknown;
        }

        if (s.SuccessCount == 0 || s.ConsecutiveFailures >= 3 || s.SuccessRate < 0.3)
        {
            return HealthLabel.Failed;
        }

        return score switch
        {
            >= 80 => HealthLabel.Excellent,
            >= 65 => HealthLabel.Good,
            >= 45 => HealthLabel.Average,
            _ => HealthLabel.Poor,
        };
    }

    /// <summary>Tukey fences: drop values outside [Q1 − 1.5·IQR, Q3 + 1.5·IQR] when there are ≥ 4 samples.</summary>
    public static (List<double> Kept, int Removed) RemoveOutliers(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 4)
        {
            return (values.ToList(), 0);
        }

        var sorted = values.OrderBy(v => v).ToList();
        var q1 = Percentile(sorted, 0.25);
        var q3 = Percentile(sorted, 0.75);
        var iqr = q3 - q1;
        var lo = q1 - (1.5 * iqr);
        var hi = q3 + (1.5 * iqr);
        var kept = values.Where(v => v >= lo && v <= hi).ToList();
        return (kept, values.Count - kept.Count);
    }

    public static double? Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Mean absolute difference between consecutive samples (RFC 3550-style jitter, unsmoothed).</summary>
    public static double? Jitter(IReadOnlyList<double> chronological)
    {
        ArgumentNullException.ThrowIfNull(chronological);
        if (chronological.Count < 2)
        {
            return null;
        }

        double sum = 0;
        for (var i = 1; i < chronological.Count; i++)
        {
            sum += Math.Abs(chronological[i] - chronological[i - 1]);
        }

        return sum / (chronological.Count - 1);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var pos = (sorted.Count - 1) * p;
        var lower = (int)Math.Floor(pos);
        var upper = (int)Math.Ceiling(pos);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (pos - lower));
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }
}

/// <summary>Configurable weighted score in [0, 100]; missing components are excluded, not guessed.</summary>
public static class ScoreCalculator
{
    public static double Score(NodeHealthSnapshot s, ScoreWeights w)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(w);
        if (s.SuccessCount == 0 || s.MedianLatencyMs is null)
        {
            return 0;
        }

        double total = 0;
        double weightSum = 0;
        void Add(double weight, double? component)
        {
            if (component is { } c && weight > 0)
            {
                total += weight * c;
                weightSum += weight;
            }
        }

        Add(w.Latency, LowerIsBetter(s.MedianLatencyMs, w.GoodLatencyMs, w.BadLatencyMs));
        Add(w.Jitter, LowerIsBetter(s.JitterMs, w.GoodJitterMs, w.BadJitterMs));
        Add(w.SuccessRate, s.SuccessRate);
        Add(w.Handshake, LowerIsBetter(s.MedianTlsMs ?? s.MedianTcpMs, w.GoodHandshakeMs, w.BadHandshakeMs));
        Add(w.Stability, s.Stability);
        Add(w.Throughput, s.ThroughputKbps is { } t ? Math.Clamp((t - w.BadThroughputKbps) / (w.GoodThroughputKbps - w.BadThroughputKbps), 0, 1) : null);

        var score = weightSum > 0 ? 100.0 * total / weightSum : 0;
        var penalty = Math.Min(w.MaxPenalty, (s.RecentTimeouts * w.TimeoutPenalty) + (s.RecentDisconnects * w.DisconnectPenalty));
        return Math.Round(Math.Max(0, score - penalty), 2);
    }

    private static double? LowerIsBetter(double? value, double good, double bad) =>
        value is { } v ? Math.Clamp((bad - v) / (bad - good), 0, 1) : null;
}
