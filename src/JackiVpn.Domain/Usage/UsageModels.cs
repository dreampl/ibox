using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Usage;

/// <summary>How the usage period (for the data cap) is counted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageCycle>))]
public enum UsageCycle
{
    /// <summary>Solar Hijri (Persian) calendar months.</summary>
    Jalali = 0,
    Gregorian,

    /// <summary>A custom start date and length (e.g. a 30-day package bought mid-month).</summary>
    Custom,
}

/// <summary>What happens when the period cap is used up.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapAction>))]
public enum CapAction
{
    Notify = 0,

    /// <summary>Switch to Eco mode automatically.</summary>
    Eco,
}

public sealed record UsageSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Data cap of the period in GB (0 = none).</summary>
    public double CapGb { get; init; }

    /// <summary>Daily cap in GB (0 = none).</summary>
    public double DailyGb { get; init; }

    public UsageCycle Cycle { get; init; } = UsageCycle.Jalali;

    /// <summary>First day of a custom period.</summary>
    public DateOnly? CycleStart { get; init; }

    public int CycleLength { get; init; } = 30;

    /// <summary>Free hours (e.g. night packages): traffic between these local hours does not count (equal = none).</summary>
    public int FreeFromHour { get; init; }

    public int FreeToHour { get; init; }

    public bool Alerts { get; init; } = true;
    public IReadOnlyList<int> AlertPercents { get; init; } = [80, 100];

    /// <summary>Spike alert: one app moved more than this many MB within <see cref="SpikeMinutes"/>.</summary>
    public int SpikeMb { get; init; } = 300;

    public int SpikeMinutes { get; init; } = 5;
    public bool AlertNewApp { get; init; } = true;

    /// <summary>Alert when an app uploads much more than it downloads in the background (sync, backup or malware).</summary>
    public bool AlertHeavyUpload { get; init; } = true;

    /// <summary>Alert when automatic updates start downloading.</summary>
    public bool AlertUpdates { get; init; } = true;

    /// <summary>Per-app daily limits in MB (key = app name as shown in the usage list).</summary>
    public IReadOnlyDictionary<string, int> AppDailyLimitsMb { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public CapAction OnCap { get; init; } = CapAction.Notify;

    /// <summary>Local-network traffic does not count toward the cap.</summary>
    public bool ExcludeLan { get; init; } = true;

    public int KeepDays { get; init; } = 120;
}

/// <summary>Traffic profile of the whole device.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrafficMode>))]
public enum TrafficMode
{
    Normal = 0,

    /// <summary>Saves data: no automatic updates, telemetry or cloud sync; selected apps are cut.</summary>
    Eco,

    /// <summary>A game has priority: background downloads are slowed and updates paused.</summary>
    Gaming,

    /// <summary>A stream has priority: other apps are slowed and updates paused.</summary>
    Streaming,

    /// <summary>On a workplace network: internal traffic stays local, updates and heavy personal traffic are held back.</summary>
    Work,
}

/// <summary>Families of automatic updates that can be held back inside the tunnel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateGroup>))]
public enum UpdateGroup
{
    Windows = 0,
    Office,
    Browsers,
    Games,
    Apple,
    Android,
    Linux,
    Apps,
    Drivers,
    CloudSync,
}

/// <summary>Limit or block for one app (Windows/Linux executable name, e.g. "onedrive.exe").</summary>
public sealed record AppRule
{
    public required string App { get; init; }

    /// <summary>Download limit in kbit/s (0 = none).</summary>
    public int DownKbps { get; init; }

    /// <summary>Upload limit in kbit/s (0 = none).</summary>
    public int UpKbps { get; init; }

    /// <summary>Internet cut completely (through the tunnel).</summary>
    public bool Blocked { get; init; }
}

public sealed record TrafficControlSettings
{
    /// <summary>
    /// Smart traffic control: connections pass a small in-app relay so modes and speed limits apply instantly,
    /// without reconnecting (Windows/Linux). Off = only blocks are applied.
    /// </summary>
    public bool SmartControl { get; init; } = true;

