using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Linux;

/// <summary>
/// Network facts on Linux, read from the kernel and the resolver: default gateway (/proc/net/route), its MAC
/// (/proc/net/arp), DNS search domain and resolvers (/etc/resolv.conf, or systemd-resolved's real upstream file),
/// local subnets and — when NetworkManager is there — the Wi-Fi name. Our own TUN is ignored.
/// </summary>
public sealed class LinuxNetworkFacts : INetworkFactsProvider
{
    public NetworkFacts Read()
    {
        var (gateway, device) = DefaultRoute();
        if (gateway is null)
        {
            return NetworkFacts.None;
        }

        var mac = GatewayMac(gateway);
        var (suffix, dns) = Resolver();
        var ssid = device is not null && IsWireless(device) ? Ssid() : null;
        return new NetworkFacts
        {
            Key = WorkplaceService.KeyOf(mac, gateway, suffix, ssid),
            Ssid = ssid,
            DnsSuffix = suffix,
            GatewayIp = gateway,
            GatewayMac = mac,
            DnsServers = dns,
            LocalCidrs = LocalCidrs(device),
            IsWifi = ssid is not null,
        };
    }

    /// <summary>The default route that is not a VPN device (lowest metric).</summary>
    private static (string? Gateway, string? Device) DefaultRoute()
    {
        try
        {
            string? best = null, bestDevice = null;
            var bestMetric = int.MaxValue;
            foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
            {
                var f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (f.Length < 8 || f[1] != "00000000" || f[0].StartsWith("tun", StringComparison.Ordinal) || f[0].StartsWith("wg", StringComparison.Ordinal)
                    || f[0].Contains("jacki", StringComparison.OrdinalIgnoreCase) || f[0].Contains("ibox", StringComparison.OrdinalIgnoreCase) || f[0].StartsWith("sing", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!uint.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gw) || gw == 0
                    || !int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var metric) || metric >= bestMetric)
                {
                    continue;
                }

                bestMetric = metric;
                best = new IPAddress(gw).ToString(); // little-endian in the file = network order in memory
                bestDevice = f[0];
            }

            return (best, bestDevice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static string? GatewayMac(string gateway)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 4 && f[0] == gateway && f[3] != "00:00:00:00:00:00")
                {
                    return f[3].ToUpperInvariant();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Search domain and resolvers; with systemd-resolved the stub (127.0.0.53) hides them, so its upstream file is read.</summary>
    private static (string? Suffix, IReadOnlyList<string> Dns) Resolver()
    {
        foreach (var path in new[] { "/run/systemd/resolve/resolv.conf", "/etc/resolv.conf" })
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                string? suffix = null;
                var dns = new List<string>();
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("search ", StringComparison.Ordinal) || line.StartsWith("domain ", StringComparison.Ordinal))
                    {
                        suffix ??= line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault(d => d != ".")?.Trim('.').ToLowerInvariant();
                    }
                    else if (line.StartsWith("nameserver ", StringComparison.Ordinal) && IPAddress.TryParse(line[11..].Trim(), out var ip) && !IPAddress.IsLoopback(ip))
                    {
                        dns.Add(ip.ToString());
                    }
                }

                if (dns.Count > 0 || suffix is not null)
                {
                    return (suffix, dns);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (null, []);
    }

    private static IReadOnlyList<string> LocalCidrs(string? device)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => device is null || n.Name == device)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => Cidr(a.Address, a.PrefixLength))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static string Cidr(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        var v = (((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]) & (prefix == 0 ? 0u : uint.MaxValue << (32 - prefix));
        return $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}/{prefix}";
    }

    private static bool IsWireless(string device) => Directory.Exists($"/sys/class/net/{device}/wireless");

    /// <summary>Active Wi-Fi name from NetworkManager (absent elsewhere: then only gateway and DNS identify the network).</summary>
    private static string? Ssid()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("nmcli") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-t", "-f", "active,ssid", "dev", "wifi" })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Split('\n').FirstOrDefault(l => l.StartsWith("yes:", StringComparison.Ordinal))?[4..].Trim() is { Length: > 0 } s ? s : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
