using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using JackiVpn.Ipc;

namespace JackiVpn.Application.Connection;

public sealed record BackendStartRequest
{
    public required ProxyProfile Profile { get; init; }
    public required AppSettings Settings { get; init; }
    public bool? TunnelSupportsIpv6 { get; init; }
    public bool MuxEnabled { get; init; }
    public IReadOnlyDictionary<string, string> RuleSetPaths { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<RuleSetPayload> RuleSetPayloads { get; init; } = [];

    /// <summary>Blocks, workplace routes and shaping classes (static for the session).</summary>
    public TrafficPolicy? Traffic { get; init; }

    /// <summary>Initial relay limits (changed later with <see cref="IConnectionBackend.SetTrafficLimitsAsync"/>).</summary>
    public TrafficLimits Limits { get; init; } = TrafficLimits.None;
}

public sealed record BackendStartResult(CoreKind Core, bool Chained, IReadOnlyList<string> Warnings, int? LocalProxyPort);

public sealed record BackendFault(string Reason, bool WillRetryInternally, bool KillSwitchHolding);

public sealed class BackendStartException(string message) : Exception(message);

/// <summary>
/// One way of carrying traffic: Windows system proxy (in-process), Windows TUN (via the service), or the
/// Android VpnService. The orchestrator drives every backend the same way.
/// </summary>
public interface IConnectionBackend : IAsyncDisposable
{
    ConnectionMode Mode { get; }

    event EventHandler<BackendFault>? Faulted;

    event EventHandler<TrafficSnapshot>? Traffic;

    Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Hard stop for when <see cref="StopAsync"/> hangs (a cancelled connect must be down within seconds): kills the
    /// core processes and lifts the kill switch at once. May run while a graceful stop is still in progress.
    /// </summary>
    Task KillAsync(CancellationToken ct) => StopAsync(ct);

    /// <summary>True while traffic is being blocked by this backend's kill switch (session failed, filters up).</summary>
    Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct);

    /// <summary>Open connections of the running session (null when unavailable).</summary>
    Task<CoreConnections?> GetConnectionsAsync(CancellationToken ct) => Task.FromResult<CoreConnections?>(null);

    /// <summary>Changes relay speed limits of the running session (no reconnect).</summary>
    Task SetTrafficLimitsAsync(TrafficLimits limits, CancellationToken ct) => Task.CompletedTask;
}
