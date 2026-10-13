using System.Text.Json.Serialization;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Domain.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<DnsMode>))]
public enum DnsMode
{
    /// <summary>Default. DoH through the tunnel, bootstrap only for proxy hostnames, hijack port 53.</summary>
    AutoSecure = 0,

    /// <summary>Windows-configured resolvers, queried directly (not leak-safe; flagged in UI).</summary>
    System,

    DoH,
    DoT,
    Custom,

    /// <summary>A resolver on this machine / LAN (e.g. Pi-hole), queried directly.</summary>
    Local,

    /// <summary>Plain DNS server reached through the tunnel.</summary>
    RemoteThroughVpn,
}

[JsonConverter(typeof(JsonStringEnumConverter<DnsTransport>))]
public enum DnsTransport
{
    Udp = 0,
    Tcp,
    Tls,
    Https,
    Quic,
    H3,

    /// <summary>The operating system resolver configuration.</summary>
    Local,
}

[JsonConverter(typeof(JsonStringEnumConverter<Ipv6Mode>))]
public enum Ipv6Mode
{
    Auto = 0,
    Enable,
    Disable,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    Dark = 0,

    /// <summary>Legacy palette; shown as Dark.</summary>
    Black,

    /// <summary>Legacy palette; shown as Dark.</summary>
    Blue,
    Light,

    /// <summary>Follows the Windows app theme (light or dark), live.</summary>
    System,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppLogLevel>))]
public enum AppLogLevel
{
    Error = 0,
    Warning,
    Information,
    Debug,
    Trace,
}

[JsonConverter(typeof(JsonStringEnumConverter<NotificationCategory>))]
public enum NotificationCategory
{
    Connected = 0,
    Disconnected,
    ConnectionFailed,
    AutomaticFailover,
    SubscriptionUpdated,
    SubscriptionExpired,
    TrafficAlmostFinished,
    CoreCrashed,
    KillSwitchActivated,
    UpdateAvailable,

    /// <summary>Data cap, unusual consumption, new apps, automatic updates.</summary>
    UsageAlert,
}

public sealed record DnsServerEndpoint
{
    public DnsTransport Transport { get; init; } = DnsTransport.Https;

    /// <summary>IP literal preferred (avoids bootstrap). Host names are resolved via the bootstrap resolver.</summary>
    public string? Server { get; init; }

    public int? Port { get; init; }

    /// <summary>DoH path (default <c>/dns-query</c>).</summary>
    public string? Path { get; init; }

    /// <summary>TLS server name for DoT/DoH when <see cref="Server"/> is an IP.</summary>
    public string? TlsServerName { get; init; }

    public static DnsServerEndpoint CloudflareDoh => new() { Transport = DnsTransport.Https, Server = "1.1.1.1", Path = "/dns-query" };
    public static DnsServerEndpoint GoogleDoh => new() { Transport = DnsTransport.Https, Server = "8.8.8.8", Path = "/dns-query" };
    public static DnsServerEndpoint CloudflareDot => new() { Transport = DnsTransport.Tls, Server = "1.1.1.1", TlsServerName = "one.one.one.one" };
    public static DnsServerEndpoint SystemLocal => new() { Transport = DnsTransport.Local };
}

/// <summary>A resolver entered by hand: an IP address, plain DNS or DNS-over-HTTPS (optionally with its host name).</summary>
public sealed record CustomDnsResolver
{
    public required string Name { get; init; }

    /// <summary>IP address (v4 or v6). Names are not accepted: this resolver may be the one that resolves names.</summary>
    public required string Address { get; init; }

    /// <summary><see cref="DnsTransport.Udp"/> (plain) or <see cref="DnsTransport.Https"/> (DoH).</summary>
    public DnsTransport Transport { get; init; } = DnsTransport.Udp;

    /// <summary>DoH only: the server's host name (TLS name and HTTP host), e.g. dns.google.</summary>
    public string? Host { get; init; }

    /// <summary>DoH only: path, default /dns-query.</summary>
    public string? Path { get; init; }

    public const int MaxCount = 32;

