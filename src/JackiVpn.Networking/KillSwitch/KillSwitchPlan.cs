using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using JackiVpn.Domain.Common;

namespace JackiVpn.Networking.KillSwitch;

public enum WfpLayer
{
    ConnectV4,
    ConnectV6,
    RecvAcceptV4,
    RecvAcceptV6,
}

public enum FilterAction
{
    Permit,
    Block,
}

public abstract record FilterCondition;

public sealed record LoopbackCondition : FilterCondition;

public sealed record AppIdCondition(string ExecutablePath) : FilterCondition;

public sealed record LocalAddressCondition(IPAddress Network, int PrefixLength) : FilterCondition;

public sealed record RemoteAddressCondition(IPAddress Network, int PrefixLength) : FilterCondition;

public sealed record ProtocolCondition(byte Protocol) : FilterCondition;

public sealed record LocalPortCondition(ushort Port) : FilterCondition;

public sealed record RemotePortCondition(ushort Port) : FilterCondition;

/// <summary>ICMP type is carried in the local-port field at ALE layers.</summary>
public sealed record IcmpTypeCondition(ushort Type) : FilterCondition;

public sealed record FilterSpec(int Index, string Name, WfpLayer Layer, FilterAction Action, byte Weight, IReadOnlyList<FilterCondition> Conditions)
{
    public Guid Key => KillSwitchPlan.FilterKey(Index);
}

public sealed record KillSwitchOptions
{
    public bool AllowLan { get; init; } = true;

    /// <summary>Core executables allowed to reach the network directly (to reach VPN servers).</summary>
    public IReadOnlyList<string> AllowedApplications { get; init; } = [];

    /// <summary>TUN interface subnets; traffic sourced from these is inside the tunnel.</summary>
    public string TunV4Cidr { get; init; } = "172.19.0.0/30";

    public string TunV6Cidr { get; init; } = "fdfe:dcba:9876::/126";
}

/// <summary>
/// Pure description of the kill-switch WFP filters (testable without admin). Everything not explicitly
/// permitted is blocked at the ALE connect/accept layers for IPv4 and IPv6:
/// loopback, allowed core executables, traffic sourced from the TUN subnet, DHCP/DHCPv6, NDP and — if
/// "Allow LAN" is on — private/link-local/multicast destinations. DNS to anything outside the tunnel is
/// therefore blocked as well.
/// </summary>
public static class KillSwitchPlan
{
    public const int MaxFilters = 128;

    public static readonly Guid ProviderKey = new("6b1f4e8a-7c35-4f5e-9d2a-4a1c7e3b9f10");
    public static readonly Guid SublayerKey = new("6b1f4e8a-7c35-4f5e-9d2a-4a1c7e3b9f11");
    public const ushort SublayerWeight = 0xFFFF;

    private const byte PermitWeight = 15;
    private const byte PermitLanWeight = 13;
    private const byte BlockWeight = 0;

    public static IReadOnlyList<FilterSpec> Build(KillSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var list = new List<FilterSpec>();
        var i = 0;
        void Add(string name, WfpLayer layer, FilterAction action, byte weight, params FilterCondition[] conditions) =>
            list.Add(new FilterSpec(i++, name, layer, action, weight, conditions));

        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            Add("Permit loopback", layer, FilterAction.Permit, PermitWeight, new LoopbackCondition());
        }