    /// <summary>Mode chosen by the user (Gaming/Streaming/Work can also switch on automatically).</summary>
    public TrafficMode Mode { get; init; } = TrafficMode.Normal;

    public bool AutoGaming { get; init; } = true;
    public bool AutoWork { get; init; } = true;

    public IReadOnlyList<AppRule> Apps { get; init; } = [];

    /// <summary>Apps cut while Eco mode is on.</summary>
    public IReadOnlyList<string> SaverApps { get; init; } = [];

    /// <summary>Apps never slowed by Gaming/Streaming priority (the user's own games, voice chat…).</summary>
    public IReadOnlyList<string> PriorityApps { get; init; } = [];

    /// <summary>Update families held back in every mode.</summary>
    public IReadOnlyList<UpdateGroup> BlockedUpdates { get; init; } = [];

    /// <summary>Update families held back in Eco and Work mode.</summary>
    public IReadOnlyList<UpdateGroup> EcoUpdates { get; init; } =
        [UpdateGroup.Windows, UpdateGroup.Office, UpdateGroup.Browsers, UpdateGroup.Games, UpdateGroup.Apple, UpdateGroup.Android, UpdateGroup.Apps, UpdateGroup.Drivers, UpdateGroup.CloudSync];

    public IReadOnlyList<string> BlockedDomains { get; init; } = [];
    public IReadOnlyList<string> BlockedIps { get; init; } = [];

    /// <summary>Download speed left to other apps while a game runs (kbit/s).</summary>
    public int BackgroundKbpsWhileGaming { get; init; } = 3000;

    public int BackgroundKbpsWhileStreaming { get; init; } = 6000;
}

/// <summary>Facts about the network the device is on (used to recognise a workplace).</summary>
public sealed record NetworkFacts
{
    /// <summary>Stable identifier of this network (hash of gateway, DNS suffix and Wi-Fi name).</summary>
    public string Key { get; init; } = string.Empty;

    public string? Ssid { get; init; }
    public string? DnsSuffix { get; init; }

    /// <summary>Active Directory domain of the computer (domain-joined Windows).</summary>
    public string? AdDomain { get; init; }

    public string? GatewayIp { get; init; }
    public string? GatewayMac { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = [];

    /// <summary>Subnets of the local interface (CIDR).</summary>
    public IReadOnlyList<string> LocalCidrs { get; init; } = [];

    public bool IsWifi { get; init; }

    public static NetworkFacts None { get; } = new();
}

/// <summary>Rules a company sets for its network (from a signed policy or entered by the user).</summary>
public sealed record CompanyRules
{
    public IReadOnlyList<string> BlockApps { get; init; } = [];
    public IReadOnlyList<string> BlockDomains { get; init; } = [];
    public IReadOnlyList<UpdateGroup> BlockUpdates { get; init; } = [];
    public int DailyCapMb { get; init; }

    /// <summary>Download speed per non-priority app (kbit/s, 0 = none).</summary>
    public int BackgroundKbps { get; init; }
}

/// <summary>A recognised workplace network: internal names and subnets stay outside the tunnel, Work mode applies.</summary>
public sealed record WorkplaceProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }

    public IReadOnlyList<string> NetworkKeys { get; init; } = [];
    public IReadOnlyList<string> DnsSuffixes { get; init; } = [];
    public IReadOnlyList<string> Ssids { get; init; } = [];
    public IReadOnlyList<string> GatewayMacs { get; init; } = [];

    public IReadOnlyList<string> InternalDomains { get; init; } = [];
    public IReadOnlyList<string> InternalCidrs { get; init; } = [];

    /// <summary>The company's resolvers for internal names (IP literals).</summary>
    public IReadOnlyList<string> InternalDns { get; init; } = [];

    public bool WorkMode { get; init; } = true;
    public CompanyRules Rules { get; init; } = new();

    /// <summary>SHA-256 of the public key that signed the company policy (pinned on first import).</summary>
    public string? PolicyKeySha256 { get; init; }

    /// <summary>HTTPS address the signed policy is refreshed from.</summary>
    public string? PolicyUrl { get; init; }

    public DateTimeOffset? PolicyIssuedUtc { get; init; }
    public DateTimeOffset? PolicyExpiresUtc { get; init; }

    /// <summary>Away from the workplace, in Work mode: company traffic takes the work VPN, the rest the normal one.</summary>
    public DualVpnSettings DualVpn { get; init; } = new();
}

