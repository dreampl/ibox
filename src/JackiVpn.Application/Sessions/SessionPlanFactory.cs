using System.Net;
using System.Net.Sockets;
using JackiVpn.Cores;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Sessions;

public sealed record SessionRequest
{
    public required ProxyProfile Profile { get; init; }
    public required ConnectionMode Mode { get; init; }
    public CorePreference Core { get; init; } = CorePreference.Auto;
    public DnsSettings Dns { get; init; } = new();
    public Ipv6Mode Ipv6 { get; init; } = Ipv6Mode.Auto;
    public bool? TunnelSupportsIpv6 { get; init; }
    public RoutingSettings Routing { get; init; } = new();
    public IReadOnlyDictionary<string, string> RuleSetPaths { get; init; } = new Dictionary<string, string>();
    public int Mtu { get; init; } = 9000;
    public bool MuxEnabled { get; init; }
    public bool DebugLogging { get; init; }

    public PerformanceSettings Performance { get; init; } = new();

    /// <summary>Android: a VpnService TUN feeds the local inbound (see <see cref="CoreSessionPlan.ExternalTun"/>).</summary>
    public bool ExternalTun { get; init; }

    /// <summary>Proxy mode listen port (0 = pick a free loopback port).</summary>
    public int ListenPort { get; init; }

    /// <summary>
    /// Explicit credentials for the local inbound (Android proxy-only mode, where the user enters them in apps).
    /// When null, an external-TUN session gets random per-session credentials and a Windows system proxy none.
    /// </summary>
    public InboundCredentials? InboundAuth { get; init; }

    /// <summary>TUN: also open a loopback proxy on <see cref="ListenPort"/> for the system proxy setting.</summary>
    public bool TunSystemProxy { get; init; }

    /// <summary>Blocks, workplace routes and shaping classes for this session.</summary>
    public Domain.Usage.TrafficPolicy? Traffic { get; init; }
}

public sealed record SessionPlan(
    CoreSelection Selection,
    CoreSessionPlan Frontend,
    CoreSessionPlan? Backend,
    IReadOnlyList<string> Warnings)
{
    public bool Chained => Backend is not null;
}

public sealed class SessionPlanException(string message) : Exception(message);

/// <summary>Builds validated core session plans (frontend sing-box, optional Xray backend) from a request.</summary>
public sealed class SessionPlanFactory(CoreBinaryCatalog catalog)
{
    public async Task<SessionPlan> CreateAsync(SessionRequest request, CoreCapabilities singBox, CoreCapabilities? xray, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(singBox);
        var validation = ProfileValidator.Validate(request.Profile);
        if (!validation.IsValid)
        {
            throw new SessionPlanException("Profile is invalid: " + string.Join(" ", validation.Errors.Select(e => e.Message)));
        }

        var kind = request.Mode == ConnectionMode.Tun ? SessionKind.Tun : SessionKind.SystemProxy;
        var selection = CoreSelector.Select(request.Profile, request.Core, kind, request.MuxEnabled, singBox, xray);
        if (!selection.IsUsable)
        {
            throw new SessionPlanException(string.Join(" ", selection.Report.Issues.Where(i => i.Impact == CapabilityImpact.Blocking).Select(i => i.Message)));
        }

        var warnings = selection.Report.Issues.Where(i => i.Impact == CapabilityImpact.Degraded).Select(i => i.Message).ToList();
        var ports = PortAllocator.GetFreeLoopbackPorts(5);
        ShaperEndpoint? shaper = null;
        if (request.Traffic is { Shaping: true } traffic && Cores.Generation.SingBoxConfigBuilder.ShapeClassesOf(traffic).Count > 0)
        {
            shaper = new ShaperEndpoint(ports[3], PortAllocator.NewApiSecret(), ports[4], new InboundCredentials("back-" + PortAllocator.NewApiSecret()[..8], PortAllocator.NewApiSecret()));
        }

        var listen = ports[0];
        if (request.ListenPort > 0 && (kind == SessionKind.SystemProxy || request.TunSystemProxy))
        {
            if (PortAllocator.IsPortInUse(request.ListenPort))
            {
                // Say who holds it and how to get it back, not just that it is taken.
                var owner = JackiVpn.Networking.Probes.PortOwner.Find(request.ListenPort);
                var who = owner is null ? "another program" : owner.Describe();
                warnings.Add($"Port {request.ListenPort} is used by {who}, so this session listens on 127.0.0.1:{listen}. " +
                    "Apps that copied the old proxy address when they started (e.g. Claude) need a restart. " +
                    (owner?.HowToFree(request.ListenPort) ?? $"Close the program that uses port {request.ListenPort}, or pick another port in Network › Local port."));
            }
            else
            {
                listen = request.ListenPort;
            }
        }

        var api = new ClashApiOptions(ports[1], PortAllocator.NewApiSecret());
        var externalTun = request.ExternalTun && kind == SessionKind.SystemProxy;

        CoreSessionPlan? backend = null;
        ChainBackendEndpoint? chain = null;
        var exclude = new List<string>();
        var directProcesses = new List<string> { catalog.PathOf(CoreKind.SingBox) };
        if (selection.UseChain)
        {
            var backendPort = ports[2];
            var hopAuth = new InboundCredentials("chain-" + PortAllocator.NewApiSecret()[..8], PortAllocator.NewApiSecret());

            // Xray dials the server itself. Resolve the name here (FakeIP answers left in the system cache by an
            // earlier session are ignored) and give Xray the address, keeping the name for SNI/Host: a stale fake
            // address is why chained configs (XHTTP, gRPC with custom paths) used to work only on the first connect.
            var addresses = await ResolveServerAsync(request.Profile.Server, FlushDns, ct).ConfigureAwait(false);
            backend = new CoreSessionPlan
            {
                Kind = SessionKind.ChainBackend,
                Profiles = [PinServerAddress(request.Profile, addresses)],
                ListenPort = backendPort,
                MuxEnabled = request.MuxEnabled,
                DebugLogging = request.DebugLogging,
                InboundAuth = hopAuth,
            };
            chain = new ChainBackendEndpoint(backendPort, catalog.PathOf(CoreKind.Xray), hopAuth);
            directProcesses.Add(chain.BackendExecutablePath);
            if (kind == SessionKind.Tun)
            {
                // Route the server's addresses outside the tunnel (no loop).
                exclude.AddRange(addresses.Select(Cidr).Distinct());
            }
        }

        var frontend = new CoreSessionPlan
        {
            Kind = kind,
            Profiles = [request.Profile],
            ListenPort = listen,
            Dns = request.Dns,
            Ipv6 = request.Ipv6,
            TunnelSupportsIpv6 = request.TunnelSupportsIpv6,
            Routing = request.Routing,
            RuleSetPaths = request.RuleSetPaths,
            Tun = new TunOptions { Mtu = request.Mtu, RouteExcludeAddresses = exclude },
            TunProxyPort = kind == SessionKind.Tun && request.TunSystemProxy ? listen : 0,
            TunStack = request.Performance.TunStack,
            ExternalTun = externalTun,
            InboundAuth = request.InboundAuth ?? (externalTun ? new InboundCredentials("jacki-" + PortAllocator.NewApiSecret()[..12], PortAllocator.NewApiSecret()) : null),
            BlockQuic = (kind == SessionKind.Tun || externalTun) && request.Performance.Quic switch
            {
                QuicPolicy.Block => true,
                QuicPolicy.Allow => false,
                _ => !request.Profile.IsUdpBasedProtocol,
            },
            MuxEnabled = request.MuxEnabled,
            ClashApi = api,
            ChainBackend = chain,
            DirectProcessPaths = directProcesses,
            DebugLogging = request.DebugLogging,
            Traffic = request.Traffic,
            Shaper = shaper,
        };

        if (request.Dns.Mode is DnsMode.System or DnsMode.Local)
        {
            warnings.Add("DNS is resolved outside the tunnel in this DNS mode (not leak-protected).");
        }

        return new SessionPlan(selection, frontend, backend, warnings);
    }

