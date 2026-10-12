using System.Text.Json;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Ipc;
using JackiVpn.Networking.Recovery;
using JackiVpn.Networking.SystemProxy;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Service;

/// <summary>
/// The privileged request surface. Every payload is deserialized into a typed model and validated before it
/// touches anything that runs as SYSTEM; the service never accepts raw core configs, paths or commands.
/// </summary>
public sealed class ServiceRequestHandler(
    TunnelSessionManager tunnel,
    RecoveryCoordinator recovery,
    WinHttpProxyManager winHttp,
    CoreBinaryCatalog catalog,
    ICoreEngine singBox,
    ICoreEngine xray,
    ILogger<ServiceRequestHandler> logger) : IIpcRequestHandler
{
    public const int MaxBypassLength = 4096;

    public async Task<JsonElement?> HandleAsync(string method, JsonElement? payload, IpcClientContext client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        logger.LogDebug("IPC {Method} from pid {Pid}.", method, client.ProcessId);
        switch (method)
        {
            case IpcMethods.GetStatus:
                return IpcFraming.ToElement(tunnel.Status);

            case IpcMethods.StartTunnel:
            {
                var request = Parse<TunnelStartRequest>(payload);
                try
                {
                    TunnelSessionManager.Validate(request);
                }
                catch (TunnelRequestException ex)
                {
                    throw new IpcHandlerException(IpcErrorCodes.BadRequest, ex.Message);
                }

                try
                {
                    return IpcFraming.ToElement(await tunnel.StartAsync(request, ct).ConfigureAwait(false));
                }
                catch (TunnelRequestException ex)
                {
                    throw new IpcHandlerException(IpcErrorCodes.BadRequest, ex.Message);
                }
            }

            case IpcMethods.StopTunnel:
                await tunnel.StopAsync(ct).ConfigureAwait(false);
                return IpcFraming.ToElement(tunnel.Status);

            case IpcMethods.KillTunnel:
                tunnel.Kill();
                return IpcFraming.ToElement(tunnel.Status);

            case IpcMethods.ReleaseKillSwitch:
                await tunnel.ReleaseKillSwitchAsync(ct).ConfigureAwait(false);
                return IpcFraming.ToElement(tunnel.Status);

            case IpcMethods.RepairNetwork:
            {
                // Repair removes kill-switch filters and stale adapters: never while a tunnel is protecting traffic.
                if (tunnel.Status.State is TunnelState.Running or TunnelState.Starting or TunnelState.Restarting)
                {
                    throw new IpcHandlerException(IpcErrorCodes.Busy, "Disconnect before repairing the network.");
                }

                var report = await recovery.RepairAsync(ct).ConfigureAwait(false);
                return IpcFraming.ToElement(new RepairResponse(report.Success, report.Actions.Select(a => $"{(a.Success ? "OK" : "FAILED")} {a.Step}: {a.Detail}").ToArray()));
            }

            case IpcMethods.GetCoreInfo:
                return IpcFraming.ToElement(await CoreInfoAsync(ct).ConfigureAwait(false));

            case IpcMethods.ApplyWinHttpProxy:
            {
                var request = Parse<WinHttpApplyRequest>(payload);
                if (request.Port is < 1 or > 65535)
                {
                    throw new IpcHandlerException(IpcErrorCodes.BadRequest, "Invalid port.");
                }

                var bypass = request.Bypass ?? string.Empty;
                if (bypass.Length > MaxBypassLength || NetText.HasControlChars(bypass))
                {
                    throw new IpcHandlerException(IpcErrorCodes.BadRequest, "Invalid bypass list.");
                }

                // Only ever a loopback proxy: a client cannot point machine-wide traffic anywhere else.
                winHttp.Apply(request.Port, bypass);
                return null;
            }

            case IpcMethods.RestoreWinHttpProxy:
                winHttp.Restore();
                return null;

            case IpcMethods.GetConnections:
                return IpcFraming.ToElement(await tunnel.GetConnectionsAsync(ct).ConfigureAwait(false));

            case IpcMethods.SetTrafficLimits:
            {
                var limits = Parse<Domain.Usage.TrafficLimits>(payload);
                try
                {
                    TunnelSessionManager.ValidateLimits(limits);
                }
                catch (TunnelRequestException ex)
                {
                    throw new IpcHandlerException(IpcErrorCodes.BadRequest, ex.Message);
                }

                tunnel.SetTrafficLimits(limits);
                return null;
            }

            default:
                throw new IpcHandlerException(IpcErrorCodes.UnknownMethod, "Unknown method.");
        }
    }

    private async Task<IReadOnlyList<CoreInfo>> CoreInfoAsync(CancellationToken ct)
    {
        var result = new List<CoreInfo>();
        foreach (var (kind, engine) in new[] { (CoreKind.SingBox, singBox), (CoreKind.Xray, xray) })
        {
            try
            {
                string sha;
                using (var binary = catalog.Verify(kind))
                {
                    sha = binary.Sha256;
                }

                var caps = await engine.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                result.Add(new CoreInfo(kind, caps.Version.DisplayVersion, sha, true, null));
            }
            catch (Exception ex) when (ex is CoreIntegrityException or IOException or InvalidOperationException or CoreStartException)
            {
                result.Add(new CoreInfo(kind, "?", string.Empty, false, SecretRedactor.Redact(ex.Message)));
            }
        }

        return result;
    }

    private static T Parse<T>(JsonElement? payload)
    {
        if (payload is null)
        {
            throw new IpcHandlerException(IpcErrorCodes.BadRequest, "Missing payload.");
        }

        try
        {
            return IpcFraming.FromElement<T>(payload) ?? throw new IpcHandlerException(IpcErrorCodes.BadRequest, "Empty payload.");
        }
        catch (JsonException ex)
        {
            throw new IpcHandlerException(IpcErrorCodes.BadRequest, "Malformed payload: " + ex.Message);
        }
    }
}
