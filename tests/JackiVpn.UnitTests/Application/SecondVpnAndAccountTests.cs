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
        Scope = SecondVpnScope.Custom,
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
        TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.WireGuard) with { ServerId = null, Scope = SecondVpnScope.Smart } }, new TrafficContext()).WorkRoute.Should().Be(WorkRoute.Off);
    }

    [Fact]
    public void Smart_scope_takes_what_the_second_vpn_announces_and_company_ranges_but_not_this_network()
    {
        var found = new SecondVpnDetected
        {
            Domains = ["corp.acme.example"],
            Cidrs = ["10.66.0.0/16"],
            Dns = ["10.66.0.1"],
            LocalSubnets = ["192.168.1.0/24"],
        };
        var smart = Dual(SecondVpnKind.WireGuard) with { Scope = SecondVpnScope.Smart, Domains = [], Cidrs = [], Dns = [] };

        var policy = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = smart }, new TrafficContext { DualDetected = found });

        policy.WorkRoute.Should().Be(WorkRoute.Server, "smart needs no list");
        policy.InternalDomains.Should().Equal("corp.acme.example");
        policy.InternalCidrs.Should().StartWith("10.66.0.0/16").And.Contain(TrafficPolicyBuilder.CompanyRanges);
        policy.InternalDns.Should().Equal("10.66.0.1");
        policy.WorkLocal.Should().Equal("192.168.1.0/24");

        // The user's own lists still count, and their own resolvers win over the announced ones.
        var both = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = smart with { Domains = ["acme.local"], Dns = ["10.20.0.53"] } }, new TrafficContext { DualDetected = found });
        both.InternalDomains.Should().Equal("acme.local", "corp.acme.example");
        both.InternalDns.Should().Equal("10.20.0.53");

        // Custom: only the lists, nothing detected.
        var custom = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.WireGuard) }, new TrafficContext { DualDetected = found });
        custom.InternalCidrs.Should().Equal("10.20.0.0/16");
        custom.InternalDomains.Should().Equal("acme.local");
    }

    [Fact]
    public void Alone_the_second_vpn_carries_everything()
    {
        var policy = TrafficPolicyBuilder.Build(new AppSettings { DualVpn = Dual(SecondVpnKind.WireGuard) }, new TrafficContext { DualOnly = true });

        policy.WorkRoute.Should().Be(WorkRoute.Off, "its server is the main outbound; nothing is split");
        policy.InternalDomains.Should().BeEmpty();
        policy.InternalCidrs.Should().BeEmpty();
    }

    [Fact]
    public void A_work_server_with_company_ranges_keeps_only_this_network_out_of_the_tun()
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with
        {
            Kind = SessionKind.Tun,
            Routing = new RoutingSettings { BypassLan = true },
            Traffic = new TrafficPolicy
            {
                InternalCidrs = ["10.66.0.0/16", .. TrafficPolicyBuilder.CompanyRanges],
                WorkRoute = WorkRoute.Server,
                WorkServerId = "dualvpn-1",
                WorkServer = TestSupport.Link("trojan://pw-work@vpn.acme.example:443?sni=vpn.acme.example#Acme") with { Id = "dualvpn-1" },
                WorkLocal = ["192.168.1.0/24"],
            },
        };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;

        var tun = config["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => i["type"]!.GetValue<string>() == "tun");
        var excluded = tun["route_exclude_address"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        excluded.Should().Contain("192.168.1.0/24").And.NotContain(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"], "the company may live in any private range");

        var rules = config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var local = rules.FindIndex(r => r["ip_cidr"] is JsonArray a && a.Any(v => v!.GetValue<string>() == "192.168.1.0/24"));
        var company = rules.FindIndex(r => r["ip_cidr"] is JsonArray a && a.Any(v => v!.GetValue<string>() == "10.0.0.0/8"));
        local.Should().BeGreaterThan(-1);
        rules[local]["outbound"]!.GetValue<string>().Should().Be("direct");
        rules[company]["outbound"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.WorkTag);
        local.Should().BeLessThan(company, "this device's own network wins over the company ranges");
    }

    [Fact]
    public void Alita_is_never_cut_and_takes_its_own_path_when_allowed()
    {
        var settings = new AppSettings
        {
            Traffic = new TrafficControlSettings { Apps = [new AppRule { App = "Alita.exe", Blocked = true }, new AppRule { App = "torrent.exe", Blocked = true }] },
            Friends = new FriendAppsSettings { Direct = true },
        };

        TrafficPolicyBuilder.LimitedApps(settings, null).Should().Equal("torrent.exe");
        var direct = settings with { Traffic = settings.Traffic with { SmartControl = false } };
        TrafficPolicyBuilder.Build(direct, new TrafficContext()).BlockedApps.Should().Equal("torrent.exe");

        var policy = TrafficPolicyBuilder.Build(settings, new TrafficContext());
        policy.Friends.Should().Equal("alita");
        policy.FriendsDirect.Should().BeTrue();
        TrafficPolicyBuilder.Build(settings, new TrafficContext { ProcessAware = false }).FriendsDirect.Should().BeFalse("Android has no per-process rules");

        var pattern = new System.Text.RegularExpressions.Regex(SingBoxConfigBuilder.FriendPattern("alita")!);
        pattern.IsMatch(@"C:\Program Files\Alita\Alita.exe").Should().BeTrue();
        pattern.IsMatch("/opt/alita/alita-agent").Should().BeTrue();
        pattern.IsMatch(@"C:\Alita\chrome.exe").Should().BeFalse("only the program's own name counts, not its folder");
        SingBoxConfigBuilder.FriendPattern("a|b").Should().BeNull();

        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with { Kind = SessionKind.Tun, Traffic = policy };
        var rules = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var friend = rules.FindIndex(r => r["process_path_regex"] is JsonArray a && a.Any(v => v!.GetValue<string>().Contains("alita", StringComparison.Ordinal)));
        friend.Should().BeGreaterThan(-1);
        rules[friend]["outbound"]!.GetValue<string>().Should().Be("direct");
        friend.Should().BeLessThan(rules.FindIndex(r => r["action"]?.GetValue<string>() == "hijack-dns"), "its own name lookups go its own way too");

        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(new TrafficPolicy { Friends = ["alita"], FriendsDirect = true })).Should().NotThrow();
        FluentActions.Invoking(() => TunnelSessionManager.ValidateTraffic(new TrafficPolicy { Friends = ["a|.*"], FriendsDirect = true })).Should().Throw<TunnelRequestException>();
    }

    [Fact]
    public void Ten_distinct_avatars_and_a_free_one_for_each_new_account()
    {
        JackiVpn.Domain.Visuals.Avatars.All.Should().HaveCount(10);
        JackiVpn.Domain.Visuals.Avatars.All.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        JackiVpn.Domain.Visuals.Avatars.All.Should().OnlyContain(a => a.Layers.Count > 0);

        var first = JackiVpn.Domain.Visuals.Avatars.NextFree([]);
        JackiVpn.Domain.Visuals.Avatars.NextFree([first]).Should().NotBe(first);
        JackiVpn.Domain.Visuals.Avatars.Find(first).Should().NotBeNull();
        JackiVpn.Domain.Visuals.Avatars.Find("nope").Should().BeNull();
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
    public void On_the_workplace_network_its_own_dns_answers_internal_and_direct_names()
    {
        // No DNS suffix announced, a manual direct rule for an internal name, and IBOX's own tunnel resolver mixed into the
        // saved resolvers: the company resolver still answers, the tunnel's never does.
        var rule = new JackiVpn.Domain.Routing.RoutingRule { Id = "r", Name = "intranet", Action = JackiVpn.Domain.Routing.RuleAction.Direct, Conditions = [new JackiVpn.Domain.Routing.RuleCondition(JackiVpn.Domain.Routing.RuleConditionType.DomainSuffix, ["intra.soha"])] };
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with
        {
            Kind = SessionKind.Tun,
            Routing = new RoutingSettings { Rules = [rule] },
            Traffic = new TrafficPolicy { InternalCidrs = ["10.10.0.0/16"], InternalDns = ["172.19.0.2", "10.10.0.5"] },
        };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;

        var servers = config["dns"]!["servers"]!.AsArray().OfType<JsonObject>().ToList();
        servers.Should().ContainSingle(s => s["tag"]!.GetValue<string>() == SingBoxConfigBuilder.DnsInternal).Which["server"]!.GetValue<string>().Should().Be("10.10.0.5");
        config["dns"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Should().Contain(r => r["domain_suffix"] != null && r["domain_suffix"]![0]!.GetValue<string>() == "intra.soha" && r["server"]!.GetValue<string>() == SingBoxConfigBuilder.DnsInternal);

        SingBoxConfigBuilder.CompanyResolver(new TrafficPolicy { InternalDns = ["172.19.0.2", "198.18.0.1"] }).Should().BeNull();

        // Names the core dials itself (system proxy, fast DNS) go out through the company resolver too.
        config["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => o["tag"]!.GetValue<string>() == "direct")["domain_resolver"]!.GetValue<string>().Should().Be(SingBoxConfigBuilder.DnsInternal);
    }

    [Fact]
    public void The_networks_current_dns_backs_up_a_workplace_saved_without_one()
    {
        var work = new WorkplaceProfile { Name = "Soha", InternalCidrs = ["10.10.0.0/16"], InternalDns = ["172.19.0.2"] };
        var policy = TrafficPolicyBuilder.Build(new AppSettings(), new TrafficContext { Workplace = work, SiteDns = ["10.10.0.5", "8.8.8.8"] });
        policy.InternalDns.Should().Equal("172.19.0.2", "10.10.0.5");
        SingBoxConfigBuilder.CompanyResolver(policy).Should().Be("10.10.0.5");
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