    /// <summary>Clears the OS DNS cache (Windows) when only FakeIP answers come back.</summary>
    public Action? FlushDns { get; init; }

    public static async Task<IReadOnlyList<string>> ResolveServerCidrsAsync(string server, CancellationToken ct) =>
        (await ResolveServerAsync(server, null, ct).ConfigureAwait(false)).Select(Cidr).Distinct().ToArray();

    /// <summary>Real addresses of the server; FakeIP answers (198.18.0.0/15, fc00::/18) are never returned.</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveServerAsync(string server, Action? flushDns, CancellationToken ct)
    {
        if (IPAddress.TryParse(NetText.TrimBrackets(server), out var ip))
        {
            return [ip];
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var real = (await Dns.GetHostAddressesAsync(server, ct).ConfigureAwait(false)).Where(a => !IsFakeIp(a)).ToArray();
                if (real.Length > 0)
                {
                    return real;
                }
            }
            catch (SocketException ex) when (attempt == 1)
            {
                throw new SessionPlanException($"Cannot resolve the server address '{server}': {ex.SocketErrorCode}.");
            }
            catch (SocketException)
            {
            }

            flushDns?.Invoke();
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        throw new SessionPlanException($"Cannot resolve the server address '{server}' (only tunnel-internal answers were returned; try again in a moment).");
    }

    public static bool IsFakeIp(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 198 && (b[1] & 0xFE) == 18
            : b.Length == 16 && b[0] == 0xFC && b[1] == 0x00 && (b[2] & 0xC0) == 0x00;
    }

    /// <summary>The backend dials the resolved address; the original name stays as SNI and HTTP Host/authority.</summary>
    public static ProxyProfile PinServerAddress(ProxyProfile profile, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (addresses.Count == 0 || IPAddress.TryParse(NetText.TrimBrackets(profile.Server), out _))
        {
            return profile;
        }

        var name = profile.Server;
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var tls = profile.Tls.Enabled && !profile.Tls.IsReality && string.IsNullOrEmpty(profile.Tls.ServerName) && !profile.Tls.DisableSni
            ? profile.Tls with { ServerName = name }
            : profile.Tls;
        var transport = profile.Transport.Type is TransportType.WebSocket or TransportType.HttpUpgrade or TransportType.XHttp or TransportType.Http2 or TransportType.Grpc
            && string.IsNullOrEmpty(profile.Transport.Host)
                ? profile.Transport with { Host = name }
                : profile.Transport;
        return profile with { Server = address.ToString(), Tls = tls, Transport = transport };
    }

    private static string Cidr(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip + "/128" : ip + "/32";
}
