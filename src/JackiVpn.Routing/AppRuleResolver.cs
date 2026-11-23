using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Routing;

/// <summary>Per-app lists Android applies to the VPN interface, after app rules are merged in.</summary>
public sealed record AppRouting(SplitTunnelMode Mode, IReadOnlyList<string> Packages, IReadOnlyList<string> Notes);

/// <summary>
/// Android cannot tell the core which app opened a tunnelled connection, so app rules (<see cref="RuleConditionType.AppPackage"/>)
/// are applied by the VPN itself: a Direct app is left outside the VPN, a Proxy app is put inside it (the core's
/// other rules then apply to it). Rules are read top to bottom and the first enabled rule naming an app wins,
/// like every other rule; they override the manual per-app list for the apps they name.
/// </summary>
public static class AppRuleResolver
{
    public static AppRouting Resolve(SplitTunnelSettings split, IReadOnlyList<RoutingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(split);
        ArgumentNullException.ThrowIfNull(rules);
        var decided = new Dictionary<string, RuleActionType>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.Enabled && !r.Invert && r.Action.Type is RuleActionType.Direct or RuleActionType.Proxy))
        {
            if (rule.Conditions.Count == 0 || rule.Conditions.Any(c => c.Type != RuleConditionType.AppPackage))
            {
                continue;
            }

            foreach (var p in rule.Conditions.SelectMany(c => c.Values).Select(v => v.Trim()).Where(v => RuleValidator.PackageRegex().IsMatch(v)))
            {
                decided.TryAdd(p, rule.Action.Type);
            }
        }

        var notes = new List<string>();
        var manual = split.Packages.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (decided.Count == 0)
        {
            return new AppRouting(split.Mode, manual.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), notes);
        }

        var direct = decided.Where(kv => kv.Value == RuleActionType.Direct).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var proxy = decided.Where(kv => kv.Value == RuleActionType.Proxy).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        switch (split.Mode)
        {
            case SplitTunnelMode.OnlySelected:
            {
                var allowed = manual.Where(p => !direct.Contains(p)).Concat(proxy).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (allowed.Count == 0)
                {
                    // An empty allow-list would put every app into the VPN: keep the rules' Direct apps out instead.
                    notes.Add("App rules removed every selected app; all apps except the Direct ones use the VPN.");
                    return new AppRouting(SplitTunnelMode.ExcludeSelected, direct.Order(StringComparer.Ordinal).ToList(), notes);
                }

                return new AppRouting(SplitTunnelMode.OnlySelected, allowed, notes);
            }

            default:
            {
                // AllApps behaves like "exclude none": Direct apps join the exclusions, Proxy apps leave them.
                var excluded = (split.Mode == SplitTunnelMode.ExcludeSelected ? manual : [])
                    .Where(p => !proxy.Contains(p)).Concat(direct).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                return new AppRouting(excluded.Count == 0 ? SplitTunnelMode.AllApps : SplitTunnelMode.ExcludeSelected, excluded, notes);
            }
        }
    }
}
