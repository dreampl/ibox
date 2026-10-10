using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Connection;

public sealed record TrafficSnapshot(
    long UploadBytesTotal,
    long DownloadBytesTotal,
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    DateTimeOffset TimestampUtc)
{
    public static TrafficSnapshot Empty(DateTimeOffset now) => new(0, 0, 0, 0, now);
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionOutcome>))]
public enum ConnectionOutcome
{
    Connected = 0,
    Failed,
    Disconnected,
    CoreCrashed,
    FailedOver,
    NetworkLost,
}

public sealed record ConnectionHistoryEntry
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public ConnectionMode Mode { get; init; }
    public CoreKind Core { get; init; }
    public ConnectionOutcome Outcome { get; init; }

    /// <summary>Already redacted error text.</summary>
    public string? Error { get; init; }

    public long BytesUp { get; init; }
    public long BytesDown { get; init; }
}

/// <summary>Live status shown on the dashboard.</summary>
public sealed record ConnectionStatus
{
    public ConnectionState State { get; init; } = ConnectionState.Disconnected;
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public string? Protocol { get; init; }
    public ConnectionMode? Mode { get; init; }
    public CoreKind? Core { get; init; }
    public DateTimeOffset? ConnectedSinceUtc { get; init; }
    public TrafficSnapshot? Traffic { get; init; }
    public bool SmartModeActive { get; init; }
    public bool JackiSmartConnectActive { get; init; }
    public bool KillSwitchEngaged { get; init; }
    public string? PublicIp { get; init; }

    /// <summary>ISO 3166 alpha-2 code of the exit country (as seen by an external service through the tunnel).</summary>
    public string? ExitCountryCode { get; init; }

    public string? ExitCountryName { get; init; }
    public string? ExitCity { get; init; }
    public string? ExitIsp { get; init; }

    /// <summary>Live round-trip through the tunnel on a kept-alive connection (like a ping), in ms.</summary>
    public double? LatencyMs { get; init; }

    /// <summary>Loopback port of the local mixed proxy (System Proxy mode only).</summary>
    public int? LocalProxyPort { get; init; }

    /// <summary>Why smart mode chose this server (e.g. "fastest of 12 tested · 142 ms").</summary>
    public string? SelectionReason { get; init; }

    /// <summary>The effective gaming/streaming optimization for this session.</summary>
    public ActivityProfile Activity { get; init; }

    /// <summary>Jitter of the live latency (spread of the last samples), in ms.</summary>
    public double? JitterMs { get; init; }

    public string? LastError { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
