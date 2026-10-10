using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Ipc;
using JackiVpn.Networking.SystemProxy;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Connection;

/// <summary>
/// System Proxy mode: a user-level core with a loopback mixed inbound plus the WinINET system proxy.
/// Works without the service; WinHTTP (machine-wide) is changed only on opt-in through the service.
/// </summary>
public sealed class LocalProxyBackend : IConnectionBackend
{
    private readonly SessionPlanFactory _plans;
    private readonly ICoreEngine _singBox;
    private readonly ICoreEngine? _xray;
    private readonly Func<ISessionRunner> _runnerFactory;
    private readonly SystemProxyManager _proxy;
    private readonly Func<IpcClient?> _service;
    private readonly ILogger _logger;
    private ISessionRunner? _runner;
    private CancellationTokenSource? _trafficCts;
    private bool _winHttpApplied;
    private bool _holdOnCrash;

    public LocalProxyBackend(SessionPlanFactory plans, ICoreEngine singBox, ICoreEngine? xray, Func<ISessionRunner> runnerFactory, SystemProxyManager proxy, Func<IpcClient?> service, ILogger logger)
    {
        _plans = plans;
        _singBox = singBox;
        _xray = xray;
        _runnerFactory = runnerFactory;
        _proxy = proxy;
        _service = service;
        _logger = logger;
    }

    public ConnectionMode Mode => ConnectionMode.SystemProxy;

    public event EventHandler<BackendFault>? Faulted;

    public event EventHandler<TrafficSnapshot>? Traffic;

    public async Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Server switch: stop the old core but leave the system proxy pointing at us until the new port is
        // applied, so apps never silently fall back to a direct connection between sessions.
        await StopRunnerAsync().ConfigureAwait(false);
        var s = request.Settings;
        var sbCaps = await _singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var xrCaps = _xray is null ? null : await _xray.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var plan = await _plans.CreateAsync(new SessionRequest
        {
            Profile = request.Profile,
            Mode = ConnectionMode.SystemProxy,
            Core = request.Profile.User.CoreOverride ?? s.Connection.Core,
            Dns = s.Dns,
            Ipv6 = s.Ipv6,
            TunnelSupportsIpv6 = request.TunnelSupportsIpv6,
            Routing = s.Routing,
            RuleSetPaths = request.RuleSetPaths,
            MuxEnabled = request.MuxEnabled,
            DebugLogging = s.Logging.MinimumLevel >= AppLogLevel.Debug,
            ListenPort = s.Connection.LocalProxyPort,
            Performance = s.Performance,
            Traffic = request.Traffic,
        }, sbCaps, xrCaps, ct).ConfigureAwait(false);

        var runner = _runnerFactory();
        runner.CoreExited += OnCoreExited;
        runner.SetTrafficLimits(request.Limits);
        _runner = runner;
        _holdOnCrash = s.KillSwitch.Enabled;
        IReadOnlyList<string> notes;
        try
        {
            notes = await runner.StartAsync(plan, ct).ConfigureAwait(false);
            _proxy.Apply(plan.Frontend.ListenPort, s.Connection.ProxyBypassList);
            if (s.Connection.SetWinHttpProxy && _service() is { } svc)
            {
                await svc.SendAsync(IpcMethods.ApplyWinHttpProxy, IpcFraming.ToElement(new WinHttpApplyRequest(plan.Frontend.ListenPort, s.Connection.ProxyBypassList)), ct).ConfigureAwait(false);
                _winHttpApplied = true;
            }
        }
        catch
        {
            if (_holdOnCrash)
            {
                // Kill switch: a failed (re)start keeps proxy-aware apps pointed at the dead port.
                await StopRunnerAsync().ConfigureAwait(false);
            }
            else
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        _trafficCts = new CancellationTokenSource();
        _ = Task.Run(() => TrafficLoopAsync(runner, _trafficCts.Token), CancellationToken.None);
        return new BackendStartResult(plan.Selection.Core, plan.Chained, plan.Warnings.Concat(notes).ToArray(), plan.Frontend.ListenPort);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _trafficCts?.Cancel();
        // Restore the system proxy first so apps stop pointing at a dying port.
        _proxy.Restore();
        if (_winHttpApplied && _service() is { } svc)
        {
            try
            {
                await svc.SendAsync(IpcMethods.RestoreWinHttpProxy, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServiceUnavailableException or IpcRemoteException)
            {
                _logger.LogWarning("WinHTTP proxy restore via service failed: {Error}", ex.Message);
            }

            _winHttpApplied = false;
        }

        await StopRunnerAsync().ConfigureAwait(false);
    }

    private async Task StopRunnerAsync()
    {
        _trafficCts?.Cancel();
        if (_runner is not null)
        {
            _runner.CoreExited -= OnCoreExited;
            await _runner.DisposeAsync().ConfigureAwait(false);
            _runner = null;
        }
    }

    public Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct) =>
        Task.FromResult(_holdOnCrash && _proxy.IsApplied && _runner is not { IsRunning: true });

