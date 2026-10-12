using System;
using System.Collections.Generic;
using System.Threading;
using JackiVpn.Networking.SystemProxy;

namespace JackiVpn.Desktop.Composition;

/// <summary>
/// In-memory Windows proxy for the developer sandbox (<see cref="AppServices.Sandboxed"/>): the app applies and
/// restores "the system proxy" exactly as usual, but nothing outside this process ever sees it.
/// </summary>
internal sealed class SandboxProxyApi : IWinInetProxyApi
{
    private readonly Lock _gate = new();
    private ProxySnapshot _lan = new();

    public ProxySnapshot Query(string? connectionName)
    {
        lock (_gate)
        {
            return _lan with { ConnectionName = connectionName };
        }
    }

    public void Apply(ProxySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ConnectionName is null)
        {
            lock (_gate)
            {
                _lan = snapshot;
            }
        }
    }

    public void NotifyChanged()
    {
    }

    public IReadOnlyList<string> EnumerateRasConnections() => [];
}
