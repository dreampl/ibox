using System.Net;
using System.Text.RegularExpressions;

namespace JackiVpn.Domain.Network;

/// <summary>One network adapter and the DNS servers the operating system uses on it.</summary>
public sealed record AdapterDns
{
    /// <summary>Windows: the interface GUID ({…}); Linux: the device name (wlan0).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Wi-Fi, Ethernet or Other.</summary>
    public string Kind { get; init; } = "Other";

    public bool Up { get; init; }

    /// <summary>True when the DNS servers come from the network (DHCP / router), false when set by hand.</summary>
    public bool Automatic { get; init; } = true;

    /// <summary>The servers in use now (from the network or set by hand).</summary>
    public IReadOnlyList<string> Servers { get; init; } = [];

    /// <summary>Linux: the NetworkManager connection the device uses (DNS is stored on it).</summary>
    public string? Connection { get; init; }
}

/// <summary>New DNS servers for one adapter; an empty list means "automatic (from the network)".</summary>
public sealed partial record AdapterDnsChange(string AdapterId, IReadOnlyList<string> Servers)
{
    public const int MaxServers = 4;

    /// <summary>Null when the change is safe to apply, else what is wrong (checked again by the privileged side).</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(AdapterId) || !(WindowsId().IsMatch(AdapterId) || LinuxDevice().IsMatch(AdapterId)))
        {
            return "Unknown network adapter.";
        }

        if (Servers is null || Servers.Count > MaxServers)
        {
            return $"Up to {MaxServers} DNS servers per adapter.";
        }

        foreach (var s in Servers)
        {
            if (!IsAddress(s))
            {
                return $"'{s}' is not an IP address.";
            }
        }

        return Servers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Servers.Count ? "The same server is listed twice." : null;
    }

    /// <summary>A plain IPv4 (four parts) or IPv6 literal, normalized form only (no scope, no ports).</summary>
    public static bool IsAddress(string? s) =>
        s is { Length: > 0 and <= 45 }
        && IPAddress.TryParse(s, out var ip)
        && !s.Contains('%', StringComparison.Ordinal)
        && (s.Contains(':', StringComparison.Ordinal) ? ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 : s.Count(c => c == '.') == 3);

    /// <summary>Splits "1.1.1.1, 8.8.8.8" (commas, spaces or semicolons).</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        (text ?? string.Empty).Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    public static partial Regex WindowsId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.@-]{0,14}$")]
    public static partial Regex LinuxDevice();
}

/// <summary>Network adapter names as the OS shows them ("Ethernet 2", "OpenVPN Connect DCO Adapter", "tun0").</summary>
public static partial class AdapterNames
{
    public static bool IsValid(string? name) => name is { Length: > 0 and <= 64 } && Friendly().IsMatch(name) && name.Trim() == name;

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} _.@#()+\-]*$")]
    private static partial Regex Friendly();
}
