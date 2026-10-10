using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using JackiVpn.Application.Usage;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// The physical network Windows uses for the internet: DNS suffix, Active Directory domain, gateway (IP + MAC
/// from the ARP cache), resolvers, local subnets and the Wi-Fi name. Read-only; nothing is changed.
/// </summary>
public sealed partial class WindowsNetworkFacts : INetworkFactsProvider
{
    public NetworkFacts Read()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && DotNetNicCounters.IsPhysical(n.Name, n.Description))
            .Select(n => (Nic: n, Props: n.GetIPProperties()))
            .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .OrderBy(x => x.Props.GetIPv4Properties()?.Index ?? int.MaxValue)
            .FirstOrDefault();
        if (nic.Nic is null)
        {
            return NetworkFacts.None;
        }

        var props = nic.Props;
        var gateway = props.GatewayAddresses.Select(g => g.Address).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var mac = GatewayMac(gateway);
        var suffix = string.IsNullOrWhiteSpace(props.DnsSuffix) ? null : props.DnsSuffix.Trim().ToLowerInvariant();
        string? ad = null;
        try
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            ad = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant();
        }
        catch (NetworkInformationException)
        {
        }

        var wifi = nic.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        var ssid = wifi ? Ssid() : null;
        var cidrs = props.UnicastAddresses
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is > 0 and < 32)
            .Select(u => Network(u.Address, u.PrefixLength))
            .ToList();
        return new NetworkFacts
        {
            Key = WorkplaceService.KeyOf(mac, gateway.ToString(), suffix, ssid),
            Ssid = ssid,
            DnsSuffix = suffix,
            AdDomain = ad,
            GatewayIp = gateway.ToString(),
            GatewayMac = mac,
            DnsServers = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
            LocalCidrs = cidrs,
            IsWifi = wifi,
        };
    }

    private static string Network(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var v = (((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]) & mask;
        return $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}/{prefix}";
    }

    private static string? GatewayMac(IPAddress gateway)
    {
        var mac = new byte[6];
        var len = (uint)mac.Length;
        var dest = BitConverter.ToUInt32(gateway.GetAddressBytes(), 0);
        return SendARP(dest, 0, mac, ref len) == 0 && len == 6 ? string.Join(':', mac.Select(x => x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))) : null;
    }

    /// <summary>The connected Wi-Fi name (netsh; no location permission involved on Windows 10/11 desktop apps).</summary>
    private static string? Ssid()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("SSID BSSID", StringComparison.OrdinalIgnoreCase) && t.IndexOf(':') is var colon and > 0)
                {
                    var name = t[(colon + 1)..].Trim();
                    return name.Length is > 0 and <= 64 ? name : null;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(uint destIp, uint srcIp, [Out] byte[] macAddr, ref uint physicalAddrLen);
}
