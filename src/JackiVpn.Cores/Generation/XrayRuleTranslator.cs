using System.Text.Json.Nodes;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Cores.Generation;

/// <summary>
/// Translates user rules (<see cref="RoutingRule"/>, sing-box semantics) into Xray <c>routing.rules</c>.
/// <para>
/// Xray runs behind sing-box, which already applied the rules: the backend only sees what sing-box sent to
/// the proxy. The rules are repeated there as a second line of defence, so the one thing they must never do
/// is send a connection direct that sing-box decided to tunnel. Hence:
/// </para>
/// <list type="bullet">
/// <item>a rule is translated whole or not at all (a dropped condition would widen it);</item>
/// <item>once a rule could not be translated exactly (geo/rule-set, private IP, process, invert, IP rules that
/// Xray cannot match against names without its own DNS lookup), every later Direct rule is left out, because
/// in sing-box that earlier rule might have tunnelled the connection first;</item>
/// <item>the always-tunnelled domains come first, as in sing-box.</item>
/// </list>
/// </summary>
public static class XrayRuleTranslator
{
    public const string BlockTag = "block";

    private static readonly HashSet<string> XrayProtocols = new(StringComparer.OrdinalIgnoreCase) { "http", "tls", "quic", "bittorrent" };

    public static List<JsonObject> Translate(RoutingSettings routing, string inboundTag, string proxyTag, string directTag, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(notes);
        var result = new List<JsonObject>();
        if (routing.Final != RuleActionType.Direct)
        {
            result.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(inboundTag),
                ["domain"] = new JsonArray(SingBoxConfigBuilder.AlwaysTunneledDomains.Select(d => (JsonNode?)("domain:" + d)).ToArray()),
                ["outboundTag"] = proxyTag,
            });
        }

        var uncertain = false;
        foreach (var rule in routing.Rules.Where(r => r.Enabled))
        {
            if (rule.Conditions.Count > 0 && rule.Conditions.All(c => c.Type == RuleConditionType.AppPackage))
            {
                // Applied by Android's per-app lists, skipped by sing-box too.
                continue;
            }

            if (rule.Action.Type == RuleActionType.HijackDns)
            {
                continue;
            }

            var outbound = rule.Action.Type switch
            {
                RuleActionType.Direct => directTag,
                RuleActionType.Block => BlockTag,
                RuleActionType.Outbound when rule.Action.OutboundTag == SingBoxConfigBuilder.DirectTag => directTag,
                _ => proxyTag,
            };

            var translated = TranslateOne(rule, inboundTag, outbound, out var exact);
            if (translated is null)
            {
                notes.Add($"Xray: rule '{rule.Name}' has conditions Xray cannot match; sing-box applies it.");
                uncertain = true;
                continue;
            }

            if (outbound == directTag && uncertain)
            {
                notes.Add($"Xray: Direct rule '{rule.Name}' left to sing-box (an earlier rule could not be repeated exactly).");
                continue;
            }

            result.AddRange(translated);
            uncertain |= !exact;
        }

        return result;
    }

    /// <summary>
    /// One user rule as one or two Xray rules (Xray AND-s every field of a rule, while the user's domain and IP
    /// conditions are OR-ed, so they become separate rules with the same port/network/protocol fields).
    /// Null when a condition has no exact Xray equivalent. <paramref name="exact"/> is false when Xray will match
    /// fewer connections than sing-box (IP conditions against connections that arrive as names).
    /// </summary>
    private static List<JsonObject>? TranslateOne(RoutingRule rule, string inboundTag, string outbound, out bool exact)
    {
        exact = true;
        if (rule.Invert || rule.Conditions.Count == 0)
        {
            return null;
        }

        var domains = new List<string>();
        var ips = new List<string>();
        var common = new JsonObject();
        var ports = new List<string>();
        foreach (var c in rule.Conditions)
        {
            var values = c.Values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            switch (c.Type)
            {
                case RuleConditionType.Domain:
                    domains.AddRange(values.Select(v => "full:" + v.TrimEnd('.').ToLowerInvariant()));
                    break;
                case RuleConditionType.DomainSuffix:
                    domains.AddRange(values.Select(v => "domain:" + v.Trim('.').ToLowerInvariant()));
                    break;
                case RuleConditionType.DomainKeyword:
                    domains.AddRange(values.Select(v => "keyword:" + v.ToLowerInvariant()));
                    break;
                case RuleConditionType.DomainRegex:
                    domains.AddRange(values.Select(v => "regexp:" + v));
                    break;
                case RuleConditionType.IpCidr:
                    ips.AddRange(values);
                    exact = false;
                    break;
                case RuleConditionType.Port:
                    ports.AddRange(values);
                    break;
                case RuleConditionType.PortRange:
                    foreach (var v in values)
                    {
                        var parts = v.Split(':');
                        ports.Add($"{(parts[0].Length == 0 ? "1" : parts[0])}-{(parts[1].Length == 0 ? "65535" : parts[1])}");
                    }

                    break;
                case RuleConditionType.Network:
                    common["network"] = string.Join(',', values.Select(v => v.ToLowerInvariant()).Distinct(StringComparer.Ordinal));
                    break;
                case RuleConditionType.Protocol:
                    if (!values.All(XrayProtocols.Contains))
                    {
                        return null;
                    }

                    common["protocol"] = new JsonArray(values.Select(v => (JsonNode?)v.ToLowerInvariant()).Distinct().ToArray());
                    break;
                default:
                    // GeoIp/GeoSite/RuleSet (sing-box rule-sets, not Xray .dat files), PrivateIp, process, network type.
                    return null;
            }
        }

        if (ports.Count > 0)
        {
            common["port"] = string.Join(',', ports.Distinct(StringComparer.Ordinal));
        }

        JsonObject Make()
        {
            var o = new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(inboundTag) };
            foreach (var (k, v) in common)
            {
                o[k] = v?.DeepClone();
            }

            o["outboundTag"] = outbound;
            return o;
        }

        var list = new List<JsonObject>();
        if (domains.Count > 0)
        {
            var o = Make();
            o["domain"] = new JsonArray(domains.Distinct(StringComparer.Ordinal).Select(d => (JsonNode?)d).ToArray());
            list.Add(o);
        }

        if (ips.Count > 0)
        {
            var o = Make();
            o["ip"] = new JsonArray(ips.Distinct(StringComparer.Ordinal).Select(d => (JsonNode?)d).ToArray());
            list.Add(o);
        }

        if (list.Count == 0)
        {
            // Only port/network/protocol conditions.
            list.Add(Make());
        }

        return list;
    }
}