/// <summary>How company traffic reaches the company while another VPN carries everything else.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<WorkRoute>))]
public enum WorkRoute
{
    /// <summary>No second VPN: internal names and subnets go direct.</summary>
    Off = 0,

    /// <summary>A saved server (the company's own VLESS/WireGuard/… config) carries company traffic.</summary>
    Server,

    /// <summary>The company's own VPN app (OpenVPN, AnyConnect, FortiClient…) is connected: use its network adapter.</summary>
    Adapter,
}

/// <summary>Dual VPN: company traffic over the work VPN while the normal VPN carries everything else.</summary>
public sealed record DualVpnSettings
{
    public WorkRoute Route { get; init; }

    /// <summary>The saved server that carries company traffic (<see cref="WorkRoute.Server"/>).</summary>
    public string? ServerId { get; init; }

    /// <summary>The network adapter of the company's VPN app (<see cref="WorkRoute.Adapter"/>).</summary>
    public string? Interface { get; init; }

    public bool IsOn => Route switch
    {
        WorkRoute.Server => !string.IsNullOrWhiteSpace(ServerId),
        WorkRoute.Adapter => !string.IsNullOrWhiteSpace(Interface),
        _ => false,
    };
}

/// <summary>What the standalone second VPN is (Dual VPN, any mode).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SecondVpnKind>))]
public enum SecondVpnKind
{
    /// <summary>A WireGuard config (wg-quick .conf): IBOX's own core runs it next to the main connection.</summary>
    WireGuard = 0,

    /// <summary>IPsec (IKEv2 or L2TP/IPsec): the system's own VPN client, set up and dialled by IBOX.</summary>
    Ipsec,

    /// <summary>Kerio Control: the Kerio VPN Client app is connected; IBOX sends company traffic through its adapter.</summary>
    Kerio,

    /// <summary>Another company VPN app (OpenVPN, AnyConnect, FortiClient…): its adapter.</summary>
    App,

    /// <summary>A saved server (VLESS, VMess, Trojan, …).</summary>
    Server,
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<IpsecType>))]
public enum IpsecType
{
    Ikev2 = 0,
    L2tp,
}

/// <summary>
/// Dual VPN on its own: a second VPN to a company or a server (WireGuard, IPsec, Kerio, another VPN app or a saved
/// server) next to the IBOX connection, in every mode. Only the given names and subnets take it.
/// </summary>
public sealed record SecondVpnSettings
{
    public bool Enabled { get; init; }

    public string Name { get; init; } = "Company VPN";

    public SecondVpnKind Kind { get; init; }

    /// <summary>The saved server that carries it (WireGuard: its imported config; Server: the chosen one).</summary>
    public string? ServerId { get; init; }

    /// <summary>The network adapter it uses (IPsec: the system connection's; Kerio/App: the app's).</summary>
    public string? Interface { get; init; }

    /// <summary>IPsec gateway (name or address).</summary>
    public string? Host { get; init; }

    /// <summary>The gateway's addresses when the IPsec connection was set up: they never enter the tunnel.</summary>
    public IReadOnlyList<string> GatewayIps { get; init; } = [];

    public IpsecType IpsecType { get; init; }

    public string? Username { get; init; }

    /// <summary>Password and pre-shared key, encrypted with this device's key (never stored in plain text).</summary>
    public string? Secret { get; init; }

    /// <summary>Names that take the second VPN (corp.example.com covers its subdomains).</summary>
    public IReadOnlyList<string> Domains { get; init; } = [];

    /// <summary>Subnets that take the second VPN.</summary>
    public IReadOnlyList<string> Cidrs { get; init; } = [];

