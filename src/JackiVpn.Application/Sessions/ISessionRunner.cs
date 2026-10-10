using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Application.Sessions;

/// <summary>Runs the cores of one session (implemented by <see cref="CoreSupervisor"/>; faked in tests).</summary>
public interface ISessionRunner : IAsyncDisposable
{
    event EventHandler<CoreExitInfo>? CoreExited;

    bool IsRunning { get; }

    IReadOnlyList<int> ProcessIds { get; }

    Task<IReadOnlyList<string>> StartAsync(SessionPlan plan, CancellationToken ct);

    Task StopAsync();

    Task<CoreTrafficTotals?> GetTrafficAsync(CancellationToken ct);

    /// <summary>Open connections (per-app/per-site usage); null when the core does not report them.</summary>
    Task<CoreConnections?> GetConnectionsAsync(CancellationToken ct) => Task.FromResult<CoreConnections?>(null);

    /// <summary>Applies speed limits to the traffic relay without restarting anything.</summary>
    void SetTrafficLimits(TrafficLimits limits)
    {
    }
}

public sealed class SupervisorRunner(CoreSupervisor supervisor) : ISessionRunner
{
    public event EventHandler<CoreExitInfo>? CoreExited
    {
        add => supervisor.CoreExited += value;
        remove => supervisor.CoreExited -= value;
    }

    public bool IsRunning => supervisor.IsRunning;

    public IReadOnlyList<int> ProcessIds => supervisor.ProcessIds;

    public Task<IReadOnlyList<string>> StartAsync(SessionPlan plan, CancellationToken ct) => supervisor.StartAsync(plan, ct);

    public Task StopAsync() => supervisor.StopAsync();

    public Task<CoreTrafficTotals?> GetTrafficAsync(CancellationToken ct) => supervisor.GetTrafficAsync(ct);

    public Task<CoreConnections?> GetConnectionsAsync(CancellationToken ct) => supervisor.GetConnectionsAsync(ct);

    public void SetTrafficLimits(TrafficLimits limits) => supervisor.SetTrafficLimits(limits);

    public ValueTask DisposeAsync() => supervisor.DisposeAsync();
}
