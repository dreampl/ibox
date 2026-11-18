using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Routing;
using JackiVpn.UnitTests.Cores;

namespace JackiVpn.UnitTests.Routing;

/// <summary>User rules must reach the generated sing-box and Xray configs exactly, in order, with the right action.</summary>
public class RulesInBothCoresTests
{
    private static RoutingRule R(string id, RuleAction action, params RuleCondition[] conditions) => new() { Id = id, Name = id, Action = action, Conditions = conditions };

    private static RuleCondition C(RuleConditionType t, params string[] v) => new(t, v);

    private static readonly RoutingSettings Sample = new()
    {
        BypassLan = false,
        Rules =
        [
            R("ads", RuleAction.Block, C(RuleConditionType.DomainKeyword, "doubleclick")),
            R("bank", RuleAction.Direct, C(RuleConditionType.Domain, "bank.example.ir"), C(RuleConditionType.IpCidr, "185.1.2.0/24")),
            R("ir", RuleAction.Direct, C(RuleConditionType.DomainSuffix, ".ir")),
            R("tg", RuleAction.Proxy, C(RuleConditionType.DomainSuffix, "telegram.org"), C(RuleConditionType.Port, "443")),
            R("off", RuleAction.Direct, C(RuleConditionType.Domain, "disabled.example.com")) with { Enabled = false },
        ],
    };