    /// <summary>The company's resolvers for those names (IP literals, optional).</summary>
    public IReadOnlyList<string> Dns { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public WorkRoute Route => Kind is SecondVpnKind.WireGuard or SecondVpnKind.Server ? WorkRoute.Server : WorkRoute.Adapter;

    /// <summary>Set up far enough to carry traffic.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => (Domains.Count > 0 || Cidrs.Count > 0)
        && (Route == WorkRoute.Server ? !string.IsNullOrWhiteSpace(ServerId)
            : Kind == SecondVpnKind.Ipsec ? !string.IsNullOrWhiteSpace(Host) : !string.IsNullOrWhiteSpace(Interface));

    [System.Text.Json.Serialization.JsonIgnore]
    public string KindLabel => Kind switch
    {
        SecondVpnKind.WireGuard => "WireGuard",
        SecondVpnKind.Ipsec => IpsecType == IpsecType.L2tp ? "L2TP/IPsec" : "IKEv2/IPsec",
        SecondVpnKind.Kerio => "Kerio",
        SecondVpnKind.App => "VPN app",
        _ => "Server",
    };
}

public sealed record WorkplaceSettings
{
    public bool Detect { get; init; } = true;
    public IReadOnlyList<WorkplaceProfile> Profiles { get; init; } = [];

    /// <summary>Networks the user said are not a workplace (no suggestion again).</summary>
    public IReadOnlyList<string> DismissedNetworks { get; init; } = [];
}

/// <summary>Ways the app gets through when the normal path to a server is blocked.</summary>
public sealed record ConnectivitySettings
{
    /// <summary>Resolve server names with encrypted DNS when the network's DNS is poisoned or blocked.</summary>
    public bool SecureDnsRescue { get; init; } = true;

    /// <summary>Find reachable Cloudflare addresses for CDN (WebSocket/gRPC/HTTP) configs whose usual address is filtered.</summary>
    public bool CleanIpScan { get; init; } = true;

    /// <summary>Try other browser TLS fingerprints when a REALITY/TLS handshake is blocked.</summary>
    public bool FingerprintRotation { get; init; } = true;

    /// <summary>
    /// Look up server addresses with the fastest clean resolver found on this network (DNS search), unless a
    /// bootstrap resolver was chosen by hand.
    /// </summary>
    public bool AutoPickDns { get; init; } = true;
}

/// <summary>Speed limit of one shaping class (0 = unlimited; <see cref="Block"/> refuses new connections).</summary>
public sealed record ShapeRate(long DownBytesPerSecond, long UpBytesPerSecond, bool Block = false)
{
    public static ShapeRate Unlimited { get; } = new(0, 0);
    public static ShapeRate Blocked { get; } = new(0, 0, true);

    public static ShapeRate FromKbps(int downKbps, int upKbps) => new(Math.Max(0, downKbps) * 125L, Math.Max(0, upKbps) * 125L);

    public bool IsUnlimited => !Block && DownBytesPerSecond <= 0 && UpBytesPerSecond <= 0;
}

/// <summary>Names of the shaping classes the core routes to the in-app relay.</summary>
public static class ShapeClasses
{
    public const string Background = "bg";
    public const string Telemetry = "tel";

    /// <summary>Non-browser media players (below browsing).</summary>
    public const string Media = "media";

    /// <summary>Download managers and torrent clients (the lowest).</summary>
    public const string Downloads = "dl";

    public static string App(int index) => "app" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Update(UpdateGroup group) => "u-" + group.ToString().ToLowerInvariant();
}

/// <summary>
/// The static part of traffic control that is written into the core configuration (changes need a reconnect).
/// Everything here applies to traffic that passes the tunnel's core (TUN: the whole device).
/// </summary>
public sealed record TrafficPolicy
{
    /// <summary>Executable names (or full paths) whose connections are refused.</summary>
    public IReadOnlyList<string> BlockedApps { get; init; } = [];

    public IReadOnlyList<string> BlockedDomains { get; init; } = [];
    public IReadOnlyList<string> BlockedCidrs { get; init; } = [];

    /// <summary>Workplace names/subnets: always direct, resolved by <see cref="InternalDns"/>.</summary>
    public IReadOnlyList<string> InternalDomains { get; init; } = [];

    public IReadOnlyList<string> InternalCidrs { get; init; } = [];
    public IReadOnlyList<string> InternalDns { get; init; } = [];

