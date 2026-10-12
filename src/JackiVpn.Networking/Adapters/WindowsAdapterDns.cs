using System.Diagnostics;
using System.Net.NetworkInformation;
using JackiVpn.Domain.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JackiVpn.Networking.Adapters;

/// <summary>
/// DNS servers of Windows network adapters. Reading works as the user; changing them needs administrator rights
/// and runs in the service (netsh, interface index only, IP literals only, arguments never concatenated).
/// </summary>
public static class WindowsAdapterDns
{
    /// <summary>Physical adapters with their DNS servers (no loopback, tunnels or IBOX's own Wintun adapter).</summary>
    public static IReadOnlyList<AdapterDns> List()
    {
        var list = new List<AdapterDns>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsManageable(nic))
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var byHand = StaticServers(nic.Id);
            list.Add(new AdapterDns
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                Kind = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
                    _ => "Other",
                },
                Up = nic.OperationalStatus == OperationalStatus.Up,
                Automatic = byHand.Count == 0,
                Servers = props.DnsAddresses.Where(a => !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal).Select(a => a.ToString()).Distinct().ToList(),
            });
        }

        return list.OrderByDescending(a => a.Up).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Applies a validated change (service, as SYSTEM). IPv4 and IPv6 servers go to their own protocol; a protocol
    /// without servers in the list goes back to automatic.
    /// </summary>
    public static void Apply(AdapterDnsChange change, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(logger);
        var problem = change.Problem() ?? (AdapterDnsChange.WindowsId().IsMatch(change.AdapterId) ? null : "Unknown network adapter.");
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(change));
        }

        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, change.AdapterId, StringComparison.OrdinalIgnoreCase));
        if (nic is null || !IsManageable(nic))
        {
            throw new ArgumentException("Unknown network adapter.", nameof(change));
        }

        var props = nic.GetIPProperties();
        var v4 = change.Servers.Where(s => !s.Contains(':', StringComparison.Ordinal)).ToList();
        var v6 = change.Servers.Where(s => s.Contains(':', StringComparison.Ordinal)).ToList();
        if (nic.Supports(NetworkInterfaceComponent.IPv4))
        {
            SetProtocol("ipv4", props.GetIPv4Properties().Index, v4, logger);
        }

        if (nic.Supports(NetworkInterfaceComponent.IPv6))
        {
            SetProtocol("ipv6", props.GetIPv6Properties().Index, v6, logger);
        }

        NetworkInspector.FlushDnsCache();
    }

    private static void SetProtocol(string protocol, int index, IReadOnlyList<string> servers, ILogger logger)
    {
        var name = "name=" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (servers.Count == 0)
        {
            Netsh(logger, "interface", protocol, "set", "dnsservers", name, "source=dhcp");
            return;
        }

        Netsh(logger, "interface", protocol, "set", "dnsservers", name, "source=static", "address=" + servers[0], "register=primary", "validate=no");
        for (var i = 1; i < servers.Count; i++)
        {
            Netsh(logger, "interface", protocol, "add", "dnsservers", name, "address=" + servers[i], "index=" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "validate=no");
        }
    }

    private static void Netsh(ILogger logger, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("netsh could not be started.");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15_000))
        {
            p.Kill();
            throw new InvalidOperationException("netsh did not finish.");
        }

        logger.LogInformation("netsh {Args} → {Code}", string.Join(' ', args), p.ExitCode);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows refused the DNS change ({p.ExitCode}): {output.Trim()}");
        }
    }

    private static bool IsManageable(NetworkInterface nic) =>
        nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        && !nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(nic.Name, Domain.ProductInfo.TunInterfaceName, StringComparison.OrdinalIgnoreCase)
        && (nic.Supports(NetworkInterfaceComponent.IPv4) || nic.Supports(NetworkInterfaceComponent.IPv6));

    /// <summary>Servers set by hand (registry NameServer); empty when the adapter takes DNS from the network.</summary>
    private static IReadOnlyList<string> StaticServers(string id)
    {
        var result = new List<string>();
        foreach (var root in new[] { @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root + id);
                if (key?.GetValue("NameServer") is string value)
                {
                    result.AddRange(AdapterDnsChange.Parse(value).Where(AdapterDnsChange.IsAddress));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return result;
    }
}
