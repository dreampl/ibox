using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Cores;

public class DnsAndTunPolicyTests
{
    private static readonly RoutingRule IrDirect = new()
    {
        Id = "ir",
        Name = "ir",
        Action = RuleAction.Direct,
        Conditions = [new RuleCondition(RuleConditionType.DomainSuffix, ["ir"]), new RuleCondition(RuleConditionType.GeoSite, ["category-ir"])],
    };

    private static JsonObject Build(CoreSessionPlan plan) => SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;

    private static CoreSessionPlan Tun(Action<CoreSessionPlanBuilder>? configure = null)
    {
        var b = new CoreSessionPlanBuilder();
        configure?.Invoke(b);
        return b.Plan;
    }

    [Fact]
    public void Auto_secure_dns_uses_doh_through_tunnel_and_separate_bootstrap()
    {
        var c = Build(Tun());
        var servers = c["dns"]!["servers"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s["tag"]!.GetValue<string>());

        servers["dns-remote"]["type"]!.GetValue<string>().Should().Be("https");
        servers["dns-remote"]["server"]!.GetValue<string>().Should().Be("1.1.1.1");
        servers["dns-remote"]["detour"]!.GetValue<string>().Should().Be("proxy");
        servers["dns-bootstrap"]["detour"].Should().BeNull("bootstrap must not depend on the tunnel it bootstraps");
        c["dns"]!["final"]!.GetValue<string>().Should().Be("dns-remote");
        c["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>().Should().Be("dns-bootstrap");
    }

    [Fact]
    public void Tun_captures_all_dns_and_uses_strict_route()
    {
        var c = Build(Tun());
        var tun = c["inbounds"]![0]!;
        var rules = c["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();

        tun["type"]!.GetValue<string>().Should().Be("tun");
        tun["auto_route"]!.GetValue<bool>().Should().BeTrue();
        tun["strict_route"]!.GetValue<bool>().Should().BeTrue();
        tun["address"]!.AsArray().Should().HaveCount(2, "both v4 and v6 are captured so IPv6 cannot bypass the tunnel");
        c["route"]!["auto_detect_interface"]!.GetValue<bool>().Should().BeTrue();
        rules.Should().Contain(r => r["protocol"] != null && r["protocol"]!.GetValue<string>() == "dns" && r["action"]!.GetValue<string>() == "hijack-dns");
        rules.Should().Contain(r => r["port"] != null && r["port"]!.GetValue<int>() == 53 && r["action"]!.GetValue<string>() == "hijack-dns");
    }

    [Fact]
    public void Ipv6_unsupported_rejects_v6_and_resolves_ipv4_only()
    {
        var c = Build(Tun(b => b.Plan = b.Plan with { Ipv6 = Ipv6Mode.Auto, TunnelSupportsIpv6 = false }));

        c["dns"]!["strategy"]!.GetValue<string>().Should().Be("ipv4_only");
        c["route"]!["rules"]!.AsArray().Should().Contain(r => r!["ip_version"] != null && r!["ip_version"]!.GetValue<int>() == 6 && r!["action"]!.GetValue<string>() == "reject");
    }

    [Fact]
    public void Ipv6_supported_routes_v6()
    {
        var c = Build(Tun(b => b.Plan = b.Plan with { Ipv6 = Ipv6Mode.Auto, TunnelSupportsIpv6 = true }));

        c["dns"]!["strategy"]!.GetValue<string>().Should().Be("prefer_ipv4");
        c["route"]!["rules"]!.AsArray().Should().NotContain(r => r!["ip_version"] != null);
    }

    [Fact]
    public void Direct_domains_resolve_directly_and_unavailable_rule_sets_are_not_referenced()
    {
        var withSet = Build(Tun(b => b.Plan = b.Plan with
        {
            Routing = new RoutingSettings { Rules = [IrDirect] },
            RuleSetPaths = new Dictionary<string, string> { ["geosite-category-ir"] = @"C:\cache\geosite-category-ir.srs" },
        }));
        var dnsRules = withSet["dns"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        dnsRules.Should().Contain(r => r["server"] != null && r["server"]!.GetValue<string>() == "dns-direct" && r["domain_suffix"] != null);
        withSet["route"]!["rule_set"]!.AsArray().Should().ContainSingle();

        var (without, _, notes, _) = SingBoxConfigBuilder.Build(Tun(b => b.Plan = b.Plan with { Routing = new RoutingSettings { Rules = [IrDirect] } }), GoldenConfigTests.SingBox114);
        without["route"]!["rule_set"].Should().BeNull();
        notes.Should().Contain(n => n.Contains("unavailable"));
        without["route"]!["rules"]!.AsArray().Should().Contain(r => r!["domain_suffix"] != null, "the rule keeps its remaining conditions");
    }

    [Fact]
    public void Rule_whose_only_destination_is_unavailable_is_dropped_not_widened()
    {
        var geoOnly = new RoutingRule { Id = "g", Name = "g", Action = RuleAction.Direct, Conditions = [new RuleCondition(RuleConditionType.GeoIp, ["ir"])] };
        var (c, _, notes, _) = SingBoxConfigBuilder.Build(Tun(b => b.Plan = b.Plan with { Routing = new RoutingSettings { Rules = [geoOnly] } }), GoldenConfigTests.SingBox114);

        c["route"]!["rules"]!.AsArray().Should().NotContain(r => r!["outbound"] != null && r!["outbound"]!.GetValue<string>() == "direct" && r!["ip_is_private"] == null);
        notes.Should().Contain(n => n.Contains("skipped"));
    }

    [Fact]
    public void Chain_mode_excludes_backend_process_and_server_ips_from_tunnel()
    {
        var c = Build(Tun(b => b.Plan = b.Plan with
        {
            ChainBackend = new ChainBackendEndpoint(21000, @"C:\Program Files\JackiVpn\cores\xray.exe"),
            Tun = new TunOptions { RouteExcludeAddresses = ["104.16.123.45/32"] },
        }));

        c["outbounds"]![0]!["type"]!.GetValue<string>().Should().Be("socks");
        c["outbounds"]![0]!["server_port"]!.GetValue<int>().Should().Be(21000);
        c["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Contain("104.16.123.45/32");
        c["route"]!["rules"]!.AsArray().Should().Contain(r => r!["process_path"] != null && r!["outbound"]!.GetValue<string>() == "direct");
        c["dns"]!["rules"]!.AsArray().Should().Contain(r => r!["process_path"] != null && r!["server"]!.GetValue<string>() == "dns-bootstrap");
    }

    [Fact]
    public void System_dns_mode_is_direct_only()
    {
        var c = Build(Tun(b => b.Plan = b.Plan with { Dns = new DnsSettings { Mode = DnsMode.System } }));

        c["dns"]!["final"]!.GetValue<string>().Should().Be("dns-direct");
        c["dns"]!["servers"]!.AsArray().Should().NotContain(s => s!["detour"] != null);
    }

    [Fact]
    public void Proxy_mode_listens_on_loopback_only_by_default()
    {
        var c = Build(new CoreSessionPlan { Kind = SessionKind.SystemProxy, Profiles = [Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0]], ListenPort = 20811 });

        c["inbounds"]![0]!["type"]!.GetValue<string>().Should().Be("mixed");
        c["inbounds"]![0]!["listen"]!.GetValue<string>().Should().Be("127.0.0.1");
        c["route"]!["auto_detect_interface"].Should().BeNull();
    }

    [Fact]
    public void Without_ipv6_aaaa_queries_get_an_empty_answer_so_apps_never_dial_ipv6()
    {
        var rules = Build(Tun(b => b.Plan = b.Plan with { Ipv6 = Ipv6Mode.Disable }))["dns"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var aaaa = rules.Single(r => r["query_type"] is JsonArray q && q.Any(x => x!.GetValue<string>() == "AAAA"));
        aaaa["action"]!.GetValue<string>().Should().Be("predefined");
        aaaa["rcode"]!.GetValue<string>().Should().Be("NOERROR");

        var fakeIndex = rules.FindIndex(r => r["server"]?.GetValue<string>() == "dns-fakeip");
        rules.IndexOf(aaaa).Should().BeLessThan(fakeIndex, "the empty AAAA answer must win over fake-IP");
    }

    [Fact]
    public void With_ipv6_aaaa_is_answered_normally()
    {
        Build(Tun(b => b.Plan = b.Plan with { Ipv6 = Ipv6Mode.Enable }))["dns"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Should().NotContain(r => r["action"] != null && r["action"]!.GetValue<string>() == "predefined");
    }

    [Fact]
    public void Fake_ip_is_the_tun_default_and_persists_its_mapping()
    {
        var c = Build(Tun());
        c["dns"]!["servers"]!.AsArray().Should().Contain(s => s!["type"]!.GetValue<string>() == "fakeip");
        c["experimental"]!["cache_file"]!["store_fakeip"]!.GetValue<bool>().Should().BeTrue("fake addresses must stay valid across a core restart");
        c["experimental"]!["clash_api"].Should().BeNull("this plan has no stats API; cache_file must not depend on it");
    }

    [Fact]
    public void Fake_ip_can_be_disabled_and_never_applies_outside_tun()
    {
        Build(Tun(b => b.Plan = b.Plan with { Dns = new DnsSettings { FakeIpEnabled = false } }))["dns"]!["servers"]!.AsArray()
            .Should().NotContain(s => s!["type"]!.GetValue<string>() == "fakeip");
        var proxy = Build(Tun(b => b.Plan = b.Plan with { Kind = SessionKind.SystemProxy, ListenPort = 10808 }));
        proxy["dns"]!["servers"]!.AsArray().Should().NotContain(s => s!["type"]!.GetValue<string>() == "fakeip");
        proxy["experimental"].Should().BeNull();
    }

    [Fact]
    public void Fake_ip_is_off_in_system_dns_mode()
    {
        Build(Tun(b => b.Plan = b.Plan with { Dns = new DnsSettings { Mode = DnsMode.System } }))["dns"]!["servers"]!.AsArray()
            .Should().NotContain(s => s!["type"]!.GetValue<string>() == "fakeip");
    }

    [Theory]
    [InlineData(TunStack.Mixed, "mixed")]
    [InlineData(TunStack.System, "system")]
    [InlineData(TunStack.Gvisor, "gvisor")]
    public void Tun_stack_is_explicit(TunStack stack, string expected) =>
        Build(Tun(b => b.Plan = b.Plan with { TunStack = stack }))["inbounds"]![0]!["stack"]!.GetValue<string>().Should().Be(expected);

    [Fact]
    public void Quic_is_rejected_after_user_rules_so_direct_quic_still_works()
    {
        var c = Build(Tun(b => b.Plan = b.Plan with { BlockQuic = true, Routing = new RoutingSettings { Rules = [IrDirect] } }));
        var rules = c["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var quic = rules.FindIndex(r => r["network"]?.GetValue<string>() == "udp" && r["port"]?.GetValue<int>() == 443);
        var direct = rules.FindIndex(r => r["domain_suffix"] is not null && r["outbound"]?.GetValue<string>() == "direct");

        quic.Should().BeGreaterThan(direct, "user Direct rules must match before the QUIC block");
        rules[quic]["action"]!.GetValue<string>().Should().Be("reject");
    }

    [Fact]
    public void Quic_is_not_blocked_when_unmatched_traffic_goes_direct_or_when_disabled()
    {
        static bool HasQuicBlock(JsonObject c) => c["route"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Any(r => r["network"]?.GetValue<string>() == "udp" && r["port"]?.GetValue<int>() == 443);

        HasQuicBlock(Build(Tun(b => b.Plan = b.Plan with { BlockQuic = true, Routing = new RoutingSettings { Final = RuleActionType.Direct } }))).Should().BeFalse();
        HasQuicBlock(Build(Tun())).Should().BeFalse();
    }

    [Fact]
    public void Inbound_credentials_are_required_when_set_and_registered_as_secrets()
    {
        var plan = Tun(b => b.Plan = b.Plan with { Kind = SessionKind.SystemProxy, ExternalTun = true, ListenPort = 10808, InboundAuth = new InboundCredentials("jacki-user", "s3cret-pass") });
        var (config, secrets, _, _) = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114);

        var users = config["inbounds"]![0]!["users"]!.AsArray();
        users.Should().ContainSingle();
        users[0]!["username"]!.GetValue<string>().Should().Be("jacki-user");
        users[0]!["password"]!.GetValue<string>().Should().Be("s3cret-pass");
        secrets.Should().Contain("s3cret-pass");
    }

    [Fact]
    public void Windows_system_proxy_inbound_stays_open_for_wininet_clients()
    {
        Build(Tun(b => b.Plan = b.Plan with { Kind = SessionKind.SystemProxy, ListenPort = 10808 }))["inbounds"]![0]!["users"].Should().BeNull();
    }

    internal sealed class CoreSessionPlanBuilder
    {
        public CoreSessionPlan Plan { get; set; } = new()
        {
            Kind = SessionKind.Tun,
            Profiles = [Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0]],
        };
    }
}
