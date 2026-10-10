using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Capabilities;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Cores;

/// <summary>gRPC profiles get HTTP/2 health checks, always offer h2, never get MUX, and custom paths go to Xray.</summary>
public class GrpcStabilityTests
{
    private const string Grpc = "vless://11111111-2222-4333-8444-555555555555@g.example.com:443?security=tls&sni=g.example.com&type=grpc&serviceName=tun";

    private static JsonObject SingBox(string link, bool mux = false)
    {
        var plan = new CoreSessionPlan { Kind = SessionKind.SystemProxy, Profiles = [Link(link)], ListenPort = 1, MuxEnabled = mux };
        return SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config["outbounds"]!.AsArray().OfType<JsonObject>().First(o => o["tag"]!.GetValue<string>() == "proxy");
    }

    private static JsonObject XrayStream(string link)
    {
        var plan = new CoreSessionPlan { Kind = SessionKind.ChainBackend, Profiles = [Link(link)], ListenPort = 1 };
        var (config, _, _, _) = XrayConfigBuilder.Build(plan, GoldenConfigTests.Xray26);
        return config["outbounds"]!.AsArray().OfType<JsonObject>().First(o => o["tag"]!.GetValue<string>() == "proxy")["streamSettings"]!.AsObject();
    }

    [Fact]
    public void SingBox_grpc_has_health_checks_and_no_mux()
    {
        var o = SingBox(Grpc + "#g", mux: true);

        o["transport"]!["type"]!.GetValue<string>().Should().Be("grpc");
        o["transport"]!["service_name"]!.GetValue<string>().Should().Be("tun");
        o["transport"]!["idle_timeout"]!.GetValue<string>().Should().Be("15s");
        o["transport"]!["ping_timeout"]!.GetValue<string>().Should().Be("10s");
        o["multiplex"].Should().BeNull("gRPC already multiplexes over one HTTP/2 connection");
    }

    [Fact]
    public void Grpc_always_offers_h2_alpn()
    {
        SingBox(Grpc + "&alpn=http%2F1.1#g")["tls"]!["alpn"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("h2");
        SingBox(Grpc + "&alpn=h2,http%2F1.1#g")["tls"]!["alpn"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("h2", "http/1.1");
        XrayStream(Grpc + "&alpn=http%2F1.1#g")["tlsSettings"]!["alpn"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("h2");
    }

    [Fact]
    public void Xray_grpc_has_health_checks()
    {
        var g = XrayStream(Grpc + "#g")["grpcSettings"]!;

        g["idle_timeout"]!.GetValue<int>().Should().Be(15);
        g["health_check_timeout"]!.GetValue<int>().Should().Be(10);
    }

    [Fact]
    public void Custom_grpc_path_is_sent_to_xray()
    {
        var p = Link("vless://11111111-2222-4333-8444-555555555555@g.example.com:443?security=tls&type=grpc&serviceName=%2Fmy%2Fpath%7Cmulti#c");

        CapabilityMatrix.Negotiate(p, GoldenConfigTests.SingBox114, SessionKind.Tun, false).IsUsable.Should().BeFalse();
        CoreSelector.Select(p, CorePreference.Auto, SessionKind.Tun, false, GoldenConfigTests.SingBox114, GoldenConfigTests.Xray26).Core.Should().Be(CoreKind.Xray);
    }
}
