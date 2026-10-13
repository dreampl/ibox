using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Application.Accounts;
using JackiVpn.Application.Sessions;
using JackiVpn.Application.Traffic;
using JackiVpn.Application.Workplace;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using JackiVpn.UnitTests.Cores;

namespace JackiVpn.UnitTests.Application;

/// <summary>The standalone Dual VPN (WireGuard / IPsec / Kerio next to IBOX) and moving an account between devices.</summary>
public class SecondVpnAndAccountTests
{
    private const string Key = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWY=";

    private static readonly string Conf = $"""
        [Interface]
        PrivateKey = {Key}
        Address = 10.66.0.2/32
        DNS = 10.66.0.1, corp.example.com
        MTU = 1380

        [Peer]
        PublicKey = {Key}
        AllowedIPs = 10.66.0.0/16, 192.168.50.0/24
        Endpoint = vpn.example.com:51820
        PersistentKeepalive = 25
        """;

    [Fact]
    public void A_wireguard_conf_becomes_a_server_and_names_the_company_network()
    {
        var (wg, problem) = WireGuardConf.Parse(Conf, "Acme");

        problem.Should().BeNull();
        wg!.Profile.Protocol.Should().Be(ProxyProtocol.WireGuard);
        wg.Profile.Server.Should().Be("vpn.example.com");
        wg.Profile.Port.Should().Be(51820);
        wg.Profile.WireGuard!.LocalAddresses.Should().Equal("10.66.0.2/32");
        wg.Profile.WireGuard.Mtu.Should().Be(1380);
        wg.Profile.WireGuard.PersistentKeepaliveSeconds.Should().Be(25);
        wg.Subnets.Should().Equal("10.66.0.0/16", "192.168.50.0/24");
        wg.Dns.Should().Equal("10.66.0.1");
        wg.SearchDomains.Should().Equal("corp.example.com");
    }

    [Fact]
    public void A_full_tunnel_conf_says_nothing_about_subnets_and_junk_is_refused()
    {
        var (wg, _) = WireGuardConf.Parse(Conf.Replace("10.66.0.0/16, 192.168.50.0/24", "0.0.0.0/0, ::/0", StringComparison.Ordinal), "Acme");
        wg!.Subnets.Should().BeEmpty();

        WireGuardConf.Parse("[Interface]\nPrivateKey = x", "Acme").Problem.Should().NotBeNull();
        WireGuardConf.Parse(Conf.Replace("vpn.example.com:51820", "vpn.example.com", StringComparison.Ordinal), "Acme").Problem.Should().Contain("host:port");
    }

    private static SecondVpnSettings Dual(SecondVpnKind kind) => new()
    {
        Enabled = true,
        Name = "Acme",
        Kind = kind,
        ServerId = kind is SecondVpnKind.WireGuard or SecondVpnKind.Server ? "dualvpn-1" : null,
        Interface = kind is SecondVpnKind.Kerio or SecondVpnKind.App ? "Kerio Virtual Network" : null,
        Host = kind == SecondVpnKind.Ipsec ? "vpn.acme.example" : null,
        GatewayIps = kind == SecondVpnKind.Ipsec ? ["198.51.100.7"] : [],
        Domains = ["acme.local"],
        Cidrs = ["10.20.0.0/16"],
        Dns = ["10.20.0.53"],
    };

