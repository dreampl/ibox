using System.Globalization;
using System.Text.RegularExpressions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Routing;

namespace JackiVpn.Routing;

/// <summary>Syntax validation for user rules. Nothing is saved (or sent to a core) until this passes.</summary>
public static partial class RuleValidator
{
    public const int MaxValuesPerCondition = 5_000;
    public const int MaxRules = 2_000;

    public static readonly IReadOnlySet<string> KnownProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http", "tls", "quic", "stun", "dns", "bittorrent", "dtls", "ssh", "rdp", "ntp",
    };

    public static ValidationResult ValidateAll(IReadOnlyList<RoutingRule> rules, IReadOnlyCollection<string> knownRuleSetTags, IReadOnlyCollection<string>? knownOutboundTags = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = new ValidationResult();
        if (rules.Count > MaxRules)
        {
            result.Error("Rules", $"Too many rules ({rules.Count} > {MaxRules}).");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rules.Count; i++)
        {
            if (!ids.Add(rules[i].Id))
            {
                result.Error($"Rules[{i}].Id", "Duplicate rule id.");
            }

            result.Merge(Validate(rules[i], knownRuleSetTags, knownOutboundTags, $"Rules[{i}]"));
        }

        return result;
    }

    public static ValidationResult Validate(RoutingRule rule, IReadOnlyCollection<string> knownRuleSetTags, IReadOnlyCollection<string>? knownOutboundTags = null, string prefix = "Rule")
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(knownRuleSetTags);
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || NetText.HasControlChars(rule.Id))
        {
            r.Error($"{prefix}.Id", "Rule id is missing or invalid.");
        }

        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 128 || NetText.HasControlChars(rule.Name))
        {
            r.Error($"{prefix}.Name", "Rule name is missing or invalid.");
        }

        if (rule.Conditions.Count == 0)
        {
            r.Error($"{prefix}.Conditions", "A rule needs at least one condition.");
        }

        if (rule.Action.Type == RuleActionType.Outbound
            && (string.IsNullOrWhiteSpace(rule.Action.OutboundTag) || (knownOutboundTags is not null && !knownOutboundTags.Contains(rule.Action.OutboundTag))))
        {
            r.Error($"{prefix}.Action", $"Unknown outbound '{rule.Action.OutboundTag}'.");
        }

        foreach (var c in rule.Conditions)
        {
            var field = $"{prefix}.{c.Type}";
            if (c.Values.Count == 0 && c.Type != RuleConditionType.PrivateIp)
            {
                r.Error(field, "Condition has no values.");
                continue;
            }

            if (c.Values.Count > MaxValuesPerCondition)
            {
                r.Error(field, $"Too many values (> {MaxValuesPerCondition}).");
                continue;
            }

            foreach (var raw in c.Values)
            {
                var v = raw?.Trim() ?? string.Empty;
                var error = ValidateValue(c.Type, v, knownRuleSetTags);
                if (error is not null)
                {
                    r.Error(field, $"'{Truncate(v)}': {error}");
                }
            }
        }

        if (rule.Conditions.Any(c => c.Type == RuleConditionType.NetworkType))
        {
            r.Warn($"{prefix}.NetworkType", "Network type matching is not supported by the Windows cores; the rule will never match.");
        }

        return r;
    }

    private static string? ValidateValue(RuleConditionType type, string v, IReadOnlyCollection<string> ruleSets)
    {
        if (v.Length == 0 || v.Length > 1024 || NetText.HasControlChars(v))
        {
            return "empty, too long or contains control characters";
        }

        switch (type)
        {
            case RuleConditionType.Domain:
            case RuleConditionType.DomainSuffix:
                return NetText.IsValidHostName(v.TrimStart('.')) ? null : "not a valid domain";
            case RuleConditionType.DomainKeyword:
                return v.Any(char.IsWhiteSpace) ? "keyword must not contain spaces" : null;
            case RuleConditionType.DomainRegex:
                try
                {
                    _ = new Regex(v, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                    return null;
                }
                catch (ArgumentException ex)
                {
                    return "invalid regex: " + ex.Message;
                }

            case RuleConditionType.IpCidr:
                return NetText.TryParseCidr(v, out _, out _) ? null : "not a valid IP or CIDR";
            case RuleConditionType.GeoIp:
            case RuleConditionType.GeoSite:
                return GeoCodeRegex().IsMatch(v) ? null : "invalid code (letters, digits, '-', '_', '!' and '@' only)";
            case RuleConditionType.RuleSet:
                return ruleSets.Contains(v, StringComparer.Ordinal) ? null : "unknown rule-set tag";
            case RuleConditionType.Protocol:
                return KnownProtocols.Contains(v) ? null : "unknown protocol";
            case RuleConditionType.Port:
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && NetText.IsValidPort(port) ? null : "port must be 1-65535";
            case RuleConditionType.PortRange:
                return IsPortRange(v) ? null : "range must look like 1000:2000 (either side may be empty)";
            case RuleConditionType.ProcessName:
                return v.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ? "process name must not contain path characters" : null;
            case RuleConditionType.ProcessPath:
                return Path.IsPathFullyQualified(v) && v.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0 ? null : "process path must be an absolute path";
            case RuleConditionType.Network:
                return v is "tcp" or "udp" ? null : "network must be tcp or udp";
            case RuleConditionType.PrivateIp:
                return v is "true" or "false" ? null : "use true/false";
            case RuleConditionType.NetworkType:
                return v is "wifi" or "ethernet" or "cellular" or "other" ? null : "unknown network type";
            default:
                return "unknown condition type";
        }
    }

    public static bool IsPortRange(string v)
    {
        var parts = v.Split(':');
        if (parts.Length != 2 || (parts[0].Length == 0 && parts[1].Length == 0))
        {
            return false;
        }

        int? from = parts[0].Length == 0 ? null : int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : -1;
        int? to = parts[1].Length == 0 ? null : int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : -1;
        return (from is null || NetText.IsValidPort(from.Value)) && (to is null || NetText.IsValidPort(to.Value)) && (from is null || to is null || from <= to);
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    [GeneratedRegex("^!?[A-Za-z0-9_\\-@!]{1,64}$")]
    private static partial Regex GeoCodeRegex();
}