    private static List<JsonObject> SingBoxRules(RoutingSettings routing)
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with
        {
            Routing = routing,
            RuleSetPaths = new Dictionary<string, string> { ["geoip-ir"] = "/c/geoip-ir.srs", ["geosite-category-ir"] = "/c/geosite-category-ir.srs" },
        };
        return SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
    }

    private static (List<JsonObject> Rules, JsonObject Config) XrayRules(RoutingSettings routing)
    {
        var profile = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan.Profiles[0];
        var (config, _, _, _) = XrayConfigBuilder.Build(new CoreSessionPlan { Kind = SessionKind.ChainBackend, Profiles = [profile], ListenPort = 20901, Routing = routing }, GoldenConfigTests.Xray26);
        return (config["routing"]!["rules"]!.AsArray().OfType<JsonObject>().ToList(), config);
    }

    private static string[] Strings(JsonNode? n) => n!.AsArray().Select(x => x!.ToString()).ToArray();

    [Fact]
    public void SingBox_gets_every_enabled_rule_in_order_with_its_action()
    {
        var rules = SingBoxRules(Sample);

        var ads = rules.FindIndex(r => r["domain_keyword"] is not null);
        var bank = rules.FindIndex(r => r["domain"] is JsonArray a && Strings(a).Contains("bank.example.ir"));
        var ir = rules.FindIndex(r => r["domain_suffix"] is JsonArray a && Strings(a).SequenceEqual([".ir"]));
        var tg = rules.FindIndex(r => r["domain_suffix"] is JsonArray a && Strings(a).Contains("telegram.org"));
        new[] { ads, bank, ir, tg }.Should().BeInAscendingOrder().And.NotContain(-1);

        rules[ads]["action"]!.ToString().Should().Be("reject");
        rules[bank]["outbound"]!.ToString().Should().Be(SingBoxConfigBuilder.DirectTag);
        Strings(rules[bank]["ip_cidr"]).Should().Equal("185.1.2.0/24");
        rules[tg]["outbound"]!.ToString().Should().Be(SingBoxConfigBuilder.ProxyTag);
        rules[tg]["port"]!.AsArray().Select(p => p!.GetValue<int>()).Should().Equal(443);
        rules.Should().NotContain(r => r.ToJsonString().Contains("disabled.example.com"), "disabled rules never reach the core");
    }

    [Fact]
    public void SingBox_geo_rules_use_the_cached_rule_sets()
    {
        var rules = SingBoxRules(new RoutingSettings
        {
            Rules = [R("geo", RuleAction.Direct, C(RuleConditionType.GeoSite, "category-ir"), C(RuleConditionType.GeoIp, "ir"))],
        });

        var geo = rules.Single(r => r["rule_set"] is not null);
        Strings(geo["rule_set"]).Should().BeEquivalentTo("geosite-category-ir", "geoip-ir");
        geo["outbound"]!.ToString().Should().Be("direct");
    }

    [Fact]
    public void App_rules_never_reach_the_core()
    {
        var rules = SingBoxRules(new RoutingSettings { Rules = [R("tg-app", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.telegram.messenger"))] });

        rules.Should().NotContain(r => r.ToJsonString().Contains("telegram", StringComparison.Ordinal));
        XrayRules(new RoutingSettings { Rules = [R("tg-app", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.telegram.messenger"))] })
            .Rules.Should().NotContain(r => r.ToJsonString().Contains("telegram", StringComparison.Ordinal));
    }

    [Fact]
    public void Xray_gets_the_same_rules_in_order_before_its_catch_all()
    {
        var (rules, config) = XrayRules(Sample);

        rules[0]["domain"]!.AsArray().Select(d => d!.ToString()).Should().Contain("domain:claude.ai", "always-tunnelled names come first, as in sing-box");
        var ads = rules.FindIndex(r => r["domain"] is JsonArray a && Strings(a).SequenceEqual(["keyword:doubleclick"]));
        var bankDomain = rules.FindIndex(r => r["domain"] is JsonArray a && Strings(a).SequenceEqual(["full:bank.example.ir"]));
        var bankIp = rules.FindIndex(r => r["ip"] is JsonArray a && Strings(a).SequenceEqual(["185.1.2.0/24"]));
        var ir = rules.FindIndex(r => r["domain"] is JsonArray a && Strings(a).SequenceEqual(["domain:ir"]));
        var tg = rules.FindIndex(r => r["domain"] is JsonArray a && Strings(a).SequenceEqual(["domain:telegram.org"]));
        var catchAll = rules.Count - 1;
        new[] { ads, bankDomain, bankIp, tg, catchAll }.Should().BeInAscendingOrder().And.NotContain(-1);
        ir.Should().Be(-1, "a Direct rule after an IP rule is left to sing-box: Xray cannot match that IP rule against names");

        rules[ads]["outboundTag"]!.ToString().Should().Be(XrayRuleTranslator.BlockTag);
        rules[bankDomain]["outboundTag"]!.ToString().Should().Be("direct");
        rules[bankIp]["outboundTag"]!.ToString().Should().Be("direct", "domain OR ip becomes two Xray rules (Xray AND-s fields)");
        rules[tg]["outboundTag"]!.ToString().Should().Be("proxy");
        rules[tg]["port"]!.ToString().Should().Be("443");
        rules[catchAll]["outboundTag"]!.ToString().Should().Be("proxy");
        rules[catchAll]["domain"].Should().BeNull();
        rules.Should().NotContain(r => r.ToJsonString().Contains("disabled.example.com"));

        config["outbounds"]!.AsArray().Select(o => o!["tag"]!.ToString()).Should().Contain(XrayRuleTranslator.BlockTag);
        config["inbounds"]![0]!["sniffing"]!["routeOnly"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Xray_never_sends_direct_what_an_untranslatable_earlier_rule_might_have_tunnelled()
    {
        var notes = new List<string>();
        var rules = XrayRuleTranslator.Translate(new RoutingSettings
        {
            Rules =
            [
                R("geo-proxy", RuleAction.Proxy, C(RuleConditionType.GeoSite, "category-ir")),
                R("after-direct", RuleAction.Direct, C(RuleConditionType.DomainSuffix, "example.ir")),
                R("after-block", RuleAction.Block, C(RuleConditionType.Domain, "ads.example.com")),
            ],
        }, "socks-in", "proxy", "direct", notes);

        rules.Should().NotContain(r => r["outboundTag"]!.ToString() == "direct");
        rules.Should().Contain(r => r["outboundTag"]!.ToString() == XrayRuleTranslator.BlockTag);
        notes.Should().Contain(n => n.Contains("after-direct", StringComparison.Ordinal));
    }

    [Fact]
    public void Xray_drops_direct_rules_after_an_ip_rule_but_keeps_ones_before()
    {
        var rules = XrayRuleTranslator.Translate(new RoutingSettings
        {
            Final = RuleActionType.Direct,
            Rules =
            [
                R("first", RuleAction.Direct, C(RuleConditionType.Domain, "a.example.com")),
                R("ip-proxy", RuleAction.Proxy, C(RuleConditionType.IpCidr, "10.9.0.0/16")),
                R("later", RuleAction.Direct, C(RuleConditionType.Domain, "b.example.com")),
            ],
        }, "socks-in", "proxy", "direct", []);

        rules.Select(r => r.ToJsonString()).Should().Contain(j => j.Contains("full:a.example.com"))
            .And.NotContain(j => j.Contains("b.example.com"));
        rules.Should().NotContain(r => r.ToJsonString().Contains("claude.ai"), "with Final=Direct sing-box adds no forced-tunnel rule either");
    }

    [Fact]
    public void Xray_port_ranges_and_network_are_translated()
    {
        var rules = XrayRuleTranslator.Translate(new RoutingSettings
        {
            Rules = [R("games", RuleAction.Direct, C(RuleConditionType.PortRange, "27000:27100", ":20"), C(RuleConditionType.Network, "udp"))],
        }, "socks-in", "proxy", "direct", []);

        var games = rules.Last();
        games["port"]!.ToString().Should().Be("27000-27100,1-20");
        games["network"]!.ToString().Should().Be("udp");
        games["outboundTag"]!.ToString().Should().Be("direct");
    }

    [Fact]
    public void Validator_explains_bad_app_rules()
    {
        var mixed = R("x", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.telegram.messenger"), C(RuleConditionType.Domain, "a.com"));
        var block = R("y", RuleAction.Block, C(RuleConditionType.AppPackage, "org.telegram.messenger"));
        var badName = R("z", RuleAction.Direct, C(RuleConditionType.AppPackage, "telegram"));
        var good = R("ok", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.telegram.messenger", "com.android.chrome"));

        RuleValidator.Validate(mixed, []).Errors.Should().Contain(e => e.Message.Contains("separate rule", StringComparison.Ordinal));
        RuleValidator.Validate(block, []).Errors.Should().Contain(e => e.Message.Contains("cannot block", StringComparison.Ordinal));
        RuleValidator.Validate(badName, []).Errors.Should().Contain(e => e.Message.Contains("package name", StringComparison.Ordinal));
        RuleValidator.Validate(good, []).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_rejects_bad_values_with_the_value_in_the_message()
    {
        var bad = R("b", RuleAction.Direct, C(RuleConditionType.IpCidr, "300.1.1.1/8"), C(RuleConditionType.Domain, "exa mple.com"), C(RuleConditionType.Port, "70000"));
        var errors = RuleValidator.Validate(bad, []).Errors.Select(e => e.Message).ToList();

        errors.Should().Contain(m => m.Contains("300.1.1.1/8", StringComparison.Ordinal))
            .And.Contain(m => m.Contains("exa mple.com", StringComparison.Ordinal))
            .And.Contain(m => m.Contains("70000", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_then_import_keeps_order_enabled_state_and_app_rules()
    {
        var rules = Sample.Rules.Append(R("app", RuleAction.Direct, C(RuleConditionType.AppPackage, "com.android.chrome"))).ToList();
        var json = RulesPortability.Export(new RulesDocument { Rules = rules, Final = RuleActionType.Direct });

        var (doc, validation) = RulesPortability.Import(json);

        validation.IsValid.Should().BeTrue(string.Join("; ", validation.Issues));
        doc!.Final.Should().Be(RuleActionType.Direct);
        doc.Rules.Select(r => (r.Id, r.Enabled, r.Action.Type)).Should().Equal(rules.Select(r => (r.Id, r.Enabled, r.Action.Type)));
        doc.Rules[^1].Conditions[0].Type.Should().Be(RuleConditionType.AppPackage);
    }

    [Theory]
    [InlineData(SplitTunnelMode.AllApps, new string[0], SplitTunnelMode.ExcludeSelected, new[] { "org.direct" })]
    [InlineData(SplitTunnelMode.ExcludeSelected, new[] { "org.proxy", "org.other" }, SplitTunnelMode.ExcludeSelected, new[] { "org.direct", "org.other" })]
    [InlineData(SplitTunnelMode.OnlySelected, new[] { "org.direct", "org.other" }, SplitTunnelMode.OnlySelected, new[] { "org.other", "org.proxy" })]
    [InlineData(SplitTunnelMode.OnlySelected, new[] { "org.direct" }, SplitTunnelMode.OnlySelected, new[] { "org.proxy" })]
    public void App_rules_merge_into_the_android_per_app_lists(SplitTunnelMode mode, string[] manual, SplitTunnelMode expectedMode, string[] expected)
    {
        var rules = new List<RoutingRule>
        {
            R("d", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.direct")),
            R("p", RuleAction.Proxy, C(RuleConditionType.AppPackage, "org.proxy")),
            R("later", RuleAction.Proxy, C(RuleConditionType.AppPackage, "org.direct")),
            R("off", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.off")) with { Enabled = false },
        };

        var result = AppRuleResolver.Resolve(new SplitTunnelSettings { Mode = mode, Packages = manual }, rules);

        result.Mode.Should().Be(expectedMode);
        result.Packages.Should().Equal(expected);
    }

    [Fact]
    public void Only_selected_apps_all_made_direct_keeps_them_out_instead_of_routing_everything()
    {
        var result = AppRuleResolver.Resolve(
            new SplitTunnelSettings { Mode = SplitTunnelMode.OnlySelected, Packages = ["org.a"] },
            [R("d", RuleAction.Direct, C(RuleConditionType.AppPackage, "org.a"))]);

        result.Mode.Should().Be(SplitTunnelMode.ExcludeSelected);
        result.Packages.Should().Equal("org.a");
        result.Notes.Should().NotBeEmpty();
    }

    [Fact]
    public void No_app_rules_leaves_the_manual_lists_alone()
    {
        var split = new SplitTunnelSettings { Mode = SplitTunnelMode.ExcludeSelected, Packages = ["org.b", "org.a"] };

        var result = AppRuleResolver.Resolve(split, Sample.Rules);

        result.Mode.Should().Be(SplitTunnelMode.ExcludeSelected);
        result.Packages.Should().Equal("org.a", "org.b");
    }
}
