using System.Net.NetworkInformation;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Application.Workplace;

/// <summary>
/// Dual VPN for a workplace (Windows, Linux, and the server route on Android): away from the office, Work mode sends
/// the company's names and subnets through the work VPN, and everything else through the normal one.
/// </summary>
public static class DualVpn
{
    /// <summary>One line of the editor: the company's names, subnets and resolvers (comma, space or line separated).</summary>
    public static IReadOnlyList<string> Split(string? text) =>
        (text ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The edited workplace, or why not. A signed company policy keeps its own names and subnets.</summary>
    public static (WorkplaceProfile? Profile, string? Problem) Apply(
        WorkplaceProfile profile, DualVpnSettings dual, string? domains, string? cidrs, string? dns)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(dual);
        if (dual.Route == WorkRoute.Server && string.IsNullOrWhiteSpace(dual.ServerId))
        {
            return (null, "Choose the server that carries company traffic.");
        }

        if (dual.Route == WorkRoute.Adapter && !Domain.Network.AdapterNames.IsValid(dual.Interface?.Trim()))
        {
            return (null, "Choose the network adapter of the company's VPN app.");
        }

        dual = dual with { Interface = dual.Route == WorkRoute.Adapter ? dual.Interface!.Trim() : null, ServerId = dual.Route == WorkRoute.Server ? dual.ServerId : null };
        if (profile.PolicyKeySha256 is not null)
        {
            return (profile with { DualVpn = dual }, null);
        }

        var names = Split(domains).Select(d => d.TrimStart('*', '.').TrimEnd('.').ToLowerInvariant()).ToList();
        if (names.FirstOrDefault(d => !NetText.IsValidHostName(d)) is { } badName)
        {
            return (null, $"\"{badName}\" is not a domain name.");
        }

        var subnets = Split(cidrs).Select(c => NetText.IsIpLiteral(c) ? c + (c.Contains(':', StringComparison.Ordinal) ? "/128" : "/32") : c).ToList();
        if (subnets.FirstOrDefault(c => !NetText.IsCidr(c)) is { } badCidr)
        {
            return (null, $"\"{badCidr}\" is not a subnet (like 10.20.0.0/16).");
        }

        var resolvers = Split(dns);
        if (resolvers.FirstOrDefault(d => !NetText.IsIpLiteral(d)) is { } badDns)
        {
            return (null, $"\"{badDns}\" is not an IP address.");
        }

        if (dual.Route != WorkRoute.Off && names.Count == 0 && subnets.Count == 0)
        {
            return (null, "Add the company's internal names or subnets: only they take the work VPN.");
        }

        if (names.Count > 500 || subnets.Count > 500 || resolvers.Count > 8)
        {
            return (null, "Too many entries.");
        }

        return (profile with { DualVpn = dual, InternalDomains = names, InternalCidrs = subnets, InternalDns = resolvers }, null);
    }

    /// <summary>Adapters a company VPN app may have brought up (its own tunnel first), for the adapter route.</summary>
    public static IReadOnlyList<string> Adapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && Domain.Network.AdapterNames.IsValid(n.Name))
                .Where(n => !n.Name.StartsWith("jacki", StringComparison.OrdinalIgnoreCase) && !n.Name.StartsWith("ibox", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .ThenByDescending(LooksLikeVpn)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => n.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool LooksLikeVpn(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
        || new[] { "tun", "tap", "vpn", "ppp", "wg", "openvpn", "anyconnect", "forti", "globalprotect", "pangp", "cisco", "wireguard" }
            .Any(k => n.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line for lists: what Dual VPN does for this workplace.</summary>
    public static string Describe(WorkplaceProfile profile, Func<string, string?> serverName)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(serverName);
        return profile.DualVpn.Route switch
        {
            WorkRoute.Server when profile.DualVpn.IsOn => "Dual VPN: company traffic via " + (serverName(profile.DualVpn.ServerId!) ?? "a missing server"),
            WorkRoute.Adapter when profile.DualVpn.IsOn => "Dual VPN: company traffic via the adapter " + profile.DualVpn.Interface,
            _ => "Dual VPN off",
        };
    }
}