    public DnsServerEndpoint ToEndpoint() => Transport == DnsTransport.Https
        ? new DnsServerEndpoint { Transport = DnsTransport.Https, Server = Address.Trim(), Path = string.IsNullOrWhiteSpace(Path) ? "/dns-query" : Path.Trim(), TlsServerName = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim() }
        : new DnsServerEndpoint { Transport = DnsTransport.Udp, Server = Address.Trim() };

    /// <summary>Null when the entry is usable, else what is wrong with it (shown to the user).</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 40 || Name.Any(char.IsControl))
        {
            return "Give it a short name (up to 40 characters).";
        }

        var address = Address?.Trim() ?? string.Empty;
        if (!System.Net.IPAddress.TryParse(address, out _) || (!address.Contains(':', StringComparison.Ordinal) && address.Count(c => c == '.') != 3))
        {
            return "The address must be an IP address, e.g. 1.1.1.1 or 2606:4700:4700::1111.";
        }

        if (Transport is not (DnsTransport.Udp or DnsTransport.Https))
        {
            return "Choose plain DNS or DNS-over-HTTPS.";
        }

        if (Transport == DnsTransport.Https)
        {
            if (!string.IsNullOrWhiteSpace(Host) && Uri.CheckHostName(Host.Trim()) != UriHostNameType.Dns)
            {
                return "The DoH host must be a host name, e.g. dns.google.";
            }

            if (!string.IsNullOrWhiteSpace(Path) && (!Path.Trim().StartsWith('/') || Path.Length > 128 || Path.Any(c => char.IsControl(c) || c == ' ')))
            {
                return "The DoH path must start with / (default /dns-query).";
            }
        }

        return null;
    }
}

public sealed record DnsSettings
{
    public DnsMode Mode { get; init; } = DnsMode.AutoSecure;

    /// <summary>Resolver for proxied names (reached through the tunnel).</summary>
    public DnsServerEndpoint Remote { get; init; } = DnsServerEndpoint.CloudflareDoh;

    /// <summary>Resolver for names routed Direct (queried outside the tunnel).</summary>
    public DnsServerEndpoint Direct { get; init; } = DnsServerEndpoint.SystemLocal;

    /// <summary>Resolver used only to resolve proxy server host names (breaks the circular dependency).</summary>
    public DnsServerEndpoint Bootstrap { get; init; } = DnsServerEndpoint.SystemLocal;

    /// <summary>
    /// TUN only: tunneled names get an instant placeholder answer and are resolved by the server, saving a full
    /// tunnel round-trip per new domain. Names routed Direct always get real answers.
    /// </summary>
    public bool FakeIpEnabled { get; init; } = true;

    /// <summary>Resolvers the user added by hand: offered next to the built-in ones and included in the DNS search.</summary>
    public IReadOnlyList<CustomDnsResolver> Custom { get; init; } = [];

    /// <summary>The chosen resolver (smart or manual) also answers names routed Direct, instead of the system's.</summary>
    public bool UseChosenForDirect { get; init; }

    public string FakeIpV4Range { get; init; } = "198.18.0.0/15";
    public string FakeIpV6Range { get; init; } = "fc00::/18";
    public bool CacheEnabled { get; init; } = true;
}

public sealed record KillSwitchSettings
{
    public bool Enabled { get; init; }
    public bool AllowLan { get; init; } = true;
}

public sealed record ConnectionSettings
{
    public ConnectionMode Mode { get; init; } = ConnectionMode.Tun;
    public CorePreference Core { get; init; } = CorePreference.Auto;
    public string? SelectedProfileId { get; init; }
    public bool SmartMode { get; init; } = true;

    public const int DefaultLocalProxyPort = 10808;

    /// <summary>
    /// Local mixed (HTTP + SOCKS) inbound port for proxy mode. Kept stable across connections: some apps copy
    /// the system proxy address once at start (e.g. Claude Desktop passes it to its agent as HTTPS_PROXY), so a
    /// port that changes on every connect breaks them. 0 = pick a free port each time (not recommended).
    /// </summary>
    public int LocalProxyPort { get; init; } = DefaultLocalProxyPort;

    /// <summary>
    /// TUN mode also sets the system proxy to the tunnel's loopback port: apps such as Claude check the system
    /// proxy setting and misbehave without one, although the TUN already carries their traffic.
    /// </summary>
    public bool SystemProxyInTun { get; init; } = true;

