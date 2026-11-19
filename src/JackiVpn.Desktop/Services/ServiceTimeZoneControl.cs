using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Application.Privacy;
using JackiVpn.Ipc;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Changing the Windows time zone needs the time-zone privilege: the service (LocalSystem) does it and journals the
/// original, so a crash, a service restart or an uninstall puts the user's zone back.
/// </summary>
public sealed class ServiceTimeZoneControl(ServiceControl service) : ITimeZoneControl
{
    public async Task ApplyAsync(string ianaId, CancellationToken ct)
    {
        var client = await service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SendAsync(IpcMethods.ApplyTimeZone, IpcFraming.ToElement(new TimeZoneApplyRequest(ianaId)), ct).ConfigureAwait(false);
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        IpcClient client;
        try
        {
            client = await service.GetClientAsync(ct).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException)
        {
            // No service: nothing can have been changed through it in this run (a stopped service restored on stop).
            return;
        }

        try
        {
            await client.SendAsync(IpcMethods.RestoreTimeZone, null, ct).ConfigureAwait(false);
        }
        catch (IpcRemoteException ex) when (ex.Code == IpcErrorCodes.UnknownMethod)
        {
            // An older service never changed the time zone.
        }
    }
}
