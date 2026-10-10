using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Routing;

namespace JackiVpn.Routing;

/// <summary>A connection as seen by the router.</summary>
public sealed record RouteRequest
{
    public string? Domain { get; init; }
    public IPAddress? DestinationIp { get; init; }
    public int? Port { get; init; }
    public string Network { get; init; } = "tcp";
    public string? Protocol { get; init; }
    public string? ProcessName { get; init; }
    public string? ProcessPath { get; init; }
}

public sealed record RouteDecision(RoutingRule? Rule, RuleAction Action, bool Indeterminate, string Explanation);

/// <summary>Optional evaluator for rule-set / geo conditions (data lives in the core's rule-set files).</summary>
public interface IRuleSetEvaluator
{
    /// <returns>true/false when known, null when the data is unavailable.</returns>
    bool? Matches(string tag, string? domain, IPAddress? ip);
}

/// <summary>
/// In-process evaluation of routing rules with exactly the sing-box semantics used by config generation
/// (see <see cref="RoutingRule"/>). Used for the "test a rule" feature and for unit tests; the core does
/// the real routing.
/// </summary>
public sealed class RuleMatcher
{
    private static readonly HashSet<RuleConditionType> DestinationTypes =
    [
        RuleConditionType.Domain, RuleConditionType.DomainSuffix, RuleConditionType.DomainKeyword, RuleConditionType.DomainRegex,
        RuleConditionType.GeoSite, RuleConditionType.GeoIp, RuleConditionType.IpCidr, RuleConditionType.PrivateIp, RuleConditionType.RuleSet,
    ];

    private readonly IReadOnlyList<RoutingRule> _rules;
    private readonly RuleAction _final;
    private readonly IRuleSetEvaluator? _ruleSets;
    private readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);

    public RuleMatcher(IReadOnlyList<RoutingRule> rules, RuleAction final, IRuleSetEvaluator? ruleSets = null)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _final = final ?? throw new ArgumentNullException(nameof(final));
        _ruleSets = ruleSets;
    }

    public RouteDecision Match(RouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var indeterminate = false;
        foreach (var rule in _rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var (matched, unknown) = Evaluate(rule, request);
            indeterminate |= unknown;
            if (rule.Invert)
            {
                matched = !matched;
            }

            if (matched)
            {
                return new RouteDecision(rule, rule.Action, indeterminate, $"Matched rule '{rule.Name}'.");
            }
        }

        return new RouteDecision(null, _final, indeterminate, "No rule matched; final action applies.");
    }

    private (bool Matched, bool Unknown) Evaluate(RoutingRule rule, RouteRequest req)
    {
        var unknown = false;
        var destination = rule.Conditions.Where(c => DestinationTypes.Contains(c.Type)).ToList();
        if (destination.Count > 0)
        {
            var any = false;
            foreach (var c in destination)
            {
                var r = MatchDestination(c, req);
                unknown |= r is null;
                if (r == true)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return (false, unknown);
            }
        }

        var ports = rule.Conditions.Where(c => c.Type is RuleConditionType.Port or RuleConditionType.PortRange).ToList();
        if (ports.Count > 0 && !ports.Any(c => MatchPort(c, req.Port)))
        {
            return (false, unknown);
        }

        foreach (var c in rule.Conditions)
        {
            var ok = c.Type switch
            {
                RuleConditionType.Protocol => req.Protocol is not null && c.Values.Contains(req.Protocol, StringComparer.OrdinalIgnoreCase),
                RuleConditionType.Network => c.Values.Contains(req.Network, StringComparer.OrdinalIgnoreCase),
                RuleConditionType.ProcessName => req.ProcessName is not null && c.Values.Contains(req.ProcessName, StringComparer.OrdinalIgnoreCase),
                RuleConditionType.ProcessPath => req.ProcessPath is not null && c.Values.Any(v => string.Equals(Path.GetFullPath(v), Path.GetFullPath(req.ProcessPath), StringComparison.OrdinalIgnoreCase)),
                RuleConditionType.NetworkType => false,
                _ => true,
            };
            if (!ok)
            {
                return (false, unknown);
            }
        }

        return (true, unknown);
    }

    private bool? MatchDestination(RuleCondition c, RouteRequest req)
    {
        var domain = req.Domain?.Trim().TrimEnd('.').ToLowerInvariant();
        switch (c.Type)
        {
            case RuleConditionType.Domain:
                return domain is not null && c.Values.Any(v => string.Equals(v.Trim().TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase));
            case RuleConditionType.DomainSuffix:
                return domain is not null && c.Values.Any(v => SuffixMatch(domain, v));
            case RuleConditionType.DomainKeyword:
                return domain is not null && c.Values.Any(v => domain.Contains(v.Trim().ToLowerInvariant(), StringComparison.Ordinal));
            case RuleConditionType.DomainRegex:
                return domain is not null && c.Values.Any(v => GetRegex(v).IsMatch(domain));
            case RuleConditionType.IpCidr:
                return req.DestinationIp is not null && c.Values.Any(v => NetText.TryParseCidr(v, out var net, out var len) && NetText.CidrContains(net, len, req.DestinationIp));
            case RuleConditionType.PrivateIp:
                return req.DestinationIp is not null && NetText.IsPrivateOrLocal(req.DestinationIp);
            case RuleConditionType.GeoIp:
            case RuleConditionType.GeoSite:
            case RuleConditionType.RuleSet:
            {
                if (_ruleSets is null)
                {
                    return null;
                }

                bool? any = false;
                foreach (var v in c.Values)
                {
                    var tag = c.Type switch
                    {
                        RuleConditionType.GeoIp => RoutingPresets.GeoIpTag(v),
                        RuleConditionType.GeoSite => RoutingPresets.GeoSiteTag(v),
                        _ => v,
                    };
                    var m = _ruleSets.Matches(tag, domain, req.DestinationIp);
                    if (m == true)
                    {
                        return true;
                    }

                    if (m is null)
                    {
                        any = null;
                    }
                }

                return any;
            }

            default:
                return false;
        }
    }

    /// <summary>"example.com" matches itself and sub-domains; ".example.com" matches only sub-domains.</summary>
    public static bool SuffixMatch(string domain, string suffix)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(suffix);
        var s = suffix.Trim().ToLowerInvariant();
        if (s.StartsWith('.'))
        {
            return domain.EndsWith(s, StringComparison.Ordinal);
        }

        return domain == s || domain.EndsWith("." + s, StringComparison.Ordinal);
    }

    private static bool MatchPort(RuleCondition c, int? port)
    {
        if (port is null)
        {
            return false;
        }

        if (c.Type == RuleConditionType.Port)
        {
            return c.Values.Any(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p == port);
        }

        foreach (var v in c.Values)
        {
            var parts = v.Split(':');
            if (parts.Length != 2)
            {
                continue;
            }

            var from = parts[0].Length == 0 ? 1 : int.Parse(parts[0], CultureInfo.InvariantCulture);
            var to = parts[1].Length == 0 ? 65535 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (port >= from && port <= to)
            {
                return true;
            }
        }

        return false;
    }

    private Regex GetRegex(string pattern)
    {
        lock (_regexCache)
        {
            if (!_regexCache.TryGetValue(pattern, out var re))
            {
                re = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
                _regexCache[pattern] = re;
            }

            return re;
        }
    }
}