    public Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct) =>
        _runner is { } r ? r.GetConnectionsAsync(ct) : Task.FromResult<Domain.Usage.CoreConnections?>(null);

    public Task SetTrafficLimitsAsync(Domain.Usage.TrafficLimits limits, CancellationToken ct)
    {
        _runner?.SetTrafficLimits(limits);
        return Task.CompletedTask;
    }

    private void OnCoreExited(object? sender, CoreExitInfo info)
    {
        // The local proxy port is dead. With the kill switch on, proxy-aware apps keep pointing at the dead
        // port (their traffic fails instead of going out directly); otherwise restore direct connectivity.
        // Apps that ignore the system proxy are never covered in this mode; the UI says so.
        if (!_holdOnCrash)
        {
            _proxy.Restore();
        }

        Faulted?.Invoke(this, new BackendFault($"{info.Core} exited unexpectedly: {info.LastOutput}", false, _holdOnCrash));
    }

    private async Task TrafficLoopAsync(ISessionRunner runner, CancellationToken ct)
    {
        long lastUp = 0, lastDown = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (await runner.GetTrafficAsync(ct).ConfigureAwait(false) is { } t)
                {
                    Traffic?.Invoke(this, new TrafficSnapshot(t.UploadTotal, t.DownloadTotal, Math.Max(0, t.UploadTotal - lastUp), Math.Max(0, t.DownloadTotal - lastDown), DateTimeOffset.UtcNow));
                    lastUp = t.UploadTotal;
                    lastDown = t.DownloadTotal;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>TUN mode: the privileged service runs the session; this backend speaks the IPC protocol.</summary>
public sealed class ServiceTunnelBackend(Func<CancellationToken, Task<IpcClient>> connect, ILogger logger, SystemProxyManager? systemProxy = null) : IConnectionBackend
{
    private IpcClient? _client;
    private bool _proxyApplied;

    public ConnectionMode Mode => ConnectionMode.Tun;

    public event EventHandler<BackendFault>? Faulted;

    public event EventHandler<TrafficSnapshot>? Traffic;

    public async Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var s = request.Settings;
        var response = await client.RequestAsync<TunnelStartRequest, TunnelStartResponse>(IpcMethods.StartTunnel, new TunnelStartRequest
        {
            Profile = request.Profile,
            Core = request.Profile.User.CoreOverride ?? s.Connection.Core,
            Dns = s.Dns,
            Ipv6 = s.Ipv6,
            TunnelSupportsIpv6 = request.TunnelSupportsIpv6,
            Routing = s.Routing,
            RuleSets = request.RuleSetPayloads,
            KillSwitch = s.KillSwitch,
            Mtu = s.Connection.TunMtu,
            MuxEnabled = request.MuxEnabled,
            DebugLogging = s.Logging.MinimumLevel >= AppLogLevel.Debug,
            Performance = s.Performance,
            Traffic = request.Traffic,
            Limits = request.Limits,
            SystemProxyPort = s.Connection.SystemProxyInTun && systemProxy is not null
                ? (s.Connection.LocalProxyPort > 0 ? s.Connection.LocalProxyPort : ConnectionSettings.DefaultLocalProxyPort)
                : 0,
        }, ct).ConfigureAwait(false);

        if (!response.Started)
        {
            RestoreSystemProxy();
            throw new BackendStartException(response.Error ?? "The service could not start the tunnel.");
        }

        ApplySystemProxy(response.ProxyPort, s.Connection.ProxyBypassList);
        return new BackendStartResult(response.Core, response.Chained, response.Warnings, null);
    }

    /// <summary>Points the user's system proxy at the TUN session's loopback port (apps that check it, like Claude).</summary>
    private void ApplySystemProxy(int port, string bypass)
    {
        if (systemProxy is null || port <= 0)
        {
            RestoreSystemProxy();
            return;
        }

        try
        {
            systemProxy.Apply(port, bypass);
            _proxyApplied = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("System proxy for the TUN session not applied: {Error}", ex.Message);
        }
    }

    private void RestoreSystemProxy()
    {
        if (!_proxyApplied || systemProxy is null)
        {
            return;
        }

        try
        {
            systemProxy.Restore();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("System proxy restore failed: {Error}", ex.Message);
        }

        _proxyApplied = false;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        RestoreSystemProxy();
        if (_client is null)
        {
            return;
        }

        try
        {
            await _client.SendAsync(IpcMethods.StopTunnel, null, ct).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException ex)
        {
            logger.LogWarning("Service unavailable while stopping the tunnel: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Connects to the service without starting anything and returns its tunnel status, so a desktop that
    /// restarted while the service kept a tunnel up can re-attach to it (events included).
    /// </summary>
    public async Task<ServiceStatus> AttachAsync(CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var status = await client.RequestAsync<ServiceStatus>(IpcMethods.GetStatus, ct).ConfigureAwait(false);
        if (status.State == TunnelState.Running && status.ProxyPort > 0)
        {
            ApplySystemProxy(status.ProxyPort, new ConnectionSettings().ProxyBypassList);
        }

        return status;
    }

    public async Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct)
    {
        try
        {
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            var status = await client.RequestAsync<ServiceStatus>(IpcMethods.GetStatus, ct).ConfigureAwait(false);
            return status.KillSwitchEngaged && status.State != TunnelState.Running;
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or IpcRemoteException)
        {
            return false;
        }
    }

    public async Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct)
    {
        try
        {
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            return await client.RequestAsync<Domain.Usage.CoreConnections?>(IpcMethods.GetConnections, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or IpcRemoteException)
        {
            return null;
        }
    }

    public async Task SetTrafficLimitsAsync(Domain.Usage.TrafficLimits limits, CancellationToken ct)
    {
        try
        {
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            await client.SendAsync(IpcMethods.SetTrafficLimits, IpcFraming.ToElement(limits), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or IpcRemoteException)
        {
            logger.LogDebug("Traffic limits not applied: {Error}", ex.Message);
        }
    }

    private async Task<IpcClient> EnsureClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        var client = await connect(ct).ConfigureAwait(false);
        client.EventReceived += OnEvent;
        client.Disconnected += (_, _) => Faulted?.Invoke(this, new BackendFault("Lost connection to the service.", false, false));
        await client.SubscribeAsync(ct).ConfigureAwait(false);
        _client = client;
        return client;
    }

    private void OnEvent(object? sender, IpcEnvelope e)
    {
        switch (e.Method)
        {
            case IpcEvents.Traffic:
                var t = IpcFraming.FromElement<TrafficEvent>(e.Payload);
                Traffic?.Invoke(this, new TrafficSnapshot(t.UploadTotal, t.DownloadTotal, t.UploadPerSecond, t.DownloadPerSecond, DateTimeOffset.UtcNow));
                break;
            case IpcEvents.CoreExited:
                var x = IpcFraming.FromElement<CoreExitedEvent>(e.Payload);
                Faulted?.Invoke(this, new BackendFault($"{x.Core} exited (code {x.ExitCode}): {x.LastOutput}", x.WillRestart, false));
                break;
            case IpcEvents.StatusChanged:
                var st = IpcFraming.FromElement<ServiceStatus>(e.Payload);
                if (st.State == TunnelState.Failed)
                {
                    if (!st.KillSwitchEngaged)
                    {
                        RestoreSystemProxy();
                    }

                    Faulted?.Invoke(this, new BackendFault(st.LastError ?? "Tunnel failed.", false, st.KillSwitchEngaged));
                }

                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RestoreSystemProxy();
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
