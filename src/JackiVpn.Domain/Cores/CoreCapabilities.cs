using System.Text.Json.Serialization;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Domain.Cores;

[Flags]
public enum CoreFeature : long
{
    None = 0,
    Tun = 1L << 0,
    Vless = 1L << 1,
    Vmess = 1L << 2,
    Trojan = 1L << 3,
    Shadowsocks = 1L << 4,
    Socks = 1L << 5,
    Http = 1L << 6,
    Hysteria2 = 1L << 7,
    Tuic = 1L << 8,
    WireGuard = 1L << 9,
    Reality = 1L << 10,
    Utls = 1L << 11,
    TlsFragment = 1L << 12,
    TlsRecordFragment = 1L << 13,
    Mux = 1L << 14,
    ProcessRouting = 1L << 15,
    DnsHijack = 1L << 16,
    WebSocket = 1L << 17,
    Grpc = 1L << 18,
    HttpUpgrade = 1L << 19,
    Http2Transport = 1L << 20,
    QuicTransport = 1L << 21,
    XHttp = 1L << 22,
    Kcp = 1L << 23,
    RuleSets = 1L << 24,
    FakeIp = 1L << 25,
    TrafficStatsApi = 1L << 26,
    StdinConfig = 1L << 27,
    Ech = 1L << 28,
    VisionFlow = 1L << 29,
    TypedDnsServers = 1L << 30,
    RuleActions = 1L << 31,
    DomainResolver = 1L << 32,
    TunStrictRoute = 1L << 33,
    PortHopping = 1L << 34,
    ShadowsocksPlugins = 1L << 35,
    GeoDataFiles = 1L << 36,
    PublicKeyPinning = 1L << 37,
}

/// <summary>What a specific installed core binary can do. Derived from the real version/build tags, never assumed.</summary>
public sealed record CoreCapabilities(CoreKind Kind, CoreVersionInfo Version, CoreFeature Features)
{
    public bool Supports(CoreFeature feature) => (Features & feature) == feature;

    public bool SupportsTun => Supports(CoreFeature.Tun);
    public bool SupportsVless => Supports(CoreFeature.Vless);
    public bool SupportsReality => Supports(CoreFeature.Reality);
    public bool SupportsTlsFragment => Supports(CoreFeature.TlsFragment);
    public bool SupportsMux => Supports(CoreFeature.Mux);
    public bool SupportsWireGuard => Supports(CoreFeature.WireGuard);
    public bool SupportsHysteria2 => Supports(CoreFeature.Hysteria2);
    public bool SupportsProcessRouting => Supports(CoreFeature.ProcessRouting);
    public bool SupportsDnsHijack => Supports(CoreFeature.DnsHijack);
}

public sealed record CoreVersionInfo(CoreKind Kind, Version Version, string RawVersionText, IReadOnlyList<string> BuildTags)
{
    public string DisplayVersion => Version.ToString(3);

    public bool HasTag(string tag) => BuildTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityImpact>))]
public enum CapabilityImpact
{
    /// <summary>Profile works; the feature is merely informational.</summary>
    Info = 0,

    /// <summary>Profile works but loses a feature (e.g. TLS fragment). Must be shown to the user.</summary>
    Degraded,

    /// <summary>Profile cannot work on this core.</summary>
    Blocking,
}

public sealed record CapabilityIssue(CoreFeature Feature, CapabilityImpact Impact, string Message);

/// <summary>Outcome of negotiating one profile + session against one core.</summary>
public sealed record CapabilityReport(CoreKind Core, IReadOnlyList<CapabilityIssue> Issues)
{
    public bool IsUsable => Issues.All(i => i.Impact != CapabilityImpact.Blocking);

    public bool IsLossless => Issues.All(i => i.Impact == CapabilityImpact.Info);

    public int DegradedCount => Issues.Count(i => i.Impact == CapabilityImpact.Degraded);

    public IEnumerable<ValidationIssue> ToValidationIssues() => Issues.Select(i => new ValidationIssue(
        i.Impact switch
        {
            CapabilityImpact.Blocking => IssueSeverity.Error,
            CapabilityImpact.Degraded => IssueSeverity.Warning,
            _ => IssueSeverity.Info,
        },
        i.Feature.ToString(),
        i.Message));
}
