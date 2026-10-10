using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter<ProxyProtocol>))]
public enum ProxyProtocol
{
    Unknown = 0,
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Socks,
    Http,
    Hysteria2,
    Tuic,
    WireGuard,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransportType>))]
public enum TransportType
{
    Tcp = 0,
    WebSocket,
    Grpc,
    HttpUpgrade,
    /// <summary>HTTP/2 transport ("h2" / "http" in cores).</summary>
    Http2,
    Quic,
    /// <summary>Xray XHTTP (a.k.a. SplitHTTP). Xray-only.</summary>
    XHttp,
    /// <summary>mKCP. Xray-only.</summary>
    Kcp,
}

[JsonConverter(typeof(JsonStringEnumConverter<IpPreference>))]
public enum IpPreference
{
    Default = 0,
    PreferIPv4,
    PreferIPv6,
    IPv4Only,
    IPv6Only,
}

[JsonConverter(typeof(JsonStringEnumConverter<MuxMode>))]
public enum MuxMode
{
    Auto = 0,
    Off,
    On,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConfigFormat>))]
public enum ConfigFormat
{
    Unknown = 0,
    UriList,
    Base64UriList,
    SingBoxFull,
    SingBoxOutbound,
    XrayFull,
    XrayOutbound,
    ClashYaml,
    ClashMetaYaml,
    Html,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileOriginKind>))]
public enum ProfileOriginKind
{
    Manual = 0,
    Subscription,
    File,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileSyncState>))]
public enum ProfileSyncState
{
    Unchanged = 0,
    New,
    Changed,
    /// <summary>The provider no longer lists this node. Kept (and excluded from smart selection) until the user removes it.</summary>
    RemovedByProvider,
}

/// <summary>Identity/credential fields. Every value here is a secret and must never be logged.</summary>
public sealed record ProxyCredentials
{
    /// <summary>VLESS / VMess / TUIC user id.</summary>
    public string? Uuid { get; init; }

    /// <summary>Trojan / Shadowsocks / Hysteria2 / TUIC / SOCKS / HTTP password.</summary>
    public string? Password { get; init; }

    /// <summary>SOCKS / HTTP username.</summary>
    public string? Username { get; init; }

    /// <summary>VLESS flow, e.g. <c>xtls-rprx-vision</c>.</summary>
    public string? Flow { get; init; }

    /// <summary>VLESS encryption (normally <c>none</c>) or VMess security (<c>auto</c>, <c>aes-128-gcm</c>, …).</summary>
    public string? Encryption { get; init; }

    /// <summary>VMess legacy alterId.</summary>
    public int? AlterId { get; init; }

    /// <summary>VLESS/VMess UDP packet encoding (<c>xudp</c>, <c>packetaddr</c>).</summary>
    public string? PacketEncoding { get; init; }
}

public sealed record TransportOptions
{
    public TransportType Type { get; init; } = TransportType.Tcp;

    /// <summary>WS / HTTPUpgrade / H2 / XHTTP path. Always starts with '/' after normalization.</summary>
    public string? Path { get; init; }

    /// <summary>Host header (WS / HTTPUpgrade / XHTTP) or comma separated host list (H2).</summary>
    public string? Host { get; init; }

    /// <summary>Extra request headers except Host.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>gRPC service name.</summary>
    public string? ServiceName { get; init; }

    /// <summary>gRPC multi mode (Xray).</summary>
    public bool GrpcMultiMode { get; init; }

    /// <summary>WS early data size (sing-box <c>max_early_data</c>, Xray <c>ed</c> query).</summary>
    public int? MaxEarlyData { get; init; }

    public string? EarlyDataHeaderName { get; init; }

    /// <summary>HTTP method for H2 transport.</summary>
    public string? Method { get; init; }

    /// <summary>TCP header obfuscation type (<c>none</c>/<c>http</c>) or mKCP header type.</summary>
    public string? HeaderType { get; init; }

    /// <summary>XHTTP mode (<c>auto</c>, <c>packet-up</c>, <c>stream-up</c>, <c>stream-one</c>).</summary>
    public string? XHttpMode { get; init; }

