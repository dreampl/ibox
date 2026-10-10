using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JackiVpn.Networking.Adapters;

public sealed record AdapterInfo(string Name, string Description, int IfIndex, OperationalStatus Status, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPAddress> DnsServers, bool IsTunnel);

public sealed record RouteInfo(string Destination, int PrefixLength, string NextHop, int InterfaceIndex, uint Metric, ulong InterfaceLuid);

/// <summary>Read-mostly view of adapters, routes and DNS, used for health checks, E2E verification and repair.</summary>
public static partial class NetworkInspector
{
    public static IReadOnlyList<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var index = InterfaceIndex(nic, props);
            list.Add(new AdapterInfo(
                nic.Name,
                nic.Description,
                index,
                nic.OperationalStatus,
                props.UnicastAddresses.Select(a => a.Address).ToArray(),
                props.DnsAddresses.ToArray(),
                nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel));
        }

        return list;
    }

    /// <summary>IPv4 index, else IPv6 index; adapters without a protocol (IPv6-only, tunnels) must not throw.</summary>
    private static int InterfaceIndex(NetworkInterface nic, IPInterfaceProperties props)
    {
        try
        {
            if (nic.Supports(NetworkInterfaceComponent.IPv4) && props.GetIPv4Properties() is { } v4)
            {
                return v4.Index;
            }
        }
        catch (NetworkInformationException)
        {
        }

        try
        {
            if (nic.Supports(NetworkInterfaceComponent.IPv6) && props.GetIPv6Properties() is { } v6)
            {
                return v6.Index;
            }
        }
        catch (NetworkInformationException)
        {
        }

        return -1;
    }

    public static AdapterInfo? FindAdapter(string name) =>
        Adapters().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>All IPv4/IPv6 routes from the kernel forwarding table (GetIpForwardTable2).</summary>
    public static unsafe IReadOnlyList<RouteInfo> Routes()
    {
        var result = new List<RouteInfo>();
        IntPtr table;
        if (GetIpForwardTable2(0 /* AF_UNSPEC */, &table) != 0)
        {
            return result;
        }

        try
        {
            var count = *(uint*)table;
            var rowSize = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            var first = table + 8; // NumEntries (4) + padding to 8-byte aligned Table[]
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(first + (i * rowSize));
                result.Add(new RouteInfo(
                    ToAddress(row.DestinationPrefix.Prefix)?.ToString() ?? "?",
                    row.DestinationPrefix.PrefixLength,
                    ToAddress(row.NextHop)?.ToString() ?? "?",
                    (int)row.InterfaceIndex,
                    row.Metric,
                    row.InterfaceLuid));
            }
        }
        finally
        {
            FreeMibTable(table);
        }

        return result;
    }

    /// <summary>The interface carrying the IPv4 default route with the lowest metric (the "active" network).</summary>
    public static RouteInfo? DefaultRouteV4(string? excludeAdapterName = null)
    {
        var excluded = excludeAdapterName is null ? null : FindAdapter(excludeAdapterName)?.IfIndex;
        return Routes()
            .Where(r => r.Destination == "0.0.0.0" && r.PrefixLength == 0 && r.InterfaceIndex != excluded)
            .OrderBy(r => r.Metric)
            .FirstOrDefault();
    }

    public static IReadOnlyList<RouteInfo> RoutesOnInterface(int ifIndex) => Routes().Where(r => r.InterfaceIndex == ifIndex).ToArray();

    /// <summary>Flushes the Windows DNS client cache (stale fake-IP or pre-VPN answers).</summary>
    public static bool FlushDnsCache() => DnsFlushResolverCache();

    /// <summary>
    /// Finds the PnP instance id of a network adapter by its connection name (registry, read-only), for
    /// removal of a stale Wintun adapter left behind by a crashed core.
    /// </summary>
    public static string? FindPnpInstanceId(string connectionName)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (root is null)
        {
            return null;
        }

        foreach (var sub in root.GetSubKeyNames())
        {
            using var conn = root.OpenSubKey(sub + @"\Connection");
            if (conn?.GetValue("Name") is string name && string.Equals(name, connectionName, StringComparison.OrdinalIgnoreCase)
                && conn.GetValue("PnPInstanceId") is string id)
            {
                return id;
            }
        }

        return null;
    }

    public static bool IsSafePnpInstanceId(string id) => PnpIdRegex().IsMatch(id);

    private static IPAddress? ToAddress(SOCKADDR_INET a)
    {
        switch (a.si_family)
        {
            case 2:
            {
                var bytes = BitConverter.GetBytes(a.Ipv4Address);
                return new IPAddress(bytes);
            }

            case 23:
            {
                var bytes = new byte[16];
                unsafe
                {
                    for (var i = 0; i < 16; i++)
                    {
                        bytes[i] = a.Ipv6Address[i];
                    }
                }

                return new IPAddress(bytes);
            }

            default:
                return null;
        }
    }

    [GeneratedRegex(@"^(SWD|ROOT)\\[A-Za-z0-9_\-]{1,64}\\[{}A-Za-z0-9_\-&.]{1,128}$")]
    private static partial Regex PnpIdRegex();

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private unsafe struct SOCKADDR_INET
    {
        [FieldOffset(0)]
        public ushort si_family;

        [FieldOffset(4)]
        public uint Ipv4Address;

        [FieldOffset(8)]
        public fixed byte Ipv6Address[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IP_ADDRESS_PREFIX DestinationPrefix;
        public SOCKADDR_INET NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public int Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public int Origin;
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetIpForwardTable2(ushort family, IntPtr* table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr memory);

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