    /// <summary>Also set the machine-wide WinHTTP proxy (needs the service). Off by default.</summary>
    public bool SetWinHttpProxy { get; init; }

    public string ProxyBypassList { get; init; } = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    public int TunMtu { get; init; } = 9000;

    public MuxMode Mux { get; init; } = MuxMode.Auto;

    /// <summary>Maximum automatic reconnect attempts before failing over / giving up.</summary>
    public int MaxReconnectAttempts { get; init; } = 3;
}

public sealed record RoutingSettings
{
    public RoutingPreset Preset { get; init; } = RoutingPreset.Rule;
    public IReadOnlyList<RoutingRule> Rules { get; init; } = [];
    public IReadOnlyList<RuleSetDefinition> RuleSets { get; init; } = [];

    /// <summary>What happens to traffic no rule matched.</summary>
    public RuleActionType Final { get; init; } = RuleActionType.Proxy;

    public bool BypassLan { get; init; } = true;
    public bool Sniff { get; init; } = true;
}

public sealed record ScoreWeights
{
    // Latency and handshake time (what users feel as "ping" and page-load delay) weigh most after reliability.
    public double Latency { get; init; } = 0.35;
    public double Jitter { get; init; } = 0.10;
    public double SuccessRate { get; init; } = 0.25;
    public double Handshake { get; init; } = 0.15;
    public double Stability { get; init; } = 0.10;
    public double Throughput { get; init; } = 0.05;

    public double GoodLatencyMs { get; init; } = 150;
    public double BadLatencyMs { get; init; } = 1500;
    public double GoodJitterMs { get; init; } = 10;
    public double BadJitterMs { get; init; } = 300;
    public double GoodHandshakeMs { get; init; } = 100;
    public double BadHandshakeMs { get; init; } = 1200;
    public double GoodThroughputKbps { get; init; } = 20_000;
    public double BadThroughputKbps { get; init; } = 500;

    public double TimeoutPenalty { get; init; } = 5;
    public double DisconnectPenalty { get; init; } = 8;
    public double MaxPenalty { get; init; } = 40;
}

public sealed record FailoverPolicy
{
    public int ConsecutiveFailuresToFailover { get; init; } = 3;

    /// <summary>A candidate must beat the current node's score by this ratio (hysteresis).</summary>
    public double SwitchMarginRatio { get; init; } = 0.15;

    /// <summary>…and by at least this many score points.</summary>
    public double MinScoreGap { get; init; } = 8;

    /// <summary>Minimum time on a node before a proactive (non-failure) switch.</summary>
    public TimeSpan MinDwell { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>A node abandoned for failures is not reconsidered for this long.</summary>
    public TimeSpan FailedNodeCooldown { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxSwitchesPerHour { get; init; } = 6;
}

public sealed record ProbeSettings
{
    /// <summary>At least two endpoints so one external outage does not fail every node.</summary>
    public IReadOnlyList<string> ProbeUrls { get; init; } =
        ["https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204"];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);
    public int SamplesPerNode { get; init; } = 3;
    public int MaxConcurrency { get; init; } = 4;
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
    public bool ThroughputTest { get; init; }
    public string ThroughputUrl { get; init; } = "https://speed.cloudflare.com/__down?bytes=1000000";

    /// <summary>
    /// Server tests also download a small payload (64 KB): on throttled networks a connection can complete the
    /// handshake and then freeze after ~15–20 KB, so a handshake alone does not prove a server works.
    /// </summary>
    public bool DataCheck { get; init; } = true;

    public string DataCheckUrl { get; init; } = "https://speed.cloudflare.com/__down?bytes=65536";

    /// <summary>After connecting, look up the exit IP and country through the tunnel (reveals only the VPN exit).</summary>
    public bool CheckPublicIp { get; init; } = true;

    /// <summary>Returns <c>ip=</c> and <c>loc=</c> (country) lines; served from the nearest Cloudflare edge.</summary>
    public string PublicIpUrl { get; init; } = "https://www.cloudflare.com/cdn-cgi/trace";

    /// <summary>Optional city / ISP lookup for the exit IP (empty disables it).</summary>
    public string GeoDetailsUrl { get; init; } = "https://ipwho.is/";
    public int HistoryWindow { get; init; } = 30;

    /// <summary>IPv6-only target used to detect whether a tunnel carries IPv6 (IPv6 Auto mode).</summary>
    public string Ipv6ProbeUrl { get; init; } = "http://[2606:4700:4700::1111]/";

    /// <summary>Profiles tested per core process.</summary>
    public int BatchSize { get; init; } = 24;
}

public sealed record SmartSettings
{
    public ScoreWeights Weights { get; init; } = new();
    public FailoverPolicy Failover { get; init; } = new();

