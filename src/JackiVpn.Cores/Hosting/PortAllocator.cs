using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace JackiVpn.Cores.Hosting;

/// <summary>Chooses free loopback TCP ports for core inbounds and APIs.</summary>
public static class PortAllocator
{
    public static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static IReadOnlyList<int> GetFreeLoopbackPorts(int count)
    {
        // Hold all listeners until every port is chosen so the same port is never returned twice.
        var listeners = new List<TcpListener>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                listeners.Add(l);
            }

            return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var l in listeners)
            {
                l.Stop();
            }
        }
    }

    /// <summary>
    /// True when a loopback listener cannot be created on the port. A bind test works everywhere (Android 10+
    /// forbids reading /proc/net, which the listener table relies on) and also catches 0.0.0.0 listeners.
    /// </summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = OperatingSystem.IsWindows() };
            l.Start();
            l.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>Random secret for the core's local control API.</summary>
    public static string NewApiSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}
