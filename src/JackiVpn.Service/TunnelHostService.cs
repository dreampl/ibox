using JackiVpn.Application.Sessions;
using JackiVpn.Ipc;
using JackiVpn.Networking.Recovery;
using JackiVpn.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Service;

/// <summary>
/// Service lifetime: secure state directories → recover from an unclean previous run (journal) → serve IPC.
/// On stop (service stop, uninstall, shutdown) the tunnel is torn down in leak-safe order and the kill
/// switch is removed, so Windows is never left with orphaned filters, routes or adapters.
/// </summary>
public sealed class TunnelHostService(
    AppPaths paths,
    RecoveryCoordinator recovery,
    TunnelSessionManager tunnel,
    IpcServer ipc,
    ILogger<TunnelHostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SecureFileSystem.CreatePrivateDirectory(paths.ServiceDataRoot, PrivateScope.ServiceOnly);
        foreach (var dir in new[] { paths.ServiceStateDir, paths.ServiceLogsDir, paths.ServiceRuleSetDir, paths.ServiceCoreWorkDir })
        {
            SecureFileSystem.CreatePrivateDirectory(dir, PrivateScope.ServiceOnly);
        }

        try
        {
            var report = await recovery.RecoverAsync(stoppingToken).ConfigureAwait(false);
            if (report.WasDirty)
            {
                foreach (var a in report.Actions)
                {
                    logger.Log(a.Success ? LogLevel.Information : LogLevel.Error, "Startup recovery: {Step}: {Detail}", a.Step, a.Detail);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Recovery problems must not keep the service down: the user can still run Repair Network.
            logger.LogError("Startup recovery failed: {Error}", SecretRedactor.Redact(ex.Message));
        }

        tunnel.StatusChanged += (_, s) => _ = SafeBroadcast(IpcEvents.StatusChanged, s);
        tunnel.Traffic += (_, t) => _ = SafeBroadcast(IpcEvents.Traffic, t);
        tunnel.CoreExited += (_, e) => _ = SafeBroadcast(IpcEvents.CoreExited, e);

        ipc.Start();
        logger.LogInformation("{Product} service ready.", Domain.ProductInfo.DisplayName);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Service stopping: tearing down the tunnel.");
        try
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError("Tunnel teardown failed: {Error}", SecretRedactor.Redact(ex.Message));
        }

        await ipc.DisposeAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SafeBroadcast<T>(string name, T payload)
    {
        try
        {
            await ipc.BroadcastAsync(name, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug("Broadcast {Event} failed: {Error}", name, ex.Message);
        }
    }
}