    /// <summary>Proactive (non-failure) switching; only used by Jacki Smart Connect.</summary>
    public bool ProactiveSwitching { get; init; } = true;

    public bool MuxBenchmark { get; init; } = true;
}

public sealed record OwnershipPolicy
{
    /// <summary>Subscription hosts (exact or parent domain) that belong to the owner.</summary>
    public IReadOnlyList<string> AllowedProviderDomains { get; init; } = [];

    public string MarkerHeaderName { get; init; } = ProductInfo.DefaultProviderMarkerHeader;
    public string MarkerHeaderValue { get; init; } = ProductInfo.DefaultProviderMarkerValue;

    /// <summary>When true the header marker only counts if the host is also in <see cref="AllowedProviderDomains"/>.</summary>
    public bool HeaderRequiresAllowedDomain { get; init; }

    /// <summary>Header markers are only trusted over HTTPS.</summary>
    public bool RequireHttpsForHeader { get; init; } = true;

    public string ManifestHeaderName { get; init; } = ProductInfo.DefaultProviderManifestHeader;

    /// <summary>ECDSA P-256 public key (PEM, SubjectPublicKeyInfo) that signs provider manifests.</summary>
    public string? ManifestPublicKeyPem { get; init; }

    /// <summary>Fallback for single configs: server/SNI/Host names that belong to the owner.</summary>
    public IReadOnlyList<string> SingleConfigAllowedHosts { get; init; } = [];
}

public sealed record NotificationSettings
{
    public IReadOnlyList<NotificationCategory> Disabled { get; init; } = [];
    public int TrafficWarningPercent { get; init; } = 90;

    public bool IsEnabled(NotificationCategory c) => !Disabled.Contains(c);
}

public sealed record StartupSettings
{
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool AutoConnect { get; init; }
    public bool ReconnectLastServer { get; init; } = true;
    public bool UseSmartConnect { get; init; } = true;
}

/// <summary>A node's place on the routing editor's canvas.</summary>
public sealed record NodePoint(double X, double Y);

public sealed record UiSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>The first-start tour was shown (or skipped); it can be opened again from About.</summary>
    public bool TourSeen { get; init; }

    /// <summary>Where the node editor placed each node (rules "r:{id}", conditions "c:{id}:{n}", actions "a:{type}").</summary>
    public IReadOnlyDictionary<string, NodePoint> RoutingLayout { get; init; } = new Dictionary<string, NodePoint>(StringComparer.Ordinal);
    public bool CloseToTray { get; init; } = true;

    /// <summary>Force reduced motion regardless of the Windows setting.</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>"auto" (follow the device), "en" or "fa" (Persian, right-to-left).</summary>
    public string Language { get; init; } = "auto";

    /// <summary>The phone app's first-run introduction was shown.</summary>
    public bool OnboardingDone { get; init; }

    /// <summary>Android: detect games via usage access (optional; streaming is detected from traffic).</summary>
    public bool DetectGames { get; init; }

    public NotchSettings Notch { get; init; } = new();

    public IslandSettings Island { get; init; } = new();
}

/// <summary>
/// Android: a floating "dynamic island" over other apps (needs "Display over other apps") with the IBOX eye, the
/// connection state and the live ping; tap to expand, drag anywhere, placed where the user wants it.
/// </summary>
public sealed record IslandSettings
{
    public bool Enabled { get; init; }

    public bool ShowStatus { get; init; } = true;
    public bool ShowPing { get; init; } = true;
    public bool ShowServer { get; init; }
    public bool ShowCountry { get; init; }
    public bool ShowDuration { get; init; }

