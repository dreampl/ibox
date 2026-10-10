using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Cores;

public enum SessionKind
{
    /// <summary>Loopback mixed inbound for the Windows system proxy.</summary>
    SystemProxy = 0,

    /// <summary>Wintun TUN inbound (sing-box only).</summary>
    Tun,

    /// <summary>One loopback inbound per profile, used by health probes.</summary>
    ProbeBatch,

    /// <summary>Xray running as the protocol backend behind the sing-box frontend.</summary>
    ChainBackend,
}

public sealed record TunOptions
{
    public string InterfaceName { get; init; } = Domain.ProductInfo.TunInterfaceName;
    public int Mtu { get; init; } = 9000;
    public string AddressV4 { get; init; } = "172.19.0.1/30";
    public string AddressV6 { get; init; } = "fdfe:dcba:9876::1/126";

    /// <summary>Addresses routed outside the tunnel (e.g. pre-resolved server IPs in chain mode).</summary>
    public IReadOnlyList<string> RouteExcludeAddresses { get; init; } = [];
}

/// <summary>
/// The loopback SOCKS hop from sing-box into the Xray backend. <paramref name="Auth"/> protects it: on Android any
/// app can reach loopback ports, and an open hop would let them use the VPN exit.
/// </summary>
public sealed record ChainBackendEndpoint(int SocksPort, string BackendExecutablePath, InboundCredentials? Auth = null);

public sealed record ClashApiOptions(int Port, string Secret);

/// <summary>
/// The traffic relay: the core sends shaped connections to the relay's SOCKS port (user name = shaping class),
/// and the relay hands them back through the core's <c>jv-back</c> inbound, where normal routing continues.
/// </summary>
public sealed record ShaperEndpoint(int Port, string Password, int BackPort, InboundCredentials BackAuth)
{
    public override string ToString() => $"ShaperEndpoint {{ Port = {Port}, BackPort = {BackPort} }}";
}

/// <summary>SOCKS5/HTTP credentials required by the local mixed inbound.</summary>
public sealed record InboundCredentials(string Username, string Password)
{
    /// <summary>Never prints the password (plans may end up in diagnostics).</summary>
    public override string ToString() => $"InboundCredentials {{ Username = {Username}, Password = *** }}";
}

/// <summary>Everything a core needs to generate a configuration. Built and validated by trusted code only.</summary>
public sealed record CoreSessionPlan
{
    public required SessionKind Kind { get; init; }

    /// <summary>One profile for sessions/backends; N profiles for probe batches.</summary>
    public required IReadOnlyList<ProxyProfile> Profiles { get; init; }

    /// <summary>Mixed inbound port (SystemProxy) or SOCKS port (ChainBackend).</summary>
    public int ListenPort { get; init; }

    /// <summary>Per-profile loopback ports for probe batches (same order as <see cref="Profiles"/>).</summary>
    public IReadOnlyList<int> ProbePorts { get; init; } = [];

    public DnsSettings Dns { get; init; } = new();
    public Ipv6Mode Ipv6 { get; init; } = Ipv6Mode.Auto;

    /// <summary>Result of the IPv6 capability probe for Auto mode (null = unknown → treated as unsupported).</summary>
    public bool? TunnelSupportsIpv6 { get; init; }

    public RoutingSettings Routing { get; init; } = new();

    /// <summary>Rule-set tag → verified local file path (only tags present here are referenced).</summary>
    public IReadOnlyDictionary<string, string> RuleSetPaths { get; init; } = new Dictionary<string, string>();

    public TunOptions Tun { get; init; } = new();

    /// <summary>TUN sessions: also listen as a loopback proxy on this port (for the system proxy setting; 0 = no).</summary>
    public int TunProxyPort { get; init; }

    /// <summary>TUN network stack (TUN sessions only).</summary>
    public TunStack TunStack { get; init; } = TunStack.Mixed;

    /// <summary>
    /// The mixed inbound is fed by an operating-system TUN that the core does not own (Android VpnService →
    /// tun2socks → SOCKS5). All device traffic, DNS included, arrives here, so TUN-level policy applies:
    /// port-53 DNS hijack, fake-IP, QUIC policy and the IPv6 rules.
    /// </summary>
    public bool ExternalTun { get; init; }