    /// <summary>Dual VPN: how <see cref="InternalDomains"/> / <see cref="InternalCidrs"/> are carried (Off = direct).</summary>
    public WorkRoute WorkRoute { get; init; }

    /// <summary>The saved server for <see cref="WorkRoute.Server"/> (its id; the session start fills <see cref="WorkServer"/>).</summary>
    public string? WorkServerId { get; init; }

    public Profiles.ProxyProfile? WorkServer { get; init; }

    /// <summary>The company VPN app's network adapter for <see cref="WorkRoute.Adapter"/>.</summary>
    public string? WorkInterface { get; init; }

    /// <summary>Addresses of the second VPN's own gateway (CIDRs): never enter the tunnel, always direct.</summary>
    public IReadOnlyList<string> WorkBypass { get; init; } = [];

    /// <summary>Connections of the classes below pass the in-app relay (limits change at run time).</summary>
    public bool Shaping { get; init; }

    /// <summary>Hosts sent to the relay by name (automatic updates per family, telemetry).</summary>
    public IReadOnlyList<DomainClass> DomainClasses { get; init; } = [];

    /// <summary>TCP of every app except <see cref="PriorityApps"/> goes through the relay (class "bg").</summary>
    public bool ShapeBackground { get; init; }

    public IReadOnlyList<string> PriorityApps { get; init; } = [];

    /// <summary>Apps with their own limit (class "app{index}").</summary>
    public IReadOnlyList<string> LimitedApps { get; init; } = [];

    /// <summary>Media players that are not browsers (class "media", below browsing in Gaming mode).</summary>
    public IReadOnlyList<string> MediaApps { get; init; } = [];

    /// <summary>Download managers and torrent clients (class "dl", the lowest, with updates).</summary>
    public IReadOnlyList<string> DownloadApps { get; init; } = [];

    /// <summary>Ask the core to look up the owning process of every connection (per-app usage).</summary>
    public bool TrackProcesses { get; init; }

    public bool IsEmpty =>
        BlockedApps.Count == 0 && BlockedDomains.Count == 0 && BlockedCidrs.Count == 0 && InternalDomains.Count == 0 && InternalCidrs.Count == 0
        && WorkRoute == WorkRoute.Off && WorkBypass.Count == 0 && !Shaping && !TrackProcesses;
}

/// <summary>A relay class selected by destination name.</summary>
public sealed record DomainClass(string Class, IReadOnlyList<string> Domains);

/// <summary>The run-time part of traffic control (applied to the relay without reconnecting).</summary>
public sealed record TrafficLimits
{
    public IReadOnlyDictionary<string, ShapeRate> Classes { get; init; } = new Dictionary<string, ShapeRate>(StringComparer.Ordinal);

    /// <summary>Executable names that are never slowed in class "bg" (the running game and its helpers).</summary>
    public IReadOnlyList<string> PriorityProcesses { get; init; } = [];

    /// <summary>Install folders whose programs are never slowed (a game's own launcher, anti-cheat, crash reporter).</summary>
    public IReadOnlyList<string> PriorityFolders { get; init; } = [];

    public static TrafficLimits None { get; } = new();

    public ShapeRate For(string shapeClass) => Classes.TryGetValue(shapeClass, out var r) ? r : ShapeRate.Unlimited;
}

/// <summary>One open connection as the core reports it.</summary>
public sealed record CoreConnection
{
    public required string Id { get; init; }
    public string Network { get; init; } = "tcp";
    public string? Host { get; init; }
    public string? DestinationIp { get; init; }
    public int DestinationPort { get; init; }
    public string? ProcessPath { get; init; }
    public long Upload { get; init; }
    public long Download { get; init; }

    /// <summary>Outbound tag the connection uses (proxy, direct, a relay class…).</summary>
    public string Outbound { get; init; } = string.Empty;

    public string Inbound { get; init; } = string.Empty;
    public DateTimeOffset StartUtc { get; init; }
}

public sealed record CoreConnections(long UploadTotal, long DownloadTotal, IReadOnlyList<CoreConnection> Connections);