    /// <summary>Centre of the island across the screen, 0 = left edge … 1 = right edge.</summary>
    public double X { get; init; } = 0.5;

    /// <summary>Top of the island down the screen, 0 = top edge … 1 = bottom edge.</summary>
    public double Y { get; init; } = 0.012;

    public int FadeAfterSeconds { get; init; } = 6;

    /// <summary>Hidden while IBOX itself is open (its own screen already shows the state).</summary>
    public bool HideInApp { get; init; } = true;
}

/// <summary>
/// Windows: the small black "notch" at the top of the screen — the IBOX eye with the connection state and live
/// ping. Hover shows more; it fades after a while and can be dragged along the top edge.
/// </summary>
public sealed record NotchSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Collapsed contents (the defaults: connection state and live ping).</summary>
    public bool ShowStatus { get; init; } = true;
    public bool ShowPing { get; init; } = true;
    public bool ShowTunnel { get; init; }
    public bool ShowServer { get; init; }
    public bool ShowCountry { get; init; }
    public bool ShowDuration { get; init; }

    /// <summary>What is playing (music, video) with play/pause, next and previous.</summary>
    public bool ShowNowPlaying { get; init; } = true;

    /// <summary>Horizontal position on the top edge, 0 = left … 1 = right.</summary>
    public double Position { get; init; } = 0.5;

    /// <summary>The monitor it sits on ("x,y" of that monitor's top-left corner); null = the main one.</summary>
    public string? Display { get; init; }

    /// <summary>Seconds without the pointer before it fades out of the way (0 = never).</summary>
    public int FadeAfterSeconds { get; init; } = 6;

    /// <summary>Hidden while a full-screen app (game, video, presentation) is in front.</summary>
    public bool HideInFullScreen { get; init; } = true;

    /// <summary>A soft chime when the connection is made.</summary>
    public bool Sound { get; init; } = true;
}

public sealed record LoggingSettings
{
    public AppLogLevel MinimumLevel { get; init; } = AppLogLevel.Information;
    public int RetainDays { get; init; } = 7;
}

public sealed record PrivacySettings
{
    /// <summary>Off by default; nothing is sent without explicit consent.</summary>
    public bool TelemetryEnabled { get; init; }

    public bool CrashReportsEnabled { get; init; }
}

public sealed record SubscriptionSettings
{
    public AutoUpdateInterval DefaultAutoUpdate { get; init; } = AutoUpdateInterval.ProviderDefault;
    public long MaxResponseBytes { get; init; } = 10 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>User-Agent for subscription requests. Must not look like a browser (would get HTML).</summary>
    public string UserAgent { get; init; } = $"{ProductInfo.UserAgentProduct}/1.0";
}

[JsonConverter(typeof(JsonStringEnumConverter<TunStack>))]
public enum TunStack
{
    /// <summary>Windows TCP stack for TCP (fastest, OS autotuning) + gVisor for UDP. sing-box default.</summary>
    Mixed = 0,
    System,
    Gvisor,
}

[JsonConverter(typeof(JsonStringEnumConverter<QuicPolicy>))]
public enum QuicPolicy
{
    /// <summary>Block QUIC (UDP 443) only through TCP-based servers, so browsers use TCP instead of slow UDP-over-TCP.</summary>
    Auto = 0,
    Block,
    Allow,
}

public sealed record PerformanceSettings
{
    public TunStack TunStack { get; init; } = TunStack.Mixed;

    public QuicPolicy Quic { get; init; } = QuicPolicy.Auto;

    /// <summary>
    /// uTLS fingerprint for TLS servers whose config names none (Go's own TLS hello is easy to spot).
    /// Empty = leave such configs unchanged. REALITY always uses uTLS.
    /// </summary>
    public string DefaultFingerprint { get; init; } = "chrome";

    /// <summary>
    /// Per server, measure TLS record fragmentation against TCP fragmentation (and MUX on/off) and use the
    /// fastest variant that works reliably. Results are cached and dropped when the server starts failing.
    /// </summary>
    public bool AutoTune { get; init; } = true;

