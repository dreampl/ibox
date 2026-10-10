using Android.Content;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Networking.Probes;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// Android connection backend. VPN mode: sing-box (separate process, excluded from the VPN) with a loopback mixed
/// inbound fed by <see cref="JackiVpnService"/> (TUN → tun2socks); on a server switch only the core is replaced and
/// the TUN stays up, so traffic is held — never sent outside the VPN — in between. Proxy-only mode: the same core
/// with a password-protected SOCKS5/HTTP inbound on 127.0.0.1 and no TUN; the service only keeps it alive.
/// </summary>
public sealed class AndroidVpnBackend(
    SessionPlanFactory plans,
    ICoreEngine singBox,
    ICoreEngine xray,
    Func<ISessionRunner> runnerFactory,
    Context context,
    Func<bool> isBackground,
    ILogger logger,
    bool proxyOnly = false) : IConnectionBackend
{
    public const int TunMtu = 8500;

    private ISessionRunner? _runner;
    private CancellationTokenSource? _trafficCts;
    private int? _port;

    public ConnectionMode Mode => proxyOnly ? ConnectionMode.SystemProxy : ConnectionMode.Tun;

    public event EventHandler<BackendFault>? Faulted;

    public event EventHandler<TrafficSnapshot>? Traffic;

    public async Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await StopRunnerAsync().ConfigureAwait(false);
        var s = request.Settings;
        var caps = await singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        Domain.Cores.CoreCapabilities? xrayCaps = null;
        try
        {
            xrayCaps = await xray.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JackiVpn.Cores.Hosting.CoreIntegrityException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Xray is not available on this device: {Error}", ex.Message);
        }

        var proxyAuth = proxyOnly && s.MobileProxy.RequirePassword && s.MobileProxy.Username is { } u && s.MobileProxy.Password is { } p
            ? new InboundCredentials(u, p)
            : null;
        var plan = await plans.CreateAsync(new SessionRequest
        {
            Profile = request.Profile,
            Mode = ConnectionMode.SystemProxy,
            ExternalTun = !proxyOnly,
            InboundAuth = proxyAuth,
            Core = s.Connection.Core,
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
        }, caps, xrayCaps, ct).ConfigureAwait(false);

        if (!proxyOnly && plan.Frontend.InboundAuth is null)
        {
            throw new InvalidOperationException("The Android core inbound must require credentials.");
        }

        var runner = runnerFactory();
        runner.CoreExited += OnCoreExited;
        runner.SetTrafficLimits(request.Limits);
        _runner = runner;
        IReadOnlyList<string> notes;
        try
        {
            notes = await runner.StartAsync(plan, ct).ConfigureAwait(false);
            _port = plan.Frontend.ListenPort;
            if (plan.Frontend.InboundAuth is { } auth)
            {
                LoopbackProxyCredentials.Set(_port.Value, auth.Username, auth.Password);
            }

            var service = await JackiVpnService.EnsureStartedAsync(context, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (proxyOnly)
            {
                service.CloseTunnel();
                service.UpdateNotification("Proxy on 127.0.0.1:" + _port, request.Profile.DisplayName);
            }
            else
            {
                var a = plan.Frontend.InboundAuth!;
                service.OpenTunnel(new TunnelOptions(_port.Value, a.Username, a.Password, TunMtu, s.Routing.BypassLan, s.SplitTunnel.Mode, TunnelOptions.JoinPackages(s.SplitTunnel.Packages)));
                service.UpdateNotification("Connected", request.Profile.DisplayName);
            }
        }
        catch
        {
            await StopRunnerAsync().ConfigureAwait(false);
            throw;
        }

        _trafficCts = new CancellationTokenSource();
        _ = Task.Run(() => TrafficLoopAsync(runner, _trafficCts.Token), CancellationToken.None);
        var warnings = plan.Warnings.Concat(notes).ToList();
        if (proxyOnly && proxyAuth is null)
        {
            warnings.Add("The local proxy has no password: any app on this phone can use it and see your VPN exit.");
        }

        return new BackendStartResult(plan.Selection.Core, plan.Chained, warnings, plan.Frontend.ListenPort);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        JackiVpnService.Current?.CloseTunnel();
        JackiVpnService.Stop(context);
        await StopRunnerAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// While the TUN is up with no working core, device traffic is held inside the VPN (fail-closed). Android's
    /// own "Block connections without VPN" setting covers the time the VPN is fully off.
    /// </summary>
    public Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct) =>
        Task.FromResult(JackiVpnService.Current?.IsTunnelOpen == true && _runner is not { IsRunning: true });

    public Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct) =>
        _runner is { } r ? r.GetConnectionsAsync(ct) : Task.FromResult<Domain.Usage.CoreConnections?>(null);

    public Task SetTrafficLimitsAsync(Domain.Usage.TrafficLimits limits, CancellationToken ct)
    {
        _runner?.SetTrafficLimits(limits);
        return Task.CompletedTask;
    }

    private async Task StopRunnerAsync()
    {
        _trafficCts?.Cancel();
        if (_port is { } port)
        {
            LoopbackProxyCredentials.Clear(port);
            _port = null;
        }

        if (_runner is not null)
        {
            _runner.CoreExited -= OnCoreExited;
            await _runner.DisposeAsync().ConfigureAwait(false);
            _runner = null;
        }
    }

    private void OnCoreExited(object? sender, CoreExitInfo info)
    {
        logger.LogError("Core exited on Android: {Output}", info.LastOutput);
        JackiVpnService.Current?.UpdateNotification("Reconnecting…", proxyOnly ? "The proxy core stopped; restarting." : "The VPN core stopped; traffic is held until it is back.");
        Faulted?.Invoke(this, new BackendFault($"{info.Core} exited unexpectedly: {info.LastOutput}", false, !proxyOnly));
    }

    /// <summary>Traffic for the UI: every second in front, every 10 s in the background (battery).</summary>
    private async Task TrafficLoopAsync(ISessionRunner runner, CancellationToken ct)
    {
        long lastUp = 0, lastDown = 0;
        var last = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(isBackground() ? 10_000 : 1000, ct).ConfigureAwait(false);
                if (await runner.GetTrafficAsync(ct).ConfigureAwait(false) is { } t)
                {
                    var now = DateTimeOffset.UtcNow;
                    var seconds = Math.Max(1, (now - last).TotalSeconds);
                    Traffic?.Invoke(this, new TrafficSnapshot(t.UploadTotal, t.DownloadTotal,
                        (long)(Math.Max(0, t.UploadTotal - lastUp) / seconds), (long)(Math.Max(0, t.DownloadTotal - lastDown) / seconds), now));
                    lastUp = t.UploadTotal;
                    lastDown = t.DownloadTotal;
                    last = now;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogDebug("Traffic stats unavailable: {Error}", ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopRunnerAsync().ConfigureAwait(false);
}