    [Theory]
    [InlineData(TrafficMode.Normal)]
    [InlineData(TrafficMode.Gaming)]
    [InlineData(TrafficMode.Work)]
    public void Dual_vpn_works_in_every_mode_without_a_workplace(TrafficMode mode)
    {
        var policy = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.WireGuard) }, new TrafficContext { Mode = mode });

        policy.WorkRoute.Should().Be(WorkRoute.Server);
        policy.WorkServerId.Should().Be("dualvpn-1");
        policy.InternalDomains.Should().Equal("acme.local");
        policy.InternalCidrs.Should().Equal("10.20.0.0/16");
        policy.InternalDns.Should().Equal("10.20.0.53");
    }

    [Fact]
    public void Off_or_unfinished_dual_vpn_changes_nothing()
    {
        TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.WireGuard) with { Enabled = false } }, new TrafficContext()).WorkRoute.Should().Be(WorkRoute.Off);
        TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.WireGuard) with { Domains = [], Cidrs = [] } }, new TrafficContext()).WorkRoute.Should().Be(WorkRoute.Off);
    }

    [Fact]
    public void Kerio_uses_its_adapter_on_the_desktop_only()
    {
        var kerio = new AppSettings { DualVpn = Dual(SecondVpnKind.Kerio) };
        TrafficPolicyBuilder.Build(kerio, new TrafficContext()).WorkInterface.Should().Be("Kerio Virtual Network");
        TrafficPolicyBuilder.Build(kerio, new TrafficContext { ProcessAware = false }).WorkRoute.Should().Be(WorkRoute.Off, "Android runs one VPN app at a time");
    }

    [Fact]
    public void Ipsec_keeps_its_gateway_out_of_the_tunnel_and_policy_based_ipsec_goes_direct()
    {
        var withAdapter = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.Ipsec) with { Interface = "IBOX Acme" } }, new TrafficContext());
        withAdapter.WorkRoute.Should().Be(WorkRoute.Adapter);
        withAdapter.WorkInterface.Should().Be("IBOX Acme");
        withAdapter.WorkBypass.Should().Equal("198.51.100.7/32");

        // IKEv2 through strongSwan (Linux) has no adapter: its subnets stay outside the tunnel, the kernel's IPsec policy takes them.
        var policyBased = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.Ipsec) }, new TrafficContext());
        policyBased.WorkRoute.Should().Be(WorkRoute.Off);
        policyBased.InternalCidrs.Should().Equal("10.20.0.0/16");
        policyBased.WorkBypass.Should().Equal("198.51.100.7/32");
    }

    [Fact]
    public void The_ipsec_gateway_never_enters_the_tun()
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with
        {
            Kind = SessionKind.Tun,
            Traffic = new TrafficPolicy { InternalCidrs = ["10.20.0.0/16"], WorkRoute = WorkRoute.Adapter, WorkInterface = "IBOX Acme", WorkBypass = ["198.51.100.7/32"] },
        };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;

        var tun = config["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => i["type"]!.GetValue<string>() == "tun");
        tun["route_exclude_address"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Contain(["198.51.100.7/32", "10.20.0.0/16"]);
        config["route"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Where(r => r["ip_cidr"] is JsonArray a && a.Any(v => v!.GetValue<string>() == "198.51.100.7/32"))
            .Should().ContainSingle().Which["outbound"]!.GetValue<string>().Should().Be("direct");
    }

    [Fact]
    public void The_service_rejects_a_bad_gateway()
    {
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(new TrafficPolicy { WorkBypass = ["198.51.100.7/32"] })).Should().NotThrow();
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(new TrafficPolicy { WorkBypass = ["0.0.0.0/0; rm"] })).Should().Throw<TunnelRequestException>();
    }

    [Fact]
    public void An_account_file_opens_only_with_its_password()
    {
        var plain = Encoding.UTF8.GetBytes("{\"version\":1}");
        var file = AccountTransfer.Seal(plain, "correct horse");

        AccountTransfer.Open(file, "correct horse").Should().Equal(plain);
        FluentActions.Invoking(() => AccountTransfer.Open(file, "wrong password")).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => AccountTransfer.Open("not an account"u8.ToArray(), "x")).Should().Throw<InvalidDataException>();

        var tampered = file.ToArray();
        tampered[^1] ^= 1;
        FluentActions.Invoking(() => AccountTransfer.Open(tampered, "correct horse")).Should().Throw<CryptographicException>();
        AccountTransfer.PasswordProblem("12345").Should().NotBeNull();
        AccountTransfer.PasswordProblem("123456").Should().BeNull();
    }

    [Fact]
    public void An_imported_account_keeps_what_belongs_to_this_device()
    {
        var imported = new AppSettings
        {
            Ui = new UiSettings { Theme = AppTheme.Light, Notch = new NotchSettings { Position = 0.1, Display = "1920,0" } },
            Startup = new StartupSettings { StartWithWindows = true },
            Agent = new AgentSettings { Enabled = true },
            Connection = new ConnectionSettings { SelectedProfileId = "gone", SmartMode = false },
            DualVpn = Dual(SecondVpnKind.WireGuard) with { Secret = "device-bound" },
        };
        var here = new AppSettings { Ui = new UiSettings { Notch = new NotchSettings { Position = 0.7 } } };

        var merged = AccountTransfer.Merge(imported, here, new HashSet<string>(StringComparer.Ordinal));

        merged.Ui.Theme.Should().Be(AppTheme.Light, "the account's own look travels");
        merged.Ui.Notch.Position.Should().Be(0.7, "the notch's place belongs to this screen");
        merged.Ui.Notch.Display.Should().BeNull();
        merged.Startup.Should().Be(here.Startup);
        merged.Agent.Should().Be(here.Agent, "AI-agent control is allowed per device");
        merged.Connection.SelectedProfileId.Should().BeNull("that server is not on this device");
        merged.Connection.SmartMode.Should().BeTrue();
        merged.DualVpn.Secret.Should().BeNull("secrets are bound to the device that stored them");
        merged.DualVpn.Enabled.Should().BeFalse("its WireGuard server is not here");

        var withServer = AccountTransfer.Merge(imported, here, new HashSet<string>(["dualvpn-1"], StringComparer.Ordinal));
        withServer.DualVpn.Enabled.Should().BeTrue();
    }
}