    /// <summary>mKCP seed (secret).</summary>
    public string? KcpSeed { get; init; }

    /// <summary>Unknown transport fields preserved verbatim from the source.</summary>
    public JsonObject? Extensions { get; init; }
}

public sealed record TlsFragmentOptions
{
    public bool Enabled { get; init; }

    /// <summary>sing-box <c>fragment_fallback_delay</c> (used when the wait time cannot be auto-detected).</summary>
    public TimeSpan? FallbackDelay { get; init; }

    /// <summary>sing-box <c>record_fragment</c>: split the ClientHello into multiple TLS records.</summary>
    public bool RecordFragment { get; init; }

    /// <summary>Xray fragment <c>packets</c> (e.g. <c>tlshello</c> or <c>1-3</c>). Preserved for Xray.</summary>
    public string? Packets { get; init; }

    /// <summary>Xray fragment <c>length</c> (e.g. <c>100-200</c>).</summary>
    public string? Length { get; init; }

    /// <summary>Xray fragment <c>interval</c> in ms (e.g. <c>10-20</c>).</summary>
    public string? Interval { get; init; }
}

public sealed record RealityOptions
{
    public bool Enabled { get; init; }

    /// <summary>Server public key (not secret by itself, but treated as sensitive in logs).</summary>
    public string? PublicKey { get; init; }

    public string? ShortId { get; init; }

    public string? SpiderX { get; init; }
}

public sealed record EchOptions
{
    public bool Enabled { get; init; }
    public string? Config { get; init; }
    public string? QueryServerName { get; init; }
}

public sealed record TlsOptions
{
    public bool Enabled { get; init; }

    public string? ServerName { get; init; }

    public IReadOnlyList<string> Alpn { get; init; } = [];

    /// <summary>uTLS fingerprint (<c>chrome</c>, <c>firefox</c>, …). Null means the core's native TLS stack.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Accept any certificate. Off by default; when a source enables it the UI shows a security warning.</summary>
    public bool AllowInsecure { get; init; }

    public bool DisableSni { get; init; }

    public string? MinVersion { get; init; }

    public string? MaxVersion { get; init; }

    public TlsFragmentOptions? Fragment { get; init; }

    public RealityOptions? Reality { get; init; }

    public EchOptions? Ech { get; init; }

    /// <summary>Base64 SHA-256 public-key pins (sing-box <c>certificate_public_key_sha256</c>).</summary>
    public IReadOnlyList<string> PublicKeyPins { get; init; } = [];

    /// <summary>Hex SHA-256 of the leaf or CA certificate (Xray <c>pinnedPeerCertSha256</c>).</summary>
    public IReadOnlyList<string> CertificateSha256Pins { get; init; } = [];

    public JsonObject? Extensions { get; init; }

    public bool IsReality => Reality is { Enabled: true };
}

public sealed record MuxOptions
{
    /// <summary>Per-profile request. <see cref="MuxMode.Auto"/> defers to the global setting / benchmark.</summary>
    public MuxMode Mode { get; init; } = MuxMode.Auto;

    /// <summary>sing-box multiplex protocol: <c>smux</c>, <c>yamux</c>, <c>h2mux</c>.</summary>
    public string? Protocol { get; init; }

    public int? MaxConnections { get; init; }
    public int? MinStreams { get; init; }
    public int? MaxStreams { get; init; }
    public bool Padding { get; init; }

    /// <summary>Xray mux concurrency.</summary>
    public int? XrayConcurrency { get; init; }

    public int? XudpConcurrency { get; init; }

    /// <summary>Xray <c>xudpProxyUDP443</c> (<c>reject</c>/<c>allow</c>/<c>skip</c>).</summary>
    public string? XudpProxyUdp443 { get; init; }

    public JsonObject? Extensions { get; init; }
}

public sealed record ShadowsocksOptions
{
    public required string Method { get; init; }
    public string? Plugin { get; init; }
    public string? PluginOptions { get; init; }
    public bool UdpOverTcp { get; init; }
}