    /// <summary>
    /// Gaming/streaming optimization: while a game or stream is detected, no background server tests, tuning or
    /// proactive server moves run (they cause latency spikes and dropped sessions) and the core gets CPU priority.
    /// </summary>
    public ActivityOptimization Activity { get; init; } = ActivityOptimization.Auto;
}

/// <summary>Which apps use the VPN (Android VpnService per-app routing).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SplitTunnelMode>))]
public enum SplitTunnelMode
{
    AllApps = 0,

    /// <summary>Every app except the selected ones (e.g. banking apps that refuse VPNs).</summary>
    ExcludeSelected,

    /// <summary>Only the selected apps (e.g. just Telegram and a browser).</summary>
    OnlySelected,
}

public sealed record SplitTunnelSettings
{
    public SplitTunnelMode Mode { get; init; } = SplitTunnelMode.AllApps;

    /// <summary>Android package names.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];
}

/// <summary>
/// Android "proxy only" mode: no VPN, just a local SOCKS5/HTTP proxy on 127.0.0.1 that apps are pointed at
/// (Telegram, browsers, Wi-Fi proxy setting). With a password by default — any app on the phone can reach
/// loopback ports; without one, any app could use the proxy or read the VPN exit.
/// </summary>
public sealed record MobileProxySettings
{
    public bool RequirePassword { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }
}

/// <summary>
/// Sharing subscriptions between the user's own devices on the same local network ("Sync device"). Enabled means
/// this device answers scans from the user's other devices while the app is open; nothing is ever imported
/// without the user accepting on this device. The user can turn it off.
/// </summary>
public sealed record EcosystemSettings
{
    public bool Enabled { get; init; } = true;
}

/// <summary>App updates from the signed feed (ProductInfo.UpdateFeedUrl).</summary>
public sealed record UpdateSettings
{
    /// <summary>Check automatically (at start and every few hours). Installing always needs the user.</summary>
    public bool AutoCheck { get; init; } = true;

    /// <summary>A version the user chose to skip (not offered again; newer ones are).</summary>
    public string? SkippedVersion { get; init; }
}

public sealed record DeviceSettings
{
    /// <summary>Random per-installation device ID sent to device-limited providers (see DeviceIdentity).</summary>
    public string? Hwid { get; init; }
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ConnectionSettings Connection { get; init; } = new();
    public PerformanceSettings Performance { get; init; } = new();
    public DnsSettings Dns { get; init; } = new();
    public Ipv6Mode Ipv6 { get; init; } = Ipv6Mode.Auto;
    public KillSwitchSettings KillSwitch { get; init; } = new();
    public RoutingSettings Routing { get; init; } = new();
    public SmartSettings Smart { get; init; } = new();
    public ProbeSettings Probe { get; init; } = new();
    public OwnershipPolicy Ownership { get; init; } = new();
    public NotificationSettings Notifications { get; init; } = new();
    public StartupSettings Startup { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    public LoggingSettings Logging { get; init; } = new();
    public PrivacySettings Privacy { get; init; } = new();
    public SubscriptionSettings Subscriptions { get; init; } = new();
    public DeviceSettings Device { get; init; } = new();
    public SplitTunnelSettings SplitTunnel { get; init; } = new();
    public MobileProxySettings MobileProxy { get; init; } = new();
    public EcosystemSettings Ecosystem { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    public UsageSettings Usage { get; init; } = new();
    public TrafficControlSettings Traffic { get; init; } = new();
    public WorkplaceSettings Workplace { get; init; } = new();

    /// <summary>The standalone second VPN (Dual VPN) to a company or a server, in every mode.</summary>
    public SecondVpnSettings DualVpn { get; init; } = new();
    public ConnectivitySettings Connectivity { get; init; } = new();

    /// <summary>Control by the command line and AI agents (MCP). Off by default.</summary>
    public AgentSettings Agent { get; init; } = new();
}

/// <summary>
/// The local control channel for the command line and AI agents (MCP). Only the same OS user can reach it. Reading
/// (status, servers, rules) needs <see cref="Enabled"/>; changing anything also needs <see cref="AllowChanges"/>.
/// </summary>
public sealed record AgentSettings
{
    public bool Enabled { get; init; }

    public bool AllowChanges { get; init; }
}
