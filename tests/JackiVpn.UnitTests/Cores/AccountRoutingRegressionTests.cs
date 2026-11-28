using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;

namespace JackiVpn.UnitTests.Cores;

public sealed class AccountRoutingRegressionTests
{
    [Fact]
    public void Core_process_bypass_cannot_capture_traffic_returning_from_the_shaper()
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with { DirectProcessPaths = ["/opt/ibox/sing-box"] };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;
        var route = config["route"]!["rules"]!.AsArray().OfType<JsonObject>().Single(rule => rule["process_path"] is not null);
        route["inbound"]!.AsArray().Select(value => value!.GetValue<string>()).Should().Equal(SingBoxConfigBuilder.TunInboundTag);
        var dns = config["dns"]!["rules"]!.AsArray().OfType<JsonObject>().Single(rule => rule["process_path"] is not null);
        dns["inbound"]!.AsArray().Select(value => value!.GetValue<string>()).Should().Equal(SingBoxConfigBuilder.TunInboundTag);
    }
    [Theory]
    [InlineData(DnsMode.System)]
    [InlineData(DnsMode.Local)]
    public void Claude_uses_tunnel_dns_even_when_other_queries_use_the_local_resolver(DnsMode mode)
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with { Dns = new DnsSettings { Mode = mode } };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;
        var dns = config["dns"]!.AsObject();
        dns["final"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.DnsDirect);
        dns["servers"]!.AsArray().OfType<JsonObject>().Single(server => server["tag"]!.GetValue<string>() == SingBoxConfigBuilder.DnsRemote)["detour"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.ProxyTag);
        var rule = dns["rules"]!.AsArray().OfType<JsonObject>().Single(item => item["domain_suffix"] is JsonArray names && names.Any(name => name!.GetValue<string>() == "claude.ai"));
        rule["server"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.DnsRemote);
    }
    [Fact]
    public void Direct_preset_does_not_force_Claude_through_a_proxy()
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with { Routing = new RoutingSettings { Final = RuleActionType.Direct }, Dns = new DnsSettings { Mode = DnsMode.System } };
        var dns = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config["dns"]!;
        dns["servers"]!.AsArray().OfType<JsonObject>().Should().NotContain(item => item["tag"]!.GetValue<string>() == SingBoxConfigBuilder.DnsRemote);
    }
}
