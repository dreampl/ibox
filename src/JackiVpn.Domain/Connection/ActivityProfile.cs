using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Connection;

/// <summary>What the user is doing right now, as far as the connection should care.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityProfile>))]
public enum ActivityProfile
{
    Normal = 0,

    /// <summary>A game is in the foreground: latency and jitter matter most, and no reconnect may happen.</summary>
    Gaming,

    /// <summary>Watching or broadcasting a stream: sustained throughput, and no interruption.</summary>
    Streaming,
}

/// <summary>User setting for the gaming/streaming optimization.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityOptimization>))]
public enum ActivityOptimization
{
    /// <summary>Detect games and streams automatically (default).</summary>
    Auto = 0,
    Off,
    AlwaysGaming,
    AlwaysStreaming,
}

public static class ActivityRules
{
    public static ActivityProfile Effective(ActivityOptimization setting, ActivityProfile detected) => setting switch
    {
        ActivityOptimization.Off => ActivityProfile.Normal,
        ActivityOptimization.AlwaysGaming => ActivityProfile.Gaming,
        ActivityOptimization.AlwaysStreaming => ActivityProfile.Streaming,
        _ => detected,
    };

    /// <summary>In a game or stream nothing optional may disturb the connection (pool tests, tuning, proactive moves).</summary>
    public static bool IsQuiet(ActivityProfile profile) => profile != ActivityProfile.Normal;
}
