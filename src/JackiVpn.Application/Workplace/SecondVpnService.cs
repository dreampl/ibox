using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Usage;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Workplace;

/// <summary>The system's own VPN client (IPsec), per platform: Windows RAS, NetworkManager on Linux.</summary>
public interface ISecondVpnSystem
{
    /// <summary>Creates (or replaces) the IPsec connection; split tunnel, routes for the company subnets. Null = done.</summary>
    Task<string?> SetUpIpsecAsync(string entry, string host, IpsecType type, string? psk, string? user, string? password, IReadOnlyList<string> cidrs, CancellationToken ct);

    /// <summary>Dials it; returns the problem, or null when it is up.</summary>
    Task<string?> DialAsync(string entry, string? user, string? password, CancellationToken ct);

    Task HangUpAsync(string entry, CancellationToken ct);

    Task RemoveAsync(string entry, CancellationToken ct);

    /// <summary>The network adapter the connection uses once up (null when it has none, e.g. policy-based IKEv2).</summary>
    string? AdapterOf(string entry);

    /// <summary>Full tunnel (all traffic: the Dual VPN alone) or split (only its routes); applies on the next dial.</summary>
    Task<string?> SetFullTunnelAsync(string entry, bool full, CancellationToken ct);
}

/// <summary>What the editor hands over (plain text; secrets are encrypted before they are stored).</summary>
public sealed record SecondVpnDraft
{
    public string Name { get; init; } = "Company VPN";
    public SecondVpnKind Kind { get; init; }
    public string? WireGuardConfig { get; init; }
    public string? ServerId { get; init; }
    public string? Interface { get; init; }
    public string? Host { get; init; }
    public IpsecType IpsecType { get; init; }
    public string? Username { get; init; }

    /// <summary>Null keeps the stored one.</summary>
    public string? Password { get; init; }

    /// <summary>Null keeps the stored one.</summary>
    public string? PreSharedKey { get; init; }

    public string? Domains { get; init; }
    public string? Cidrs { get; init; }
    public string? Dns { get; init; }
    public SecondVpnScope Scope { get; init; }
}

/// <summary>A wg-quick configuration ([Interface] + [Peer]) as an IBOX server, plus what it says about the network.</summary>
public static class WireGuardConf
{
    public sealed record Result(ProxyProfile Profile, IReadOnlyList<string> Subnets, IReadOnlyList<string> Dns, IReadOnlyList<string> SearchDomains);

    public static (Result? Value, string? Problem) Parse(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 * 1024)
        {
            return (null, "Paste the WireGuard configuration (the .conf file from the company or the server).");
        }

        var section = string.Empty;
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = section + "." + line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            if (!values.TryGetValue(key, out var list))
            {
                values[key] = list = [];
            }

            // A second [Peer] would add a second endpoint: only the first peer is used.
            if (list.Count == 0 || key is "interface.address" or "interface.dns" or "peer.allowedips")
            {
                list.Add(value);
            }
        }

        string? One(string key) => values.TryGetValue(key, out var v) ? v[0] : null;
        IEnumerable<string> Many(string key) => values.TryGetValue(key, out var v)
            ? v.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : [];

        var endpoint = One("peer.endpoint");
        if (One("interface.privatekey") is not { } privateKey || One("peer.publickey") is not { } publicKey || endpoint is null)
        {
            return (null, "This is not a WireGuard configuration: it needs [Interface] PrivateKey and [Peer] PublicKey and Endpoint.");
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || !NetText.IsValidPort(port))
        {
            return (null, "The [Peer] Endpoint must be host:port.");
        }

        var host = NetText.TrimBrackets(endpoint[..colon]);
        var addresses = Many("interface.address").Select(a => a.Contains('/', StringComparison.Ordinal) ? a : a + (a.Contains(':', StringComparison.Ordinal) ? "/128" : "/32")).ToList();
        var allowed = Many("peer.allowedips").Where(NetText.IsCidr).ToList();
        var dnsEntries = Many("interface.dns").ToList();
        var profile = new ProxyProfile
        {
            Id = "wireguard-import", // replaced by the caller
            Name = name,
            Protocol = ProxyProtocol.WireGuard,
            Server = host,
            Port = port,
            WireGuard = new WireGuardOptions
            {
                PrivateKey = privateKey,
                PeerPublicKey = publicKey,
                PreSharedKey = One("peer.presharedkey"),
                LocalAddresses = addresses,
                Mtu = int.TryParse(One("interface.mtu"), NumberStyles.None, CultureInfo.InvariantCulture, out var mtu) ? mtu : null,
                PersistentKeepaliveSeconds = int.TryParse(One("peer.persistentkeepalive"), NumberStyles.None, CultureInfo.InvariantCulture, out var ka) ? ka : null,
            },
        };
        var check = ProfileValidator.Validate(profile);
        if (!check.IsValid)
        {
            return (null, "The WireGuard configuration is not valid: " + string.Join("; ", check.Errors.Select(e => e.Message)));
        }

        // Full-tunnel configs (0.0.0.0/0) say nothing about the company's subnets.
        var subnets = allowed.Where(c => !c.StartsWith("0.0.0.0/", StringComparison.Ordinal) && !c.StartsWith("::/", StringComparison.Ordinal)).ToList();
        return (new Result(profile, subnets, dnsEntries.Where(NetText.IsIpLiteral).ToList(), dnsEntries.Where(d => !NetText.IsIpLiteral(d) && NetText.IsValidHostName(d)).ToList()), null);
    }
}

