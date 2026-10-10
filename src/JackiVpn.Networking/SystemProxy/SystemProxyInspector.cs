using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace JackiVpn.Networking.SystemProxy;

public enum SystemProxyHealth
{
    /// <summary>No manual system proxy (or only PAC / auto-detect).</summary>
    None = 0,

    /// <summary>The proxy is this app's own local inbound.</summary>
    Ours,

    /// <summary>Another program's loopback proxy that is accepting connections.</summary>
    ForeignLive,

    /// <summary>A loopback proxy where nothing listens: every app honouring the system proxy fails.</summary>
    ForeignDead,

    /// <summary>A proxy on another host (corporate etc.); not judged.</summary>
    Remote,
}

public sealed record SystemProxyFinding(SystemProxyHealth Health, string? Server, int? Port);

/// <summary>
/// Read-only check of the Windows (WinINET) system proxy. Explains a classic failure: another VPN client set
/// <c>127.0.0.1:port</c> and exited without restoring it — browsers, Electron apps and anything that copied the
/// proxy into HTTPS_PROXY (Claude Desktop does) then fail with network errors although the tunnel works.
/// </summary>
public static class SystemProxyInspector
{
    public static async Task<SystemProxyFinding> InspectAsync(IWinInetProxyApi api, int? ourPort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        var lan = api.Query(null);
        if (!lan.ProxyEnabled || string.IsNullOrWhiteSpace(lan.ProxyServer))
        {
            return new SystemProxyFinding(SystemProxyHealth.None, null, null);
        }

        if (!TryParseLoopback(lan.ProxyServer, out var port))
        {
            return new SystemProxyFinding(SystemProxyHealth.Remote, lan.ProxyServer, null);
        }

        if (ourPort == port)
        {
            return new SystemProxyFinding(SystemProxyHealth.Ours, lan.ProxyServer, port);
        }

        var live = await IsListeningAsync(port, ct).ConfigureAwait(false);
        return new SystemProxyFinding(live ? SystemProxyHealth.ForeignLive : SystemProxyHealth.ForeignDead, lan.ProxyServer, port);
    }

    /// <summary>
    /// Turns a dead loopback proxy off on the LAN connection (user-initiated only). Bypass list and PAC settings
    /// are kept; only the manual proxy flag is cleared.
    /// </summary>
    public static void DisableManualProxy(IWinInetProxyApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var lan = api.Query(null);
        api.Apply(lan with { Flags = (lan.Flags & ~ProxySnapshot.FlagProxy) | ProxySnapshot.FlagDirect });
        api.NotifyChanged();
    }

    /// <summary>Accepts "127.0.0.1:3067", "localhost:3067", "http=127.0.0.1:3067;https=127.0.0.1:3067".</summary>
    public static bool TryParseLoopback(string proxyServer, out int port)
    {
        ArgumentNullException.ThrowIfNull(proxyServer);
        port = 0;
        foreach (var raw in proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.Contains('=', StringComparison.Ordinal) ? raw[(raw.IndexOf('=', StringComparison.Ordinal) + 1)..] : raw;
            if (entry.Contains("://", StringComparison.Ordinal))
            {
                entry = entry[(entry.IndexOf("://", StringComparison.Ordinal) + 3)..];
            }

            var colon = entry.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(entry[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                continue;
            }

            var host = entry[..colon].Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)))
            {
                port = p;
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(600));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
