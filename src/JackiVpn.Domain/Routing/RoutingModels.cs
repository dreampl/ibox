using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Routing;

[JsonConverter(typeof(JsonStringEnumConverter<RuleConditionType>))]
public enum RuleConditionType
{
    Domain = 0,
    DomainSuffix,
    DomainKeyword,
    DomainRegex,
    IpCidr,
    GeoIp,
    GeoSite,
    RuleSet,
    PrivateIp,
    Protocol,
    Port,
    PortRange,
    ProcessName,
    ProcessPath,
    Network,

    /// <summary>Physical network type (wifi/ethernet/cellular). Not supported by Windows cores — kept for model completeness.</summary>
    NetworkType,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleActionType>))]
public enum RuleActionType
{
    Proxy = 0,
    Direct,
    Block,
    HijackDns,

    /// <summary>Route to a specific outbound / group tag.</summary>
    Outbound,
}

[JsonConverter(typeof(JsonStringEnumConverter<RoutingPreset>))]
public enum RoutingPreset
{
    Rule = 0,
    Global,
    Direct,
    IranFriendly,
    Custom,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleSetFormat>))]
public enum RuleSetFormat
{
    Binary = 0,
    Source,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleSetKind>))]
public enum RuleSetKind
{
    /// <summary>Only domain rules — safe to use in DNS rules.</summary>
    Domain = 0,

    /// <summary>Only IP rules.</summary>
    Ip,

    Mixed,
}

public sealed record RuleCondition(RuleConditionType Type, IReadOnlyList<string> Values);

public sealed record RuleAction(RuleActionType Type, string? OutboundTag = null)
{
    public static readonly RuleAction Proxy = new(RuleActionType.Proxy);
    public static readonly RuleAction Direct = new(RuleActionType.Direct);
    public static readonly RuleAction Block = new(RuleActionType.Block);
    public static readonly RuleAction HijackDns = new(RuleActionType.HijackDns);
}

/// <summary>
/// One routing rule. Matching semantics intentionally mirror sing-box so what the UI shows is what the core
/// does: all <i>destination</i> conditions (domain*, geosite, geoip, ip_cidr, private, rule_set) are OR-ed
/// as one group; every other condition group (port, protocol, process, network) is AND-ed with it.
/// Values inside one condition are OR-ed. <see cref="Invert"/> negates the whole rule.
/// </summary>
public sealed record RoutingRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<RuleCondition> Conditions { get; init; } = [];
    public RuleAction Action { get; init; } = RuleAction.Proxy;
    public bool Invert { get; init; }
    public string? Description { get; init; }

    /// <summary>Set when the rule was materialized from a preset (still fully editable).</summary>
    public RoutingPreset? FromPreset { get; init; }
}

public sealed record RuleSetDefinition
{
    /// <summary>Tag referenced by rules (<c>RuleSet</c>/<c>GeoIp</c>/<c>GeoSite</c> conditions).</summary>
    public required string Tag { get; init; }

    public required string Url { get; init; }

    public RuleSetFormat Format { get; init; } = RuleSetFormat.Binary;

    public RuleSetKind Kind { get; init; } = RuleSetKind.Mixed;

    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Optional pin; when set, a download whose SHA-256 differs is rejected.</summary>
    public string? ExpectedSha256 { get; init; }

    public bool Enabled { get; init; } = true;

    public string? Description { get; init; }
}

/// <summary>Metadata of a cached rule-set file.</summary>
public sealed record RuleSetCacheEntry
{
    public required string Tag { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset FetchedUtc { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public RuleSetFormat Format { get; init; }
    public int Version { get; init; }
}