/// <summary>
/// The standalone Dual VPN: a second VPN to a company or a server, with its own on/off. On alone it carries all
/// traffic (a WireGuard config or a saved server as IBOX's connection; IPsec dialled as a full tunnel); with the main
/// VPN on too, the traffic is split — only the company's part (smart, or the names and subnets listed) takes the
/// second VPN. Kerio and other company VPN apps keep their own app and IBOX uses their adapter.
/// </summary>
public sealed class SecondVpnService(SettingsStore settings, IProfileRepository profiles, ISecretProtector protector, ISecondVpnSystem? system, ILogger logger)
{
    private const string Purpose = "dual-vpn";

    /// <summary>Marks the server a WireGuard config was saved as (so a new config replaces it, and lists can tell).</summary>
    public const string OwnedTag = "dual-vpn";

    public SecondVpnSettings Current => settings.Current.DualVpn;

    /// <summary>Raised after a change (on the caller's thread).</summary>
    public event EventHandler? Changed;

    /// <summary>IPsec here (Windows, Linux with NetworkManager); not on Android.</summary>
    public bool SupportsIpsec => system is not null;

    private ConnectionOrchestrator? _orchestrator;
    private bool? _ipsecFull;

    /// <summary>
    /// Connects this to the connection: when the user turns the main VPN off while the Dual VPN is on, the Dual VPN
    /// takes over everything; before the main VPN comes on, an IPsec second VPN switches to split tunnel.
    /// </summary>
    public void Attach(ConnectionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        orchestrator.UserDisconnected += (_, wasDualOnly) =>
        {
            if (!wasDualOnly && Current is { Enabled: true, IsConfigured: true })
            {
                _ = Task.Run(() => RunAloneAsync(CancellationToken.None));
            }
            else
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>True while the Dual VPN runs on its own and carries everything.</summary>
    public bool IsAlone => Current is { Enabled: true, IsConfigured: true } d && (d.Route == WorkRoute.Server
        ? _orchestrator?.Status is { State: ConnectionState.Connected, DualOnly: true }
        : !MainOn && AdapterUp(d.Interface));

    /// <summary>The main VPN is on (not just the Dual VPN alone).</summary>
    private bool MainOn => _orchestrator?.Status is { DualOnly: false } s && !ConnectionStateRules.IsIdle(s.State);

    /// <summary>The Dual VPN alone: its server becomes the connection (all traffic), or IPsec dials as a full tunnel.</summary>
    public async Task<string?> RunAloneAsync(CancellationToken ct)
    {
        var d = Current;
        if (!d.Enabled || !d.IsConfigured || MainOn)
        {
            return null;
        }

        try
        {
            switch (d.Kind)
            {
                case SecondVpnKind.WireGuard or SecondVpnKind.Server when _orchestrator is { } o && d.ServerId is { } id:
                    logger.LogInformation("Dual VPN on its own: all traffic goes through it.");
                    if (!await o.ConnectAsync(new ConnectRequest { ProfileId = id, Smart = false, DualOnly = true }, ct).ConfigureAwait(false))
                    {
                        return o.Status.LastError ?? "The second VPN did not connect.";
                    }

                    return null;
                case SecondVpnKind.Ipsec when system is not null:
                    return await DialIpsecAsync(full: true, ct).ConfigureAwait(false);
                default:
                    return null; // Kerio / another VPN app: that app decides what it carries
            }
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Before the main VPN connects: an IPsec second VPN that carried everything switches to split tunnel.</summary>
    public async Task PrepareMainAsync(CancellationToken ct)
    {
        if (Current is { Enabled: true, Kind: SecondVpnKind.Ipsec, IsConfigured: true } && system is not null && _ipsecFull != false)
        {
            await DialIpsecAsync(full: false, ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> DialIpsecAsync(bool full, CancellationToken ct)
    {
        var d = Current;
        var entry = EntryName(d);
        if (_ipsecFull != full)
        {
            await system!.HangUpAsync(entry, ct).ConfigureAwait(false);
            if (await system.SetFullTunnelAsync(entry, full, ct).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }

            _ipsecFull = full;
        }

        return await ConnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For the smart scope: the second VPN's own subnet, resolvers and domain (its adapter, when it has one), and this
    /// device's local networks (the adapters with a gateway, other than the VPNs), which must stay direct.
    /// </summary>
    public SecondVpnDetected Detect()
    {
        var d = Current;
        if (!d.Enabled || !d.IsConfigured || d.Scope != SecondVpnScope.Smart)
        {
            // Nothing to find out (and the adapter queries are not free).
            return new SecondVpnDetected();
        }

        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
            var vpn = d.Interface is { Length: > 0 } name ? all.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
            var domains = new List<string>();
            var cidrs = new List<string>();
            var dns = new List<string>();
            if (vpn is not null)
            {
                var props = vpn.GetIPProperties();
                if (props.DnsSuffix is { Length: > 0 } suffix && NetText.IsValidHostName(suffix))
                {
                    domains.Add(suffix);
                }

                dns.AddRange(props.DnsAddresses.Where(a => !IPAddress.IsLoopback(a)).Select(a => a.ToString()));
                cidrs.AddRange(props.UnicastAddresses.Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && u.PrefixLength is > 0 and < 32)
                    .Select(u => Network(u.Address, u.PrefixLength)));
            }

            static bool OurOrVpn(NetworkInterface n) => n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
                || new[] { "jacki", "ibox", "sing", "tun", "wg", "kvnet", "kerio", "vpn", "tap" }.Any(k => n.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(k, StringComparison.OrdinalIgnoreCase));
            var local = all.Where(n => n != vpn && !OurOrVpn(n) && n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && u.PrefixLength is > 0 and < 32)
                .Select(u => Network(u.Address, u.PrefixLength))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new SecondVpnDetected { Domains = domains, Cidrs = cidrs, Dns = dns, LocalSubnets = local };
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            // Android and some systems do not tell gateways or resolvers: the smart scope then uses the private ranges only.
            return new SecondVpnDetected();
        }
    }

    private static string Network(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return new IPAddress(bytes) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The system connection's name for this second VPN.</summary>
    public static string EntryName(SecondVpnSettings dual) => "IBOX " + Clean(dual.Name);

    private static string Clean(string? name)
    {
        var s = new string((name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        return s.Length == 0 ? "Company VPN" : s.Length > 40 ? s[..40].Trim() : s;
    }

    /// <summary>
    /// Is it carrying traffic now? A server route rides with the IBOX connection; an adapter route needs that adapter
    /// up (IPsec dialled, the Kerio / company app connected).
    /// </summary>
    public bool IsActive(ConnectionState main)
    {
        var d = Current;
        if (!d.Enabled || !d.IsConfigured)
        {
            return false;
        }

        return d.Route == WorkRoute.Server ? main == ConnectionState.Connected : AdapterUp(d.Interface) || (d.Kind == SecondVpnKind.Ipsec && d.Interface is null && _ipsecFull is not null);
    }

    /// <summary>One line for the dashboard and the servers list.</summary>
    public string Describe(ConnectionState main)
    {
        var d = Current;
        if (!d.Enabled)
        {
            return d.IsConfigured ? "Off" : "Not set up";
        }

        if (!d.IsConfigured)
        {
            return "Needs setup";
        }

        if (IsAlone)
        {
            return "On alone — all traffic goes through it";
        }

        if (IsActive(main))
        {
            var what = d.Scope == SecondVpnScope.Smart ? "the company's traffic (smart)" : "the listed names and subnets";
            return MainOn ? $"Active — {what} via {(d.Route == WorkRoute.Server ? d.KindLabel : d.Interface)}" : "Active — via " + (d.Interface ?? d.KindLabel);
        }

        return d.Kind switch
        {
            SecondVpnKind.Kerio => "Waiting for the Kerio VPN Client to connect",
            SecondVpnKind.App => "Waiting for the company VPN app (" + d.Interface + ")",
            SecondVpnKind.Ipsec => "Not connected",
            _ => "Connecting…",
        };
    }

    public static bool AdapterUp(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up
                && (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Description, name, StringComparison.OrdinalIgnoreCase)));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>The Kerio VPN Client's adapter ("Kerio Virtual Network" on Windows, kvnet on Linux), if present.</summary>
    public static string? FindKerioAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Description.Contains("Kerio", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("kvnet", StringComparison.OrdinalIgnoreCase)
                    || n.Name.Contains("Kerio", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.Name)
                .FirstOrDefault(Domain.Network.AdapterNames.IsValid);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Dual VPN's own switch, independent of the main VPN: on alone it carries everything; with the main VPN on
    /// it takes the company's part (the main connection is rebuilt with the split). Off: back to the main VPN alone,
    /// or nothing.
    /// </summary>
    public async Task<string?> SetEnabledAsync(bool on, CancellationToken ct)
    {
        settings.Update(s => s with { DualVpn = s.DualVpn with { Enabled = on } });
        Changed?.Invoke(this, EventArgs.Empty); // with the main VPN on, the traffic settings rebuild its connection
        var d = Current;
        if (on)
        {
            if (MainOn)
            {
                return d.Kind == SecondVpnKind.Ipsec && system is not null ? await DialIpsecAsync(full: false, ct).ConfigureAwait(false) : null;
            }

            return await RunAloneAsync(ct).ConfigureAwait(false);
        }

        if (_orchestrator?.Status.DualOnly == true)
        {
            await _orchestrator.DisconnectAsync(ct).ConfigureAwait(false);
        }

        if (d.Kind == SecondVpnKind.Ipsec && system is not null)
        {
            await system.HangUpAsync(EntryName(d), ct).ConfigureAwait(false);
            _ipsecFull = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Checks and stores the edited second VPN (sets up IPsec, saves a WireGuard config as a server).</summary>
    public async Task<string?> SaveAsync(SecondVpnDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = Clean(draft.Name);
        var domains = DualVpn.Split(draft.Domains).Select(d => d.TrimStart('*', '.').TrimEnd('.').ToLowerInvariant()).ToList();
        var cidrs = DualVpn.Split(draft.Cidrs).Select(c => NetText.IsIpLiteral(c) ? c + (c.Contains(':', StringComparison.Ordinal) ? "/128" : "/32") : c).ToList();
        var dns = DualVpn.Split(draft.Dns);
        var old = Current;
        var next = old with
        {
            Name = name,
            Kind = draft.Kind,
            Scope = draft.Scope,
            IpsecType = draft.IpsecType,
            Username = string.IsNullOrWhiteSpace(draft.Username) ? null : draft.Username.Trim(),
            Interface = null,
            ServerId = null,
            Host = null,
            GatewayIps = [],
        };

        switch (draft.Kind)
        {
            case SecondVpnKind.WireGuard:
            {
                var (wg, problem) = WireGuardConf.Parse(draft.WireGuardConfig, name);
                if (wg is null)
                {
                    // Editing other fields of a saved WireGuard second VPN without pasting the config again.
                    if (string.IsNullOrWhiteSpace(draft.WireGuardConfig) && old.Kind == SecondVpnKind.WireGuard && old.ServerId is { } kept
                        && await profiles.GetAsync(kept, ct).ConfigureAwait(false) is not null)
                    {
                        next = next with { ServerId = kept };
                        break;
                    }

                    return problem;
                }

                var id = old.Kind == SecondVpnKind.WireGuard && old.ServerId is { } previous ? previous : "dualvpn-" + Guid.NewGuid().ToString("N")[..12];
                var profile = wg.Profile with
                {
                    Id = id,
                    Name = name + " (Dual VPN)",
                    Origin = new ProfileOrigin { Kind = ProfileOriginKind.Manual, ProviderId = OwnedTag },
                };
                await profiles.UpsertAsync(profile, ct).ConfigureAwait(false);
                next = next with { ServerId = id };
                if (cidrs.Count == 0)
                {
                    cidrs.AddRange(wg.Subnets);
                }

                if (domains.Count == 0)
                {
                    domains.AddRange(wg.SearchDomains);
                }

                if (dns.Count == 0)
                {
                    dns = wg.Dns;
                }

                break;
            }

            case SecondVpnKind.Server:
                if (string.IsNullOrWhiteSpace(draft.ServerId) || await profiles.GetAsync(draft.ServerId, ct).ConfigureAwait(false) is null)
                {
                    return "Choose the server that carries company traffic.";
                }

                next = next with { ServerId = draft.ServerId };
                break;

            case SecondVpnKind.Kerio:
            case SecondVpnKind.App:
            {
                var adapter = (draft.Interface ?? (draft.Kind == SecondVpnKind.Kerio ? FindKerioAdapter() : null))?.Trim();
                if (!Domain.Network.AdapterNames.IsValid(adapter))
                {
                    return draft.Kind == SecondVpnKind.Kerio
                        ? "Install the Kerio Control VPN Client and connect it once: IBOX then finds its adapter by itself."
                        : "Connect the company's VPN app first, then choose its network adapter.";
                }

                next = next with { Interface = adapter };
                break;
            }

            case SecondVpnKind.Ipsec:
            {
                if (system is null)
                {
                    return "IPsec is not available on this device.";
                }

                var host = draft.Host?.Trim();
                if (!NetText.IsValidServerAddress(host))
                {
                    return "Enter the VPN gateway (a name like vpn.company.com or an address).";
                }

                if (next.Username is null)
                {
                    return "Enter the VPN user name.";
                }

                var (password, psk) = Secrets(old);
                password = draft.Password ?? password;
                psk = draft.PreSharedKey ?? psk;
                if (string.IsNullOrEmpty(password))
                {
                    return "Enter the VPN password.";
                }

                if (draft.IpsecType == IpsecType.L2tp && string.IsNullOrEmpty(psk))
                {
                    return "L2TP/IPsec needs the pre-shared key (PSK).";
                }

                var entry = EntryName(next);
                if (old.Kind == SecondVpnKind.Ipsec && EntryName(old) != entry)
                {
                    await system.RemoveAsync(EntryName(old), ct).ConfigureAwait(false);
                }

                if (await system.SetUpIpsecAsync(entry, host!, draft.IpsecType, psk, next.Username, password, cidrs.Where(NetText.IsCidr).ToList(), ct).ConfigureAwait(false) is { } failed)
                {
                    return failed;
                }

                next = next with
                {
                    Host = host,
                    Interface = system.AdapterOf(entry),
                    GatewayIps = await ResolveAsync(host!, ct).ConfigureAwait(false),
                    Secret = Protect(password, psk),
                };
                break;
            }
        }

        if (domains.FirstOrDefault(d => !NetText.IsValidHostName(d)) is { } badName)
        {
            return $"\"{badName}\" is not a domain name.";
        }

        if (cidrs.FirstOrDefault(c => !NetText.IsCidr(c)) is { } badCidr)
        {
            return $"\"{badCidr}\" is not a subnet (like 10.20.0.0/16).";
        }

        if (dns.FirstOrDefault(d => !NetText.IsIpLiteral(d)) is { } badDns)
        {
            return $"\"{badDns}\" is not an IP address.";
        }

        if (draft.Scope == SecondVpnScope.Custom && domains.Count == 0 && cidrs.Count == 0)
        {
            return "Add the company's names or subnets (only they take the second VPN), or choose Smart.";
        }

        if (domains.Count > 500 || cidrs.Count > 500 || dns.Count > 8)
        {
            return "Too many entries.";
        }

        // A WireGuard server replaced by another kind is no longer needed.
        if (old.Kind == SecondVpnKind.WireGuard && old.ServerId is { } stale && stale != next.ServerId)
        {
            await profiles.DeleteAsync(stale, ct).ConfigureAwait(false);
        }

        if (old.Kind == SecondVpnKind.Ipsec && next.Kind != SecondVpnKind.Ipsec && system is not null)
        {
            await system.RemoveAsync(EntryName(old), ct).ConfigureAwait(false);
        }

        next = next with
        {
            Enabled = true,
            Domains = domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Cidrs = cidrs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Dns = dns.ToList(),
            Secret = next.Kind == SecondVpnKind.Ipsec ? next.Secret : null,
        };
        settings.Update(s => s with { DualVpn = next });
        logger.LogInformation("Dual VPN saved: {Kind}, {Names} names, {Subnets} subnets.", next.KindLabel, next.Domains.Count, next.Cidrs.Count);
        _ipsecFull = null;
        Changed?.Invoke(this, EventArgs.Empty);

        // Saved = on: with the main VPN on it takes the company's part; alone it carries everything.
        if (MainOn)
        {
            return next.Kind == SecondVpnKind.Ipsec && system is not null ? await DialIpsecAsync(full: false, ct).ConfigureAwait(false) : null;
        }

        return await RunAloneAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Dials the IPsec second VPN (others connect by themselves or with IBOX).</summary>
    public async Task<string?> ConnectAsync(CancellationToken ct)
    {
        var d = Current;
        if (d.Kind != SecondVpnKind.Ipsec || system is null)
        {
            return null;
        }

        var (password, _) = Secrets(d);
        var entry = EntryName(d);
        var problem = await system.DialAsync(entry, d.Username, password, ct).ConfigureAwait(false);
        if (problem is null && system.AdapterOf(entry) is { } adapter && adapter != Current.Interface)
        {
            settings.Update(s => s with { DualVpn = s.DualVpn with { Interface = adapter } });
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return problem;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (Current.Kind == SecondVpnKind.Ipsec && system is not null)
        {
            await system.HangUpAsync(EntryName(Current), ct).ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the second VPN (its WireGuard server and IPsec connection too).</summary>
    public async Task RemoveAsync(CancellationToken ct)
    {
        var d = Current;
        if (d.Kind == SecondVpnKind.WireGuard && d.ServerId is { } id)
        {
            await profiles.DeleteAsync(id, ct).ConfigureAwait(false);
        }

        if (d.Kind == SecondVpnKind.Ipsec && system is not null)
        {
            await system.HangUpAsync(EntryName(d), ct).ConfigureAwait(false);
            await system.RemoveAsync(EntryName(d), ct).ConfigureAwait(false);
        }

        settings.Update(s => s with { DualVpn = new SecondVpnSettings() });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The stored IPsec password and pre-shared key (for an account export, which encrypts them again).</summary>
    public (string? Password, string? Psk) ReadSecrets() => Secrets(Current);

    private string Protect(string? password, string? psk)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new[] { password ?? string.Empty, psk ?? string.Empty });
        return Convert.ToBase64String(protector.Protect(payload, Purpose));
    }

    private (string? Password, string? Psk) Secrets(SecondVpnSettings d)
    {
        if (d.Secret is not { Length: > 0 } secret)
        {
            return (null, null);
        }

        try
        {
            var parts = System.Text.Json.JsonSerializer.Deserialize<string[]>(protector.Unprotect(Convert.FromBase64String(secret), Purpose));
            return parts is { Length: 2 } ? (parts[0].Length > 0 ? parts[0] : null, parts[1].Length > 0 ? parts[1] : null) : (null, null);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogWarning("The Dual VPN password could not be read on this device; enter it again.");
            return (null, null);
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(NetText.TrimBrackets(host), out var ip))
        {
            return [ip.ToString()];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return (await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false)).Take(8).Select(a => a.ToString()).ToList();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return [];
        }
    }
}
