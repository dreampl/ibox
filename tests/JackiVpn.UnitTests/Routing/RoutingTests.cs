using System.Net;
using FluentAssertions;
using JackiVpn.Domain.Routing;
using JackiVpn.Routing;

namespace JackiVpn.UnitTests.Routing;

public class RoutingTests
{
    private static RoutingRule R(string id, RuleAction action, params RuleCondition[] c) => new() { Id = id, Name = id, Action = action, Conditions = c };

    private static RuleCondition C(RuleConditionType t, params string[] v) => new(t, v);

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("www.example.com", "example.com", true)]
    [InlineData("badexample.com", "example.com", false)]
    [InlineData("example.com", ".example.com", false)]
    [InlineData("a.example.com", ".example.com", true)]
    public void Domain_suffix_semantics(string domain, string suffix, bool expected)
    {
        RuleMatcher.SuffixMatch(domain, suffix).Should().Be(expected);
    }

    [Fact]
    public void First_matching_rule_wins_and_final_applies_otherwise()
    {
        var m = new RuleMatcher(
        [
            R("block-ads", RuleAction.Block, C(RuleConditionType.DomainKeyword, "doubleclick")),
            R("ir-direct", RuleAction.Direct, C(RuleConditionType.DomainSuffix, "ir")),
            R("lan", RuleAction.Direct, new RuleCondition(RuleConditionType.PrivateIp, [])),
            R("dns", RuleAction.HijackDns, C(RuleConditionType.Port, "53")),
        ], RuleAction.Proxy);

        m.Match(new RouteRequest { Domain = "ad.doubleclick.net" }).Action.Type.Should().Be(RuleActionType.Block);
        m.Match(new RouteRequest { Domain = "www.digikala.ir" }).Rule!.Id.Should().Be("ir-direct");
        m.Match(new RouteRequest { DestinationIp = IPAddress.Parse("192.168.1.10") }).Rule!.Id.Should().Be("lan");
        m.Match(new RouteRequest { DestinationIp = IPAddress.Parse("8.8.8.8"), Port = 53, Network = "udp" }).Rule!.Id.Should().Be("dns");
        var d = m.Match(new RouteRequest { Domain = "github.com", Port = 443 });
        d.Rule.Should().BeNull();
        d.Action.Should().Be(RuleAction.Proxy);
    }

    [Fact]
    public void Destination_conditions_are_ored_other_groups_anded()
    {
        var m = new RuleMatcher(
        [
            R("r", RuleAction.Direct,
                C(RuleConditionType.DomainSuffix, "corp.example"),
                C(RuleConditionType.IpCidr, "10.0.0.0/8"),
                C(RuleConditionType.Network, "tcp"),
                C(RuleConditionType.PortRange, "8000:9000")),
        ], RuleAction.Proxy);

        m.Match(new RouteRequest { Domain = "git.corp.example", Port = 8443 }).Rule.Should().NotBeNull();
        m.Match(new RouteRequest { DestinationIp = IPAddress.Parse("10.1.2.3"), Port = 8080 }).Rule.Should().NotBeNull();
        m.Match(new RouteRequest { Domain = "git.corp.example", Port = 443 }).Rule.Should().BeNull("port group must also match");
        m.Match(new RouteRequest { Domain = "git.corp.example", Port = 8443, Network = "udp" }).Rule.Should().BeNull("network group must also match");
    }

    [Fact]
    public void Process_ipv6_regex_and_invert()
    {
        var m = new RuleMatcher(
        [
            R("tg", RuleAction.Proxy, C(RuleConditionType.ProcessName, "Telegram.exe")),
            R("v6", RuleAction.Block, C(RuleConditionType.IpCidr, "2001:db8::/32")),
            R("re", RuleAction.Direct, C(RuleConditionType.DomainRegex, "^cdn[0-9]+\\.example\\.org$")),
            R("inv", RuleAction.Block, C(RuleConditionType.Port, "80", "443")) with { Invert = true },
        ], RuleAction.Direct);

        m.Match(new RouteRequest { ProcessName = "telegram.exe", Port = 443 }).Rule!.Id.Should().Be("tg");
        m.Match(new RouteRequest { DestinationIp = IPAddress.Parse("2001:db8::1"), Port = 443 }).Rule!.Id.Should().Be("v6");
        m.Match(new RouteRequest { Domain = "cdn12.example.org", Port = 443 }).Rule!.Id.Should().Be("re");
        m.Match(new RouteRequest { Domain = "x.org", Port = 25 }).Rule!.Id.Should().Be("inv");
    }

    [Fact]
    public void Geo_conditions_without_data_are_indeterminate_not_guessed()
    {
        var m = new RuleMatcher([R("geo", RuleAction.Direct, C(RuleConditionType.GeoIp, "ir"))], RuleAction.Proxy);

        var d = m.Match(new RouteRequest { DestinationIp = IPAddress.Parse("5.200.200.200") });

        d.Rule.Should().BeNull();
        d.Indeterminate.Should().BeTrue();
    }

    [Theory]
    [InlineData(RuleConditionType.Domain, "exa mple.com")]
    [InlineData(RuleConditionType.IpCidr, "10.0.0.0/33")]
    [InlineData(RuleConditionType.Port, "70000")]
    [InlineData(RuleConditionType.PortRange, "9000:8000")]
    [InlineData(RuleConditionType.DomainRegex, "([a-z")]
    [InlineData(RuleConditionType.ProcessName, "C:\\x\\a.exe")]
    [InlineData(RuleConditionType.ProcessPath, "relative\\a.exe")]
    [InlineData(RuleConditionType.RuleSet, "not-declared")]
    [InlineData(RuleConditionType.Protocol, "carrier-pigeon")]
    [InlineData(RuleConditionType.Network, "icmp")]
    public void Validation_rejects_bad_values(RuleConditionType type, string value)
    {
        RuleValidator.Validate(R("x", RuleAction.Proxy, C(type, value)), []).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validation_accepts_good_rules_and_detects_duplicates()
    {
        var good = R("ok", RuleAction.Direct, C(RuleConditionType.DomainSuffix, "ir", ".example.com"), C(RuleConditionType.PortRange, ":1024"));
        RuleValidator.Validate(good, []).IsValid.Should().BeTrue();
        RuleValidator.ValidateAll([good, good], []).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Iran_preset_is_editable_and_exportable()
    {
        var preset = RoutingPresets.Build(RoutingPreset.IranFriendly);

        preset.Rules.Should().Contain(r => r.Id == "ir-geoip-direct");
        preset.Rules.Single(r => r.Id == "ir-ads-block").Enabled.Should().BeFalse();
        var edited = preset.Rules.Select(r => r.Id == "ir-tld-direct" ? r with { Action = RuleAction.Proxy } : r).ToList();

        var json = RulesPortability.Export(new RulesDocument { Rules = edited, RuleSets = preset.RuleSets });
        var (doc, validation) = RulesPortability.Import(json);

        validation.IsValid.Should().BeTrue(validation.ToString());
        doc!.Rules.Single(r => r.Id == "ir-tld-direct").Action.Should().Be(RuleAction.Proxy);
        doc.RuleSets.Should().HaveCount(3);
    }

    [Fact]
    public void Srs_binary_validation()
    {
        var zlib = new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
        RuleSetManager.ValidateContent([.. "SRS"u8.ToArray(), 1, .. zlib], RuleSetFormat.Binary).Valid.Should().BeTrue();
        RuleSetManager.ValidateContent([.. "XYZ"u8.ToArray(), 1, .. zlib], RuleSetFormat.Binary).Valid.Should().BeFalse();
        RuleSetManager.ValidateContent([.. "SRS"u8.ToArray(), 1, 0x00, 0x01, 0x02], RuleSetFormat.Binary).Valid.Should().BeFalse();
        RuleSetManager.ValidateContent("{\"version\":2,\"rules\":[]}"u8.ToArray(), RuleSetFormat.Source).Valid.Should().BeTrue();
        RuleSetManager.ValidateContent("{\"rules\":\"x\"}"u8.ToArray(), RuleSetFormat.Source).Valid.Should().BeFalse();
    }
}