    /// <summary>
    /// Credentials the mixed inbound demands. On Android every app can reach loopback ports, so an open proxy
    /// would let any installed app use the tunnel or learn the VPN exit; the frontend gets random per-session
    /// credentials there. Not used for the Windows system proxy (WinINet cannot pass them to applications).
    /// </summary>
    public InboundCredentials? InboundAuth { get; init; }

    /// <summary>True when the core sees all device traffic (own TUN or an external one).</summary>
    public bool CapturesAllTraffic => Kind == SessionKind.Tun || (Kind == SessionKind.SystemProxy && ExternalTun);

    /// <summary>Reject QUIC (UDP 443) that would go through the proxy, so browsers fall back to TCP immediately.</summary>
    public bool BlockQuic { get; init; }

    /// <summary>Effective MUX decision (Auto already resolved by settings/benchmark).</summary>
    public bool MuxEnabled { get; init; }

    public ClashApiOptions? ClashApi { get; init; }

    /// <summary>When set, the sing-box frontend forwards proxied traffic to this Xray backend.</summary>
    public ChainBackendEndpoint? ChainBackend { get; init; }

    /// <summary>Executables whose traffic must never enter the tunnel (the cores themselves).</summary>
    public IReadOnlyList<string> DirectProcessPaths { get; init; } = [];

    public bool DebugLogging { get; init; }

    /// <summary>Allow inbound connections from the LAN to the local proxy (default loopback only).</summary>
    public bool ListenOnLan { get; init; }

    /// <summary>Blocks, workplace routes and shaping classes (null = none).</summary>
    public Domain.Usage.TrafficPolicy? Traffic { get; init; }

    /// <summary>The in-app relay that applies speed limits (required when <see cref="Traffic"/> shapes).</summary>
    public ShaperEndpoint? Shaper { get; init; }

    public bool Ipv6Allowed => Ipv6 switch
    {
        Ipv6Mode.Enable => true,
        Ipv6Mode.Disable => false,
        _ => TunnelSupportsIpv6 == true,
    };
}

public sealed record GeneratedCoreConfig
{
    public required CoreKind Kind { get; init; }
    public required SessionKind Session { get; init; }

    /// <summary>Complete configuration JSON. Contains credentials — never log or persist it.</summary>
    public required string Json { get; init; }

    public required CapabilityReport Report { get; init; }

    /// <summary>Secret strings contained in <see cref="Json"/> (registered with the redactor while the core runs).</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];

    public ClashApiOptions? ClashApi { get; init; }

    public IReadOnlyList<int> ListenPorts { get; init; } = [];

    /// <summary>Non-fatal notes produced while generating (e.g. an unavailable rule-set was skipped).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record CoreValidationResult(bool IsValid, IReadOnlyList<string> Errors, string RedactedOutput)
{
    public string Summary => IsValid ? "Configuration accepted by the core." : string.Join(" ", Errors);
}

public sealed record CoreLaunchSpec(
    CoreKind Kind,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public interface ICoreEngine
{
    CoreKind Kind { get; }

    /// <summary>Detects the installed binary's version/build tags (cached) and derives capabilities.</summary>
    Task<CoreCapabilities> GetCapabilitiesAsync(CancellationToken ct);

    /// <summary>Checks every feature the profile/session needs against the core. Never silently drops features.</summary>
    CapabilityReport Negotiate(ProxyProfile profile, CoreCapabilities capabilities, SessionKind session, bool muxEnabled);

    GeneratedCoreConfig GenerateConfig(CoreSessionPlan plan, CoreCapabilities capabilities);

    /// <summary>Runs the core's own config check (<c>sing-box check</c> / <c>xray run -test</c>).</summary>
    Task<CoreValidationResult> ValidateAsync(GeneratedCoreConfig config, CancellationToken ct);

    CoreLaunchSpec BuildLaunchSpec(string workingDirectory);
}