public sealed record Hysteria2Options
{
    public int? UpMbps { get; init; }
    public int? DownMbps { get; init; }
    public string? ObfsType { get; init; }

    /// <summary>Secret.</summary>
    public string? ObfsPassword { get; init; }

    /// <summary>Port hopping range, e.g. <c>20000:30000</c>.</summary>
    public IReadOnlyList<string> ServerPorts { get; init; } = [];

    public TimeSpan? HopInterval { get; init; }
}

public sealed record TuicOptions
{
    public string? CongestionControl { get; init; }
    public string? UdpRelayMode { get; init; }
    public bool ZeroRttHandshake { get; init; }
    public TimeSpan? Heartbeat { get; init; }
}

public sealed record WireGuardOptions
{
    /// <summary>Secret.</summary>
    public required string PrivateKey { get; init; }

    public required string PeerPublicKey { get; init; }

    /// <summary>Secret.</summary>
    public string? PreSharedKey { get; init; }

    public IReadOnlyList<string> LocalAddresses { get; init; } = [];

    public IReadOnlyList<int> Reserved { get; init; } = [];

    public int? Mtu { get; init; }

    public IReadOnlyList<string> AllowedIps { get; init; } = ["0.0.0.0/0", "::/0"];

    public int? PersistentKeepaliveSeconds { get; init; }
}

public sealed record ProfileOrigin
{
    public ProfileOriginKind Kind { get; init; } = ProfileOriginKind.Manual;
    public string? SubscriptionId { get; init; }
    public string? ProviderId { get; init; }
    public ConfigFormat SourceFormat { get; init; } = ConfigFormat.Unknown;

    /// <summary>The exact source text of this node (URI or JSON/YAML fragment). Secret: stored encrypted, never logged.</summary>
    public string? RawSource { get; init; }

    public DateTimeOffset ImportedUtc { get; init; }
}

public sealed record ProfileUserData
{
    public bool IsFavorite { get; init; }
    public string? CustomName { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? LastUsedUtc { get; init; }

    /// <summary>Per-profile core override (null = follow global preference).</summary>
    public Connection.CorePreference? CoreOverride { get; init; }
}

/// <summary>
/// Internal normalized representation of one proxy node. Every supported input format is converted to
/// this model first; config generators only ever read this model (plus preserved <see cref="Extensions"/>).
/// </summary>
public sealed record ProxyProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ProxyProtocol Protocol { get; init; }

    /// <summary>Server host name or IP literal (IPv6 without brackets).</summary>
    public required string Server { get; init; }

    public required int Port { get; init; }

    public ProxyCredentials Credentials { get; init; } = new();

    public TransportOptions Transport { get; init; } = new();

    public TlsOptions Tls { get; init; } = new();

    public MuxOptions? Mux { get; init; }

    public ShadowsocksOptions? Shadowsocks { get; init; }

    public Hysteria2Options? Hysteria2 { get; init; }

    public TuicOptions? Tuic { get; init; }

    public WireGuardOptions? WireGuard { get; init; }

    public IpPreference IpPreference { get; init; } = IpPreference.Default;

    /// <summary>Whether UDP should be relayed (SOCKS/Shadowsocks "udp" flags). Null = protocol default.</summary>
    public bool? Udp { get; init; }

    public ProfileOrigin Origin { get; init; } = new();

    public ProfileUserData User { get; init; } = new();

    public ProfileSyncState SyncState { get; init; } = ProfileSyncState.Unchanged;

    public DateTimeOffset? SyncStateSinceUtc { get; init; }

    /// <summary>Unknown top-level fields from the source, preserved so newer configs survive older parsers.</summary>
    public JsonObject? Extensions { get; init; }

    /// <summary>Stable logical fingerprint (see <see cref="ProfileIdentity"/>). Recomputed on import.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(User.CustomName) ? Name : User.CustomName!;

    [JsonIgnore]
    public bool IsUdpBasedProtocol => Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic or ProxyProtocol.WireGuard
        || Transport.Type is TransportType.Quic or TransportType.Kcp;
}
