using JackiVpn.Cores;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
#if WINDOWS
using JackiVpn.Networking.Adapters;
#endif
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.Recovery;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Sessions;

public sealed record CoreExitInfo(CoreKind Core, int ExitCode, string LastOutput, DateTimeOffset AtUtc);

public sealed class CoreValidationFailedException(string message, IReadOnlyList<string> errors) : Exception(message)
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Runs the core process(es) of one session: validates every generated config with the core itself first
/// (a rejected config never starts), starts the Xray backend before the sing-box frontend, waits for real
/// readiness, records the processes in the journal and reports any unexpected exit immediately.
/// </summary>
public sealed class CoreSupervisor : IAsyncDisposable
{
    private readonly CoreBinaryCatalog _catalog;
    private readonly ICoreEngine _singBox;
    private readonly ICoreEngine? _xray;
    private readonly string _workDir;
    private readonly ILogger _logger;
    private readonly NetworkStateJournal? _journal;
    private readonly List<(CoreProcess Process, string? JournalId)> _processes = [];
    private ClashApiClient? _api;
    private int _exitReported;

    public CoreSupervisor(CoreBinaryCatalog catalog, ICoreEngine singBox, ICoreEngine? xray, string workDir, ILogger logger, NetworkStateJournal? journal = null)
    {
        _catalog = catalog;
        _singBox = singBox;
        _xray = xray;
        _workDir = workDir;
        _logger = logger;
        _journal = journal;
    }

    public event EventHandler<CoreExitInfo>? CoreExited;

    public bool IsRunning => _processes.Count > 0 && _processes.All(p => !p.Process.HasExited);

    public IReadOnlyList<int> ProcessIds => _processes.Select(p => p.Process.ProcessId).ToArray();

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public IReadOnlyList<string> RecentOutput => _processes.SelectMany(p => p.Process.RecentOutput).TakeLast(200).ToArray();

    public async Task<IReadOnlyList<string>> StartAsync(SessionPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_processes.Count > 0)
        {
            throw new InvalidOperationException("Supervisor already running a session.");
        }

        var notes = new List<string>();
        var singBoxCaps = await _singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        GeneratedCoreConfig? backendConfig = null;
        if (plan.Backend is not null)
        {
            var xray = _xray ?? throw new InvalidOperationException("Xray is required for this profile but not installed.");
            var xrayCaps = await xray.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            backendConfig = xray.GenerateConfig(plan.Backend, xrayCaps);
            await ValidateAsync(xray, backendConfig, ct).ConfigureAwait(false);
            notes.AddRange(backendConfig.Notes);
        }

        var frontendConfig = _singBox.GenerateConfig(plan.Frontend, singBoxCaps);
        await ValidateAsync(_singBox, frontendConfig, ct).ConfigureAwait(false);
        notes.AddRange(frontendConfig.Notes);

        try
        {
            if (plan.Frontend.Shaper is { } shaper && plan.Frontend.Traffic is { Shaping: true })
            {
                // The relay listens before the core starts routing connections to it.
                _shaper = new Traffic.TrafficShaper(shaper, _logger, GetConnectionsAsync);
                _shaper.SetLimits(_limits);
                _shaper.Start();
            }

            if (backendConfig is not null)
            {
                var backend = await LaunchAsync(_xray!, backendConfig, ct).ConfigureAwait(false);
                await backend.WaitForPortAsync(plan.Backend!.ListenPort, StartTimeout, ct).ConfigureAwait(false);
            }

            var front = await LaunchAsync(_singBox, frontendConfig, ct).ConfigureAwait(false);
            if (plan.Frontend.Kind == SessionKind.Tun)
            {
                await WaitForTunAsync(front, plan.Frontend, ct).ConfigureAwait(false);
            }
            else
            {
                await front.WaitForPortAsync(plan.Frontend.ListenPort, StartTimeout, ct).ConfigureAwait(false);
            }

            if (frontendConfig.ClashApi is { } api)
            {
                _api = new ClashApiClient(api);
            }
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }

        return notes;
    }

    private static async Task ValidateAsync(ICoreEngine engine, GeneratedCoreConfig config, CancellationToken ct)
    {
        var result = await engine.ValidateAsync(config, ct).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new CoreValidationFailedException($"{engine.Kind} rejected the generated configuration: {result.Summary}", result.Errors);
        }
    }

    private async Task<CoreProcess> LaunchAsync(ICoreEngine engine, GeneratedCoreConfig config, CancellationToken ct)
    {
        using var binary = _catalog.Verify(engine.Kind);
        var process = await CoreProcess.StartAsync(binary, engine.BuildLaunchSpec(_workDir), config, _logger, ct).ConfigureAwait(false);
        string? journalId = null;
        if (_journal is not null)
        {
            journalId = _journal.Record(JournalEntryKind.CoreProcess, new CoreProcessJournalData(process.ProcessId, process.ExecutablePath, process.StartedUtc));
        }

        _processes.Add((process, journalId));
        _ = process.Exited.ContinueWith(t => OnExited(process, t.Result), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return process;
    }

    private async Task WaitForTunAsync(CoreProcess front, CoreSessionPlan plan, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        using var api = plan.ClashApi is { } options ? new ClashApiClient(options) : null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (front.HasExited)
            {
                throw new CoreStartException($"sing-box exited while creating the TUN adapter: {front.LastError()}", front.RecentOutput);
            }

#if WINDOWS
            var adapterUp = NetworkInspector.FindAdapter(plan.Tun.InterfaceName) is { Status: System.Net.NetworkInformation.OperationalStatus.Up };
#else
            var adapterUp = true; // TUN sessions created by the core itself only exist on Windows (Android uses VpnService).
#endif
            var apiUp = api is null;
            if (api is not null)
            {
                try
                {
                    apiUp = await api.GetVersionAsync(ct).ConfigureAwait(false) is not null;
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                }
            }

            if (adapterUp && apiUp)
            {
                return;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        throw new CoreStartException($"TUN adapter '{plan.Tun.InterfaceName}' did not come up within {StartTimeout.TotalSeconds:0}s. {front.LastError()}", front.RecentOutput);
    }

    private void OnExited(CoreProcess process, int code)
    {
        if (process.StopRequested)
        {
            return;
        }

        if (Interlocked.Exchange(ref _exitReported, 1) == 1)
        {
            return;
        }

        CoreExited?.Invoke(this, new CoreExitInfo(process.Kind, code, process.LastError(), DateTimeOffset.UtcNow));
    }

    public async Task<CoreTrafficTotals?> GetTrafficAsync(CancellationToken ct)
    {
        if (_api is null)
        {
            return null;
        }

        try
        {
            return await _api.GetTotalsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct)
    {
        if (_api is not { } api)
        {
            return null;
        }

        try
        {
            return await api.GetConnectionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            return null;
        }
    }

    private Traffic.TrafficShaper? _shaper;
    private Domain.Usage.TrafficLimits _limits = Domain.Usage.TrafficLimits.None;

    public void SetTrafficLimits(Domain.Usage.TrafficLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _shaper?.SetLimits(limits);
    }

    public async Task StopAsync()
    {
        // Frontend first (it owns the TUN), then the backend.
        for (var i = _processes.Count - 1; i >= 0; i--)
        {
            var (process, journalId) = _processes[i];
            await process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            if (journalId is not null)
            {
                _journal?.Complete(journalId);
            }
        }

        _processes.Clear();
        if (_shaper is not null)
        {
            await _shaper.DisposeAsync().ConfigureAwait(false);
            _shaper = null;
        }

        _api?.Dispose();
        _api = null;
        Interlocked.Exchange(ref _exitReported, 0);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
