using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Application.Connectivity;
using JackiVpn.Domain.Network;
using JackiVpn.Ipc;
using JackiVpn.Networking.Adapters;

namespace JackiVpn.Desktop.Services;

/// <summary>Adapters are read here (as the user); changes go to the service, which runs as administrator.</summary>
public sealed class WindowsAdapterDnsControl(ServiceControl service) : IAdapterDnsControl
{
    public Task<IReadOnlyList<AdapterDns>> ListAsync(CancellationToken ct) => Task.Run(WindowsAdapterDns.List, ct);

    public async Task SetAsync(AdapterDnsChange change, CancellationToken ct)
    {
        IpcClient client;
        try
        {
            client = await service.GetClientAsync(ct).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException ex)
        {
            throw new System.InvalidOperationException("The IBOX service is needed to change adapter DNS (it runs as administrator). Install or repair it on the Network page.", ex);
        }

        try
        {
            await client.SendAsync(IpcMethods.SetAdapterDns, IpcFraming.ToElement(change), ct).ConfigureAwait(false);
        }
        catch (IpcRemoteException ex) when (ex.Code == IpcErrorCodes.UnknownMethod)
        {
            throw new System.InvalidOperationException("The installed IBOX service is older than this app: repair the service on the Network page.", ex);
        }
    }
}
