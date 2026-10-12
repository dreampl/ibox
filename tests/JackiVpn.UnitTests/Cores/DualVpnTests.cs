using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Application.Sessions;
using JackiVpn.Application.Traffic;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Cores;

/// <summary>Dual VPN in Work mode: company traffic over the work VPN, everything else over the normal one.</summary>
public class DualVpnTests
{
    private static readonly JackiVpn.Domain.Profiles.ProxyProfile WorkServer =
        Link("trojan://pw-work@vpn.acme.example:443?sni=vpn.acme.example#Acme%20work") with { Id = "work-1" };

    private static WorkplaceProfile Acme(DualVpnSettings dual) => new()
    {
        Name = "Acme",
        InternalDomains = ["acme.local"],
        InternalCidrs = ["10.20.0.0/16"],
        InternalDns = ["10.20.0.53"],
        DualVpn = dual,
    };

    private static AppSettings With(WorkplaceProfile work) => new() { Workplace = new WorkplaceSettings { Profiles = [work] } };

    private static JsonObject Config(TrafficPolicy traffic, SessionKind kind = SessionKind.Tun)
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with { Kind = kind, Traffic = traffic, ListenPort = 20811 };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;
        if (Environment.GetEnvironmentVariable("IBOX_DUMP_CONFIG") is { Length: > 0 } dir)
        {
            File.WriteAllText(Path.Combine(dir, $"dual-{traffic.WorkRoute}-{kind}.json"), config.ToJsonString());
        }

        return config;
    }

    private static List<JsonObject> Rules(JsonObject config) => config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();

    private static JsonObject? Outbound(JsonObject config, string tag) =>
        config["outbounds"]!.AsArray().Concat(config["endpoints"]?.AsArray() ?? []).OfType<JsonObject>().FirstOrDefault(o => o["tag"]!.GetValue<string>() == tag);

    [Fact]
    public void Off_at_the_office_and_on_when_away_in_work_mode()
    {
        var work = Acme(new DualVpnSettings { Route = WorkRoute.Server, ServerId = "work-1" });

        TrafficPolicyBuilder.Build(With(work), new TrafficContext { Mode = TrafficMode.Work, Workplace = work }).WorkRoute.Should().Be(WorkRoute.Off, "on site the company network is right there");
        var away = TrafficPolicyBuilder.Build(With(work), new TrafficContext { Mode = TrafficMode.Work, Workplace = work, Away = true });
        away.WorkRoute.Should().Be(WorkRoute.Server);
        away.WorkServerId.Should().Be("work-1");
        away.InternalDomains.Should().Equal("acme.local");
    }

    [Fact]
    public void Adapter_route_needs_a_desktop()
    {
        var work = Acme(new DualVpnSettings { Route = WorkRoute.Adapter, Interface = "OpenVPN TAP-Windows6" });
        TrafficPolicyBuilder.Build(With(work), new TrafficContext { Mode = TrafficMode.Work, Workplace = work, Away = true }).WorkInterface.Should().Be("OpenVPN TAP-Windows6");
        TrafficPolicyBuilder.Build(With(work), new TrafficContext { Mode = TrafficMode.Work, Workplace = work, Away = true, ProcessAware = false }).WorkRoute.Should().Be(WorkRoute.Off, "Android runs one VPN app at a time");
    }

    [Theory]
    [InlineData(SessionKind.Tun)]
    [InlineData(SessionKind.SystemProxy)]
    public void Work_server_carries_company_names_subnets_and_dns(SessionKind kind)
    {
        var config = Config(new TrafficPolicy
        {
            InternalDomains = ["acme.local"],
            InternalCidrs = ["10.20.0.0/16"],
            InternalDns = ["10.20.0.53"],
            WorkRoute = WorkRoute.Server,
            WorkServerId = "work-1",
            WorkServer = WorkServer,
        }, kind);

        Outbound(config, SingBoxConfigBuilder.WorkTag)!["type"]!.GetValue<string>().Should().Be("trojan");
        var rules = Rules(config);
        rules.Should().Contain(r => r["domain_suffix"] != null && r["domain_suffix"]![0]!.GetValue<string>() == "acme.local" && r["outbound"]!.GetValue<string>() == "work");
        rules.Should().Contain(r => r["ip_cidr"] != null && r["ip_cidr"]![0]!.GetValue<string>() == "10.20.0.0/16" && r["outbound"]!.GetValue<string>() == "work");
        var dns = config["dns"]!["servers"]!.AsArray().OfType<JsonObject>().Single(s => s["tag"]!.GetValue<string>() == SingBoxConfigBuilder.DnsInternal);
        dns["detour"]!.GetValue<string>().Should().Be("work");
        if (kind == SessionKind.Tun)
        {
            var tun = config["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => i["type"]!.GetValue<string>() == "tun");
            (tun["route_exclude_address"]?.AsArray().Select(a => a!.GetValue<string>()) ?? []).Should().NotContain("10.20.0.0/16", "the work server inside the tunnel carries the company subnets");
        }
    }

    [Fact]
    public void Work_adapter_binds_company_traffic_and_lets_the_company_app_out()
    {
        var config = Config(new TrafficPolicy
        {
            InternalDomains = ["acme.local"],
            InternalCidrs = ["10.20.0.0/16"],
            InternalDns = ["10.20.0.53"],
            WorkRoute = WorkRoute.Adapter,
            WorkInterface = "tun9",
        });

        Outbound(config, "work")!["bind_interface"]!.GetValue<string>().Should().Be("tun9");
        var tun = config["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => i["type"]!.GetValue<string>() == "tun");
        tun["route_exclude_address"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Contain("10.20.0.0/16");
        var dns = config["dns"]!["servers"]!.AsArray().OfType<JsonObject>().Single(s => s["tag"]!.GetValue<string>() == SingBoxConfigBuilder.DnsInternal);
        dns["bind_interface"]!.GetValue<string>().Should().Be("tun9");
        var app = Rules(config).Single(r => r["process_path_regex"] is JsonArray a && a.Any(v => v!.GetValue<string>().Contains("openvpn", StringComparison.Ordinal)));
        app["outbound"]!.GetValue<string>().Should().Be("direct");
    }

    [Fact]
    public void Without_a_work_route_company_traffic_stays_direct()
    {
        var config = Config(new TrafficPolicy { InternalDomains = ["acme.local"], InternalCidrs = ["10.20.0.0/16"] });
        Outbound(config, "work").Should().BeNull();
        Rules(config).Should().Contain(r => r["domain_suffix"] != null && r["domain_suffix"]![0]!.GetValue<string>() == "acme.local" && r["outbound"]!.GetValue<string>() == "direct");
    }

    [Fact]
    public void The_service_rejects_a_bad_work_route()
    {
        var ok = new TrafficPolicy { WorkRoute = WorkRoute.Adapter, WorkInterface = "Ethernet 2" };
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(ok)).Should().NotThrow();
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(ok with { WorkInterface = "eth0\"; rm" })).Should().Throw<TunnelRequestException>();
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(ok with { WorkRoute = WorkRoute.Server })).Should().Throw<TunnelRequestException>("no server came with it");
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(new TrafficPolicy { WorkRoute = WorkRoute.Server, WorkServer = WorkServer })).Should().NotThrow();
    }
}
