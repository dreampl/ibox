using JackiVpn.Domain.Routing;

namespace JackiVpn.Routing;

public sealed record PresetResult(IReadOnlyList<RoutingRule> Rules, IReadOnlyList<RuleSetDefinition> RuleSets, RuleActionType Final);

/// <summary>
/// Presets materialize <b>editable</b> rules — nothing is hard-coded or locked. Selecting a preset copies its
/// rules into the user's rule list, where each can be edited, disabled, reordered or exported.
/// </summary>
public static class RoutingPresets
{
    public const string GeoIpPrefix = "geoip-";
    public const string GeoSitePrefix = "geosite-";

    public static string GeoIpTag(string code) => GeoIpPrefix + code.Trim().ToLowerInvariant();

    public static string GeoSiteTag(string name) => GeoSitePrefix + name.Trim().ToLowerInvariant();

    /// <summary>Official sing-box rule-set data (SagerNet sing-geoip / sing-geosite, "rule-set" branch).</summary>
    public static RuleSetDefinition GeoIp(string code) => new()
    {
        Tag = GeoIpTag(code),
        Url = $"https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-{code.ToLowerInvariant()}.srs",
        Format = RuleSetFormat.Binary,
        Kind = RuleSetKind.Ip,
        UpdateInterval = TimeSpan.FromDays(3),
        Description = $"GeoIP {code.ToUpperInvariant()} (SagerNet/sing-geoip)",
    };

    public static RuleSetDefinition GeoSite(string name) => new()
    {
        Tag = GeoSiteTag(name),
        Url = $"https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-{name.ToLowerInvariant()}.srs",
        Format = RuleSetFormat.Binary,
        Kind = RuleSetKind.Domain,
        UpdateInterval = TimeSpan.FromDays(3),
        Description = $"GeoSite {name} (SagerNet/sing-geosite)",
    };

    public static PresetResult Build(RoutingPreset preset) => preset switch
    {
        RoutingPreset.Global => new PresetResult([], [], RuleActionType.Proxy),
        RoutingPreset.Direct => new PresetResult([], [], RuleActionType.Direct),
        RoutingPreset.Rule => new PresetResult(
            [
                Rule("rule-bt-direct", "BitTorrent goes direct", RuleAction.Direct, RoutingPreset.Rule, new RuleCondition(RuleConditionType.Protocol, ["bittorrent"])),
            ],
            [],
            RuleActionType.Proxy),
        RoutingPreset.IranFriendly => new PresetResult(
            [
                Rule("ir-ads-block", "Block ads (optional)", RuleAction.Block, RoutingPreset.IranFriendly, new RuleCondition(RuleConditionType.GeoSite, ["category-ads-all"])) with { Enabled = false },
                Rule("ir-tld-direct", ".ir domains direct", RuleAction.Direct, RoutingPreset.IranFriendly, new RuleCondition(RuleConditionType.DomainSuffix, ["ir"])),
                Rule("ir-geosite-direct", "Iranian sites direct", RuleAction.Direct, RoutingPreset.IranFriendly, new RuleCondition(RuleConditionType.GeoSite, ["category-ir"])),
                Rule("ir-geoip-direct", "Iranian IPs direct", RuleAction.Direct, RoutingPreset.IranFriendly, new RuleCondition(RuleConditionType.GeoIp, ["ir"])),
                Rule("ir-bt-direct", "BitTorrent goes direct", RuleAction.Direct, RoutingPreset.IranFriendly, new RuleCondition(RuleConditionType.Protocol, ["bittorrent"])),
            ],
            [GeoSite("category-ads-all"), GeoSite("category-ir"), GeoIp("ir")],
            RuleActionType.Proxy),
        _ => new PresetResult([], [], RuleActionType.Proxy),
    };

    /// <summary>Rule-set definitions implied by GeoIp/GeoSite conditions that the user has not declared yet.</summary>
    public static IReadOnlyList<RuleSetDefinition> ImpliedRuleSets(IEnumerable<RoutingRule> rules, IReadOnlyCollection<RuleSetDefinition> declared)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(declared);
        var have = declared.Select(d => d.Tag).ToHashSet(StringComparer.Ordinal);
        var result = new List<RuleSetDefinition>();
        foreach (var c in rules.Where(r => r.Enabled).SelectMany(r => r.Conditions))
        {
            foreach (var v in c.Values)
            {
                var def = c.Type switch
                {
                    RuleConditionType.GeoIp => GeoIp(v.TrimStart('!')),
                    RuleConditionType.GeoSite => GeoSite(v.TrimStart('!')),
                    _ => null,
                };
                if (def is not null && have.Add(def.Tag))
                {
                    result.Add(def);
                }
            }
        }

        return result;
    }

    private static RoutingRule Rule(string id, string name, RuleAction action, RoutingPreset preset, params RuleCondition[] conditions) => new()
    {
        Id = id,
        Name = name,
        Action = action,
        Conditions = conditions,
        FromPreset = preset,
    };
}
