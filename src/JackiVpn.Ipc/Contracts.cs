using System.Text.Json;
using System.Text.Json.Serialization;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Ipc;

/// <summary>Wire protocol version; Desktop and Service refuse to talk across incompatible versions.</summary>
public static class IpcProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 16 * 1024 * 1024;
    public const int MaxRuleSetBytes = 8 * 1024 * 1024;
}

public static class IpcMethods
{
    public const string Hello = "hello";
    public const string GetStatus = "status.get";
    public const string StartTunnel = "tunnel.start";
    public const string StopTunnel = "tunnel.stop";

    /// <summary>Hard stop of a cancelled connect: kill the cores and lift the kill switch now, clean up the rest after.</summary>
    public const string KillTunnel = "tunnel.kill";
    public const string RepairNetwork = "network.repair";
    public const string ReleaseKillSwitch = "killswitch.release";
    public const string GetCoreInfo = "core.info";
    public const string ApplyWinHttpProxy = "winhttp.apply";
    public const string RestoreWinHttpProxy = "winhttp.restore";
    public const string Subscribe = "events.subscribe";

    /// <summary>Open connections of the running TUN session (per-app usage).</summary>
    public const string GetConnections = "traffic.connections";

    /// <summary>New speed limits for the running session's relay.</summary>
    public const string SetTrafficLimits = "traffic.limits";

    /// <summary>New DNS servers for one network adapter (<see cref="Domain.Network.AdapterDnsChange"/>; empty = automatic).</summary>
    public const string SetAdapterDns = "adapter.dns.set";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Hello, GetStatus, StartTunnel, StopTunnel, KillTunnel, RepairNetwork, ReleaseKillSwitch, GetCoreInfo, ApplyWinHttpProxy, RestoreWinHttpProxy, Subscribe,
        GetConnections, SetTrafficLimits, SetAdapterDns,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<IpcMessageKind>))]
public enum IpcMessageKind
{
    Request = 0,
    Response,
    Event,
}

public sealed record IpcEnvelope
{
    public required IpcMessageKind Kind { get; init; }
    public required string Id { get; init; }
    public string? Method { get; init; }
    public JsonElement? Payload { get; init; }
    public IpcError? Error { get; init; }
}

public sealed record IpcError(string Code, string Message);

public static class IpcErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
    public const string UnknownMethod = "unknown_method";
    public const string Busy = "busy";
    public const string Failed = "failed";
    public const string VersionMismatch = "version_mismatch";
}

public sealed record HelloRequest(int ProtocolVersion, string ClientVersion);

public sealed record HelloResponse(int ProtocolVersion, string ServiceVersion, string SessionId);

public sealed record RuleSetPayload
{
    public required string Tag { get; init; }
    public required RuleSetFormat Format { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>Base64 content (validated + hashed by the service, stored under its own directory).</summary>
    public required string ContentBase64 { get; init; }
}

/// <summary>
/// Everything the service needs to run a TUN session. The service never receives a raw core config —
/// it validates this model and generates the configuration itself.
/// </summary>
public sealed record TunnelStartRequest
{
    public required ProxyProfile Profile { get; init; }
    public CorePreference Core { get; init; } = CorePreference.Auto;
    public DnsSettings Dns { get; init; } = new();
    public Ipv6Mode Ipv6 { get; init; } = Ipv6Mode.Auto;
    public bool? TunnelSupportsIpv6 { get; init; }
    public RoutingSettings Routing { get; init; } = new();
    public IReadOnlyList<RuleSetPayload> RuleSets { get; init; } = [];
    public KillSwitchSettings KillSwitch { get; init; } = new();
    public int Mtu { get; init; } = 9000;
    public bool MuxEnabled { get; init; }
    public bool DebugLogging { get; init; }
    public PerformanceSettings Performance { get; init; } = new();

    /// <summary>Blocks, workplace routes and shaping classes (validated by the service).</summary>
    public Domain.Usage.TrafficPolicy? Traffic { get; init; }

    public Domain.Usage.TrafficLimits Limits { get; init; } = Domain.Usage.TrafficLimits.None;

    /// <summary>Also listen as a loopback proxy on this port (the desktop points the system proxy at it; 0 = no).</summary>
    public int SystemProxyPort { get; init; }
}

public sealed record TunnelStartResponse(bool Started, CoreKind Core, bool Chained, string? Error, IReadOnlyList<string> Warnings, CapabilityReport? Report)
{
    /// <summary>Loopback proxy port of the TUN session (0 = none).</summary>
    public int ProxyPort { get; init; }
}

public sealed record WinHttpApplyRequest(int Port, string Bypass);

[JsonConverter(typeof(JsonStringEnumConverter<TunnelState>))]
public enum TunnelState
{
    Stopped = 0,
    Starting,
    Running,
    Restarting,
    Stopping,
    Failed,
}

public sealed record ServiceStatus
{
    public TunnelState State { get; init; }
    public string? ProfileId { get; init; }
    public CoreKind? Core { get; init; }
    public bool Chained { get; init; }
    public DateTimeOffset? SinceUtc { get; init; }
    public bool KillSwitchEngaged { get; init; }
    public string? LastError { get; init; }
    public long UploadTotal { get; init; }
    public long DownloadTotal { get; init; }
    public int RestartCount { get; init; }
    public string ServiceVersion { get; init; } = string.Empty;

    /// <summary>Loopback proxy port of the running TUN session (0 = none).</summary>
    public int ProxyPort { get; init; }
}

public sealed record CoreInfo(CoreKind Kind, string Version, string Sha256, bool Verified, string? Error);

public sealed record RepairResponse(bool Success, IReadOnlyList<string> Actions);

/// <summary>Events pushed to subscribed clients.</summary>
public static class IpcEvents
{
    public const string StatusChanged = "status.changed";
    public const string Traffic = "traffic";
    public const string CoreExited = "core.exited";
    public const string KillSwitchBlocking = "killswitch.blocking";
    public const string Log = "log";
}

public sealed record TrafficEvent(long UploadTotal, long DownloadTotal, long UploadPerSecond, long DownloadPerSecond);

public sealed record CoreExitedEvent(CoreKind Core, int ExitCode, bool Expected, string LastOutput, bool WillRestart);

public sealed record LogEvent(string Level, string Message, DateTimeOffset TimestampUtc);
