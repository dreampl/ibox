using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Health;

[JsonConverter(typeof(JsonStringEnumConverter<ProbeFailureKind>))]
public enum ProbeFailureKind
{
    None = 0,
    InvalidConfig,
    DnsFailed,
    TcpFailed,
    TlsFailed,
    CoreStartFailed,
    ProxyHandshakeFailed,
    HttpFailed,
    Timeout,
    Cancelled,
    Unsupported,
}

[JsonConverter(typeof(JsonStringEnumConverter<HealthLabel>))]
public enum HealthLabel
{
    Unknown = 0,
    Excellent,
    Good,
    Average,
    Poor,
    Failed,
}

/// <summary>One multi-stage probe of one node. Timings are null when the stage was not measurable.</summary>
public sealed record HealthSample
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required bool Success { get; init; }
    public ProbeFailureKind Failure { get; init; }
    public double? DnsMs { get; init; }
    public double? TcpMs { get; init; }
    public double? TlsMs { get; init; }

    /// <summary>Time to first byte of the HTTP probe through the proxy.</summary>
    public double? TtfbMs { get; init; }

    /// <summary>Total HTTP probe time through the proxy (the primary "latency").</summary>
    public double? TotalMs { get; init; }

    public double? ThroughputKbps { get; init; }

    /// <summary>Host of the probe endpoint used (no path/query).</summary>
    public string? ProbeHost { get; init; }

    /// <summary>Redacted diagnostic text.</summary>
    public string? Detail { get; init; }
}

/// <summary>Aggregated, outlier-filtered view of a node's rolling history.</summary>
public sealed record NodeHealthSnapshot
{
    public required string ProfileId { get; init; }
    public int SampleCount { get; init; }
    public int SuccessCount { get; init; }
    public double? MedianLatencyMs { get; init; }
    public double? JitterMs { get; init; }
    public double? MedianTcpMs { get; init; }
    public double? MedianTlsMs { get; init; }
    public double? MedianTtfbMs { get; init; }

    /// <summary>Laplace-smoothed success rate in [0,1].</summary>
    public double SuccessRate { get; init; }

    /// <summary>1 − coefficient of variation of latency, clamped to [0,1].</summary>
    public double Stability { get; init; }

    public int RecentTimeouts { get; init; }
    public int RecentDisconnects { get; init; }
    public int ConsecutiveFailures { get; init; }
    public double? ThroughputKbps { get; init; }
    public double Score { get; init; }
    public HealthLabel Label { get; init; }
    public DateTimeOffset? LastTestedUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public ProbeFailureKind LastFailure { get; init; }
    public int OutliersRemoved { get; init; }

    public static NodeHealthSnapshot Unknown(string profileId) => new() { ProfileId = profileId, Label = HealthLabel.Unknown };
}

/// <summary>Cached result of a MUX on/off benchmark for one profile fingerprint.</summary>
public sealed record MuxBenchmarkResult(
    string ProfileFingerprint,
    bool MuxWorks,
    double? MedianWithMuxMs,
    double? MedianWithoutMuxMs,
    bool Recommended,
    DateTimeOffset MeasuredUtc);
