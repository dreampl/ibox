using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;

namespace JackiVpn.UnitTests.Cores;

/// <summary>Rules that the core silently never matched (checked against sing-box 1.14.2).</summary>
public class RoutingRuleFixTests
{
    private static RoutingRule R(string id, RuleAction action, params RuleCondition[] conditions) => new() { Id = id, Name = id, Action = action, Conditions = conditions };

    private static List<JsonObject> Rules(RoutingSettings routing, Func<CoreSessionPlan, CoreSessionPlan>? change = null)
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with
        {
            Routing = routing,
            RuleSetPaths = new Dictionary<string, string> { ["geoip-ir"] = "/c/geoip-ir.srs", ["geosite-category-ir"] = "/c/geosite-category-ir.srs" },
        };
        plan = change?.Invoke(plan) ?? plan;
        return SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
    }

    private static int IndexOf(List<JsonObject> rules, Func<JsonObject, bool> match) => rules.FindIndex(r => match(r));

    [Fact]
    public void Process_rules_match_regardless_of_case()
    {
        var rules = Rules(new RoutingSettings { Rules = [R("tg", RuleAction.Direct, new RuleCondition(RuleConditionType.ProcessName, ["telegram.exe"]))] });

        var rule = rules.Single(r => r["process_path_regex"] is JsonArray a && a.Any(v => v!.GetValue<string>().Contains("telegram", StringComparison.Ordinal)));
        var pattern = rule["process_path_regex"]![0]!.GetValue<string>();
        pattern.Should().StartWith("(?i)");
        System.Text.RegularExpressions.Regex.IsMatch(@"C:\Users\a\AppData\Roaming\Telegram Desktop\Telegram.exe", pattern).Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(@"C:\x\notelegram.exe", pattern).Should().BeFalse();
        rules.Should().NotContain(r => r["process_name"] != null, "process_name is case-sensitive in the core");
    }

    [Fact]
    public void Ip_rules_get_a_lookup_right_before_them_and_domain_rules_keep_their_names()
    {
        var rules = Rules(new RoutingSettings
        {
            Rules =
            [
                R("site", RuleAction.Direct, new RuleCondition(RuleConditionType.GeoSite, ["category-ir"])),
                R("ip", RuleAction.Direct, new RuleCondition(RuleConditionType.GeoIp, ["ir"])),
            ],
        });

        var site = IndexOf(rules, r => r["rule_set"] is JsonArray a && a[0]!.GetValue<string>() == "geosite-category-ir");
        var resolve = IndexOf(rules, r => r["action"]?.GetValue<string>() == "resolve");
        var ip = IndexOf(rules, r => r["rule_set"] is JsonArray a && a[0]!.GetValue<string>() == "geoip-ir");
        resolve.Should().BeGreaterThan(site).And.BeLessThan(ip);
        rules[resolve]["server"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.DnsRemote, "looked up through the tunnel, not by the local network");
        rules.Count(r => r["action"]?.GetValue<string>() == "resolve").Should().Be(1);
    }

    [Fact]
    public void Ip_cidr_and_private_rules_also_get_the_lookup_but_domain_only_rules_do_not()
    {
        Rules(new RoutingSettings { Rules = [R("c", RuleAction.Direct, new RuleCondition(RuleConditionType.IpCidr, ["5.0.0.0/8"]))] })
            .Should().Contain(r => r["action"] != null && r["action"]!.GetValue<string>() == "resolve");
        Rules(new RoutingSettings { Rules = [R("p", RuleAction.Block, new RuleCondition(RuleConditionType.PrivateIp, ["true"]))] })
            .Should().Contain(r => r["action"] != null && r["action"]!.GetValue<string>() == "resolve");
        Rules(new RoutingSettings { Rules = [R("d", RuleAction.Direct, new RuleCondition(RuleConditionType.DomainSuffix, ["ir"]))] })
            .Should().NotContain(r => r["action"] != null && r["action"]!.GetValue<string>() == "resolve");
    }

    [Fact]
    public void Lookup_uses_the_local_resolver_only_when_dns_is_local_anyway()
    {
        var rules = Rules(
            new RoutingSettings { Rules = [R("c", RuleAction.Direct, new RuleCondition(RuleConditionType.IpCidr, ["5.0.0.0/8"]))] },
            p => p with { Dns = new DnsSettings { Mode = DnsMode.System } });

        rules.Single(r => r["action"]?.GetValue<string>() == "resolve")["server"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.DnsDirect);
    }

    [Fact]
    public void Disabled_ip_rules_add_no_lookup()
    {
        Rules(new RoutingSettings { Rules = [R("c", RuleAction.Direct, new RuleCondition(RuleConditionType.IpCidr, ["5.0.0.0/8"])) with { Enabled = false }] })
            .Should().NotContain(r => r["action"] != null && r["action"]!.GetValue<string>() == "resolve");
    }
}