        foreach (var app in options.AllowedApplications.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var layer in Enum.GetValues<WfpLayer>())
            {
                Add($"Permit {Path.GetFileName(app)}", layer, FilterAction.Permit, PermitWeight, new AppIdCondition(app));
            }
        }

        if (NetText.TryParseCidr(options.TunV4Cidr, out var tun4, out var tun4Len))
        {
            Add("Permit tunnel v4", WfpLayer.ConnectV4, FilterAction.Permit, PermitWeight, new LocalAddressCondition(tun4, tun4Len));
            Add("Permit tunnel v4 inbound", WfpLayer.RecvAcceptV4, FilterAction.Permit, PermitWeight, new LocalAddressCondition(tun4, tun4Len));
        }

        if (NetText.TryParseCidr(options.TunV6Cidr, out var tun6, out var tun6Len))
        {
            Add("Permit tunnel v6", WfpLayer.ConnectV6, FilterAction.Permit, PermitWeight, new LocalAddressCondition(tun6, tun6Len));
            Add("Permit tunnel v6 inbound", WfpLayer.RecvAcceptV6, FilterAction.Permit, PermitWeight, new LocalAddressCondition(tun6, tun6Len));
        }

        // DHCP (keeps the physical link alive so the VPN can reconnect).
        Add("Permit DHCP", WfpLayer.ConnectV4, FilterAction.Permit, PermitWeight, new ProtocolCondition(17), new LocalPortCondition(68), new RemotePortCondition(67));
        Add("Permit DHCP inbound", WfpLayer.RecvAcceptV4, FilterAction.Permit, PermitWeight, new ProtocolCondition(17), new LocalPortCondition(68), new RemotePortCondition(67));
        Add("Permit DHCPv6", WfpLayer.ConnectV6, FilterAction.Permit, PermitWeight, new ProtocolCondition(17), new LocalPortCondition(546), new RemotePortCondition(547));
        Add("Permit DHCPv6 inbound", WfpLayer.RecvAcceptV6, FilterAction.Permit, PermitWeight, new ProtocolCondition(17), new LocalPortCondition(546), new RemotePortCondition(547));

        // IPv6 neighbor discovery (router/neighbor solicitation/advertisement, redirect).
        foreach (ushort type in new ushort[] { 133, 134, 135, 136, 137 })
        {
            Add($"Permit NDP {type}", WfpLayer.ConnectV6, FilterAction.Permit, PermitWeight, new ProtocolCondition(58), new IcmpTypeCondition(type));
            Add($"Permit NDP {type} inbound", WfpLayer.RecvAcceptV6, FilterAction.Permit, PermitWeight, new ProtocolCondition(58), new IcmpTypeCondition(type));
        }

        if (options.AllowLan)
        {
            foreach (var cidr in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32" })
            {
                NetText.TryParseCidr(cidr, out var net, out var len);
                Add($"Permit LAN {cidr}", WfpLayer.ConnectV4, FilterAction.Permit, PermitLanWeight, new RemoteAddressCondition(net, len));
                Add($"Permit LAN {cidr} inbound", WfpLayer.RecvAcceptV4, FilterAction.Permit, PermitLanWeight, new RemoteAddressCondition(net, len));
            }

            foreach (var cidr in new[] { "fe80::/10", "fc00::/7", "ff00::/8" })
            {
                NetText.TryParseCidr(cidr, out var net, out var len);
                Add($"Permit LAN {cidr}", WfpLayer.ConnectV6, FilterAction.Permit, PermitLanWeight, new RemoteAddressCondition(net, len));
                Add($"Permit LAN {cidr} inbound", WfpLayer.RecvAcceptV6, FilterAction.Permit, PermitLanWeight, new RemoteAddressCondition(net, len));
            }
        }

        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            Add("Block all", layer, FilterAction.Block, BlockWeight);
        }

        if (list.Count > MaxFilters)
        {
            throw new InvalidOperationException($"Kill switch plan has {list.Count} filters (max {MaxFilters}).");
        }

        return list;
    }

    /// <summary>Deterministic filter keys: recovery can always delete every filter without any saved state.</summary>
    public static Guid FilterKey(int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("JackiVpn.KillSwitch.Filter/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var g = bytes.AsSpan(0, 16).ToArray();
        g[7] = (byte)((g[7] & 0x0F) | 0x50); // version 5-style
        g[8] = (byte)((g[8] & 0x3F) | 0x80);
        return new Guid(g);
    }

    public static bool IsV6(WfpLayer layer) => layer is WfpLayer.ConnectV6 or WfpLayer.RecvAcceptV6;

    public static bool MatchesFamily(IPAddress address, WfpLayer layer) =>
        (address.AddressFamily == AddressFamily.InterNetworkV6) == IsV6(layer);
}
