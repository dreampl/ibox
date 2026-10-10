using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace JackiVpn.Domain.Common;

/// <summary>Strict validators for network-related strings coming from untrusted sources.</summary>
public static class NetText
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>True when the value contains characters that must never reach a header, argument or config.</summary>
    public static bool HasControlChars(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsIpLiteral(string? value) =>
        !string.IsNullOrEmpty(value) && IPAddress.TryParse(TrimBrackets(value), out _);

    public static string TrimBrackets(string value) =>
        value.Length > 1 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;

    /// <summary>Validates a DNS host name (IDN allowed, converted to punycode for the check).</summary>
    public static bool IsValidHostName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || HasControlChars(value))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(value.TrimEnd('.'));
        }
        catch (ArgumentException)
        {
            return false;
        }

        var labels = ascii.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                // '_' is technically invalid in host names but used by real-world SRV-like records and some CDNs.
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A server address is either an IP literal or a valid host name.</summary>
    public static bool IsValidServerAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && (IsIpLiteral(value) || IsValidHostName(value));

    public static string ToAsciiHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (IsIpLiteral(host))
        {
            return TrimBrackets(host);
        }

        try
        {
            return Idn.GetAscii(host.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return host.ToLowerInvariant();
        }
    }

    public static bool IsValidPort(int port) => port is > 0 and <= 65535;

    /// <summary>
    /// True when <paramref name="host"/> equals one of <paramref name="domains"/> or is a sub-domain of one.
    /// Label-boundary match only: "evil-jacki.ir" or "jacki-inc.ir.evil.com" never match "jacki-inc.ir".
    /// </summary>
    public static bool IsHostInDomains(string? host, IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = ToAsciiHost(host.Trim());
        foreach (var raw in domains)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var d = ToAsciiHost(raw.Trim().TrimStart('*').TrimStart('.'));
            if (h.Equals(d, StringComparison.OrdinalIgnoreCase)
                || (!IsIpLiteral(d) && h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses "a.b.c.d/n", "x::/n" or a bare IP (treated as /32 or /128).</summary>
    public static bool TryParseCidr(string? value, out IPAddress address, out int prefixLength)
    {
        address = IPAddress.None;
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(value) || HasControlChars(value))
        {
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var ipPart = slash >= 0 ? value[..slash] : value;
        if (!IPAddress.TryParse(ipPart.Trim(), out var ip))
        {
            return false;
        }

        var max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max))
        {
            return false;
        }

        address = ip;
        prefixLength = prefix;
        return true;
    }

    /// <summary>A CIDR (or bare IP) that the cores accept.</summary>
    public static bool IsCidr(string? value) => TryParseCidr(value, out _, out _);

    /// <summary>Normalized CIDR text ("10.1.2.3" → "10.1.2.3/32").</summary>
    public static string? NormalizeCidr(string? value) =>
        TryParseCidr(value, out var ip, out var prefix) ? ip + "/" + prefix.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>True when the address lies in one of the CIDRs.</summary>
    public static bool InAnyCidr(IPAddress ip, IEnumerable<string> cidrs)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(cidrs);
        foreach (var c in cidrs)
        {
            if (TryParseCidr(c, out var net, out var prefix) && CidrContains(net, prefix, ip))
            {
                return true;
            }
        }

        return false;
    }

    public static bool CidrContains(IPAddress network, int prefixLength, IPAddress candidate)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.IsIPv4MappedToIPv6 && network.AddressFamily == AddressFamily.InterNetwork)
        {
            candidate = candidate.MapToIPv4();
        }

        if (network.AddressFamily != candidate.AddressFamily)
        {
            return false;
        }

        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        network.TryWriteBytes(a, out var len);
        candidate.TryWriteBytes(b, out _);
        var fullBytes = prefixLength / 8;
        var remBits = prefixLength % 8;
        if (!a[..fullBytes].SequenceEqual(b[..fullBytes]))
        {
            return false;
        }

        if (remBits == 0 || fullBytes >= len)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remBits));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }

    /// <summary>RFC1918, loopback, link-local, CGNAT, ULA, multicast, unspecified, broadcast.</summary>
    public static bool IsPrivateOrLocal(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        foreach (var (net, prefix) in PrivateRanges)
        {
            if (CidrContains(net, prefix, ip))
            {
                return true;
            }
        }

        return false;
    }

    public static readonly IReadOnlyList<string> PrivateCidrs =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/4", "240.0.0.0/4",
        "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
    ];

    private static readonly (IPAddress Net, int Prefix)[] PrivateRanges = PrivateCidrs
        .Select(c => TryParseCidr(c, out var ip, out var p) ? (ip, p) : throw new InvalidOperationException(c))
        .ToArray();
}
