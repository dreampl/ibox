using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Capabilities;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Cores;

/// <summary>
/// Golden round-trip for the owner's reference config (dummy credentials):
/// sing-box JSON → Parse → Normalize (ProxyProfile) → Generate (sing-box and Xray) must preserve
/// WS Host, WS Path, TLS, SNI, uTLS firefox, ALPN http/1.1, fragment and fragment delay.
/// </summary>
public class GoldenConfigTests
{
    private const string Host = "edge.jacki-example.test";

    internal static readonly CoreCapabilities SingBox114 = CapabilityMatrix.ForSingBox(
        new CoreVersionInfo(CoreKind.SingBox, new Version(1, 14, 2), "sing-box version 1.14.2",
            ["with_gvisor", "with_quic", "with_dhcp", "with_wireguard", "with_utls", "with_acme", "with_clash_api"]));

    internal static readonly CoreCapabilities Xray26 = CapabilityMatrix.ForXray(
        new CoreVersionInfo(CoreKind.Xray, new Version(26, 3, 27), "Xray 26.3.27", []));

    private static ProxyProfile Reference() => Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles.Single();

    private static JsonObject SingBoxProxy(ProxyProfile p, SessionKind kind = SessionKind.SystemProxy)
    {
        var plan = new CoreSessionPlan { Kind = kind, Profiles = [p], ListenPort = 20800 };
        var (config, _, _, _) = SingBoxConfigBuilder.Build(plan, SingBox114);
        return config["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => o["tag"]!.GetValue<string>() == "proxy");
    }

    [Fact]
    public void SingBox_round_trip_preserves_every_golden_field()
    {
        var o = SingBoxProxy(Reference());

        o["type"]!.GetValue<string>().Should().Be("vless");
        o["server"]!.GetValue<string>().Should().Be("104.16.123.45");
        o["server_port"]!.GetValue<int>().Should().Be(443);
        o["uuid"]!.GetValue<string>().Should().Be("11111111-2222-4333-8444-555555555555");
        o["transport"]!["type"]!.GetValue<string>().Should().Be("ws");
        o["transport"]!["path"]!.GetValue<string>().Should().Be("/jacki-ws");
        o["transport"]!["headers"]!["Host"]!.GetValue<string>().Should().Be(Host);
        var tls = o["tls"]!;
        tls["enabled"]!.GetValue<bool>().Should().BeTrue();
        tls["server_name"]!.GetValue<string>().Should().Be(Host);
        tls["utls"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        tls["utls"]!["fingerprint"]!.GetValue<string>().Should().Be("firefox");
        tls["alpn"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("http/1.1");
        tls["fragment"]!.GetValue<bool>().Should().BeTrue();
        tls["fragment_fallback_delay"]!.GetValue<string>().Should().Be("20ms");
        tls["insecure"].Should().BeNull("insecure is never enabled unless the source enabled it");
    }

    [Fact]
    public void SingBox_output_can_be_reparsed_losslessly()
    {
        var original = Reference();
        var regenerated = SingBoxProxy(original).ToJsonString();
        var reparsed = Parse(regenerated).Profiles.Single();

        reparsed.Fingerprint.Should().Be(original.Fingerprint);
        reparsed.Transport.Host.Should().Be(original.Transport.Host);
        reparsed.Transport.Path.Should().Be(original.Transport.Path);
        reparsed.Tls.ServerName.Should().Be(original.Tls.ServerName);
        reparsed.Tls.Fingerprint.Should().Be(original.Tls.Fingerprint);
        reparsed.Tls.Alpn.Should().Equal(original.Tls.Alpn);
        reparsed.Tls.Fragment.Should().Be(original.Tls.Fragment);
    }

    [Fact]
    public void Xray_round_trip_preserves_golden_fields_with_fragment_chain()
    {
        var plan = new CoreSessionPlan { Kind = SessionKind.ChainBackend, Profiles = [Reference()], ListenPort = 20900 };
        var (config, _, _, _) = XrayConfigBuilder.Build(plan, Xray26);
        var outbounds = config["outbounds"]!.AsArray().OfType<JsonObject>().ToList();
        var proxy = outbounds.Single(o => o["tag"]!.GetValue<string>() == "proxy");
        var stream = proxy["streamSettings"]!;

        stream["network"]!.GetValue<string>().Should().Be("ws");
        stream["wsSettings"]!["path"]!.GetValue<string>().Should().Be("/jacki-ws");
        stream["wsSettings"]!["host"]!.GetValue<string>().Should().Be(Host);
        stream["security"]!.GetValue<string>().Should().Be("tls");
        stream["tlsSettings"]!["serverName"]!.GetValue<string>().Should().Be(Host);
        stream["tlsSettings"]!["fingerprint"]!.GetValue<string>().Should().Be("firefox");
        stream["tlsSettings"]!["alpn"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("http/1.1");
        var dialer = stream["sockopt"]!["dialerProxy"]!.GetValue<string>();
        var fragment = outbounds.Single(o => o["tag"]!.GetValue<string>() == dialer);
        fragment["protocol"]!.GetValue<string>().Should().Be("freedom");
        fragment["settings"]!["fragment"]!["packets"]!.GetValue<string>().Should().Be("tlshello");
        fragment["settings"]!["fragment"]!["interval"]!.GetValue<string>().Should().Be("10-20", "20ms fallback delay is approximated as 10-20ms");

        var reparsed = Parse(config.ToJsonString()).Profiles.Single();
        reparsed.Transport.Host.Should().Be(Host);
        reparsed.Tls.Fragment!.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Capability_negotiation_for_reference_profile()
    {
        var p = Reference();

        CapabilityMatrix.Negotiate(p, SingBox114, SessionKind.Tun, false).IsLossless.Should().BeTrue();
        var xr = CapabilityMatrix.Negotiate(p, Xray26, SessionKind.Tun, false);
        xr.IsUsable.Should().BeTrue();
        xr.Issues.Should().Contain(i => i.Impact == CapabilityImpact.Degraded && i.Feature == CoreFeature.TlsFragment);

        var selection = CoreSelector.Select(p, CorePreference.Auto, SessionKind.Tun, false, SingBox114, Xray26);
        selection.Core.Should().Be(CoreKind.SingBox);
        selection.UseChain.Should().BeFalse();
    }

    [Fact]
    public void Fragment_is_not_silently_dropped_on_old_core()
    {
        var old = CapabilityMatrix.ForSingBox(new CoreVersionInfo(CoreKind.SingBox, new Version(1, 11, 4), "sing-box version 1.11.4", ["with_utls"]));
        var report = CapabilityMatrix.Negotiate(Reference(), old, SessionKind.SystemProxy, false);

        report.Issues.Should().Contain(i => i.Feature == CoreFeature.TlsFragment && i.Impact == CapabilityImpact.Degraded);
        report.IsUsable.Should().BeFalse("1.11 is below the minimum supported schema version");
    }

    [Fact]
    public void Xray_only_transport_selects_xray_chain()
    {
        var p = Link("vless://11111111-2222-4333-8444-555555555555@x.example.com:443?security=tls&type=xhttp&path=%2Fx&mode=auto#xhttp");

        var s = CoreSelector.Select(p, CorePreference.Auto, SessionKind.Tun, false, SingBox114, Xray26);

        s.Core.Should().Be(CoreKind.Xray);
        s.UseChain.Should().BeTrue();
        CapabilityMatrix.Negotiate(p, SingBox114, SessionKind.Tun, false).IsUsable.Should().BeFalse();
    }

    [Fact]
    public void Xray_style_fragment_prefers_xray()
    {
        var p = Link("vless://11111111-2222-4333-8444-555555555555@x.example.com:443?security=tls&type=ws&fragment=tlshello,100-200,10-20#f");

        CoreSelector.Select(p, CorePreference.Auto, SessionKind.SystemProxy, false, SingBox114, Xray26).Core.Should().Be(CoreKind.Xray);
    }

    [Fact]
    public void Mux_is_not_applied_to_vision_or_udp_protocols()
    {
        var vision = Link("vless://11111111-2222-4333-8444-555555555555@x.example.com:443?security=tls&type=tcp&flow=xtls-rprx-vision#v");
        var ws = Link("vless://11111111-2222-4333-8444-555555555555@x.example.com:443?security=tls&type=ws#w");

        SingBoxProxy(vision with { }, SessionKind.SystemProxy)["multiplex"].Should().BeNull();
        var plan = new CoreSessionPlan { Kind = SessionKind.SystemProxy, Profiles = [ws], ListenPort = 1, MuxEnabled = true };
        var (config, _, _, _) = SingBoxConfigBuilder.Build(plan, SingBox114);
        config["outbounds"]![0]!["multiplex"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        var visionPlan = plan with { Profiles = [vision] };
        SingBoxConfigBuilder.Build(visionPlan, SingBox114).Config["outbounds"]![0]!["multiplex"].Should().BeNull();
    }

    [Fact]
    public void Unsafe_extensions_never_reach_generated_config()
    {
        const string json = """
        {"type":"trojan","tag":"t","server":"t.example.com","server_port":443,"password":"pw",
         "detour":"evil","bind_interface":"x","tcp_fast_open":true,
         "tls":{"enabled":true,"server_name":"t.example.com","certificate_path":"C:\\Windows\\System32\\config\\SAM","future":1}}
        """;
        var p = Parse(json).Profiles.Single();
        var notes = new List<string>();
        var built = SingBoxOutboundBuilder.Build(p, "proxy", SingBox114, false, new List<string>(), notes);

        built.Node["detour"].Should().BeNull();
        built.Node["bind_interface"].Should().BeNull();
        built.Node["tcp_fast_open"]!.GetValue<bool>().Should().BeTrue();
        built.Node["tls"]!["certificate_path"].Should().BeNull();
        built.Node["tls"]!["future"]!.GetValue<int>().Should().Be(1);
        notes.Should().ContainSingle(n => n.Contains("certificate_path") && n.Contains("detour"));
    }

    [Fact]
    public void Xray26_blocks_removed_features_and_auto_falls_back_to_singbox()
    {
        var insecure = Link("vless://11111111-2222-4333-8444-555555555555@x.example.com:443?security=tls&type=ws&allowInsecure=1#i");
        var legacyKcp = Link("vless://11111111-2222-4333-8444-555555555555@x.example.com:443?type=kcp&headerType=wechat-video&seed=s#k");

        CapabilityMatrix.Negotiate(insecure, Xray26, SessionKind.SystemProxy, false).IsUsable.Should().BeFalse();
        CoreSelector.Select(insecure, CorePreference.Auto, SessionKind.SystemProxy, false, SingBox114, Xray26).Core.Should().Be(CoreKind.SingBox);
        CapabilityMatrix.Negotiate(legacyKcp, Xray26, SessionKind.SystemProxy, false).Issues
            .Should().Contain(i => i.Impact == CapabilityImpact.Blocking && i.Feature == CoreFeature.Kcp);

        var pinned = insecure with { Tls = insecure.Tls with { AllowInsecure = false, CertificateSha256Pins = ["e8e2d387fdbffeb38e9c9065cf30a97ee23c0e3d32ee6f78ffae40966befccc9"] } };
        var (config, _, _, _) = XrayConfigBuilder.Build(new CoreSessionPlan { Kind = SessionKind.ChainBackend, Profiles = [pinned], ListenPort = 1 }, Xray26);
        config["outbounds"]![0]!["streamSettings"]!["tlsSettings"]!["pinnedPeerCertSha256"]!.GetValue<string>()
            .Should().Be("e8e2d387fdbffeb38e9c9065cf30a97ee23c0e3d32ee6f78ffae40966befccc9");
    }

    [Fact]
    public void Secrets_are_collected_for_redaction()
    {
        var secrets = new List<string>();
        SingBoxOutboundBuilder.Build(Reference(), "proxy", SingBox114, false, secrets, new List<string>());

        secrets.Should().Contain("11111111-2222-4333-8444-555555555555");
    }
}
