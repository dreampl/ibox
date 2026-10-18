using System.Net;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Connectivity;

/// <summary>
/// Addresses our own tunnel hands out. While a TUN session with FakeIP runs, the app's own lookups (subscription and
/// rule-set downloads) get FakeIP answers; the tunnel connects those by the original name, so they are not local
/// addresses even though the range looks reserved.
/// </summary>
public static class TunnelAddresses
{
    public static Func<IPAddress, bool> FakeIp(Func<AppSettings> settings, ConnectionOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(orchestrator);
        return address =>
        {
            var s = settings();
            return s.Dns.FakeIpEnabled
                && s.Connection.Mode == ConnectionMode.Tun
                && orchestrator.State is not (ConnectionState.Disconnected or ConnectionState.Faulted)
                && NetText.InAnyCidr(address, [s.Dns.FakeIpV4Range, s.Dns.FakeIpV6Range]);
        };
    }
}
