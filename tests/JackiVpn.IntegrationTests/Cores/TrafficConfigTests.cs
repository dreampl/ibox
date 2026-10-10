using FluentAssertions;
using JackiVpn.Application.Traffic;
using JackiVpn.Cores;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using JackiVpn.IntegrationTests.Infrastructure;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>Traffic control (relay classes, blocks, workplace routes, TUN system proxy) must pass the real sing-box check.</summary>
public sealed class TrafficConfigTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SessionKind.Tun, true)]
    [InlineData(SessionKind.Tun, false)]
    [InlineData(SessionKind.SystemProxy, true)]
    [InlineData(SessionKind.SystemProxy, false)]
    public async Task SingBox_accepts_traffic_policies(SessionKind kind, bool smart)
    {
        cores.RequireCores();
        var settings = new AppSettings
        {
            Traffic = new TrafficControlSettings
            {
                SmartControl = smart,
                Apps = [new AppRule { App = "OneDrive.exe", Blocked = true }, new AppRule { App = "qbittorrent.exe", DownKbps = 500 }],
                SaverApps = ["Dropbox.exe"],
                PriorityApps = [@"C:\Games\My Game\game.exe"],
                BlockedDomains = ["ads.example.com"],
                BlockedIps = ["203.0.113.7", "198.51.100.0/24"],
            },
        };
        var work = new WorkplaceProfile { Name = "Acme", InternalDomains = ["acme.local", "intranet.acme.com"], InternalCidrs = ["10.20.0.0/16"], InternalDns = ["10.20.0.53"], Rules = new CompanyRules { BlockApps = ["utorrent.exe"] } };
        var policy = TrafficPolicyBuilder.Build(settings, new TrafficContext { Mode = TrafficMode.Eco, Workplace = work });
        var plan = new CoreSessionPlan
        {
            Kind = kind,
            Profiles = [CoreFixture.ReferenceProfile()],
            ListenPort = 20811,
            Routing = new RoutingSettings
            {
                Rules = [new RoutingRule { Id = "tg", Name = "tg", Action = RuleAction.Direct, Conditions = [new RuleCondition(RuleConditionType.ProcessName, ["Telegram.exe"])] }],
            },
            Traffic = policy,
            Shaper = smart ? new ShaperEndpoint(20812, "relay-pw", 20813, new InboundCredentials("back", "back-pw")) : null,
            TunProxyPort = kind == SessionKind.Tun ? 20814 : 0,
            DirectProcessPaths = [@"C:\x\sing-box.exe"],
        };

        var config = cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!);
        var result = await cores.SingBox.ValidateAsync(config, Ct);

        result.IsValid.Should().BeTrue(result.RedactedOutput);
    }

    [Fact]
    public async Task Relay_round_trip_through_real_core_reaches_the_server_and_reports_the_owner()
    {
        cores.RequireCores();
        using var target = NetHelpers.StartTarget("via-relay");
        var cert = new TestCertificate(cores.WorkDir, "edge.jacki-example.test");
        using var _ = cert;
        var serverPort = NetHelpers.FreePort();
        await using var server = await cores.StartRawAsync(Domain.Connection.CoreKind.SingBox, NetHelpers.VlessWsTlsServerConfig(serverPort, "11111111-2222-4333-8444-555555555555", "edge.jacki-example.test", "/jacki-ws", cert));
        await server.WaitForPortAsync(serverPort, TimeSpan.FromSeconds(15), Ct);

        var p = CoreFixture.ReferenceProfile();
        var mixed = NetHelpers.FreePort();
        var api = new ClashApiOptions(NetHelpers.FreePort(), JackiVpn.Cores.Hosting.PortAllocator.NewApiSecret());
        var shaper = new ShaperEndpoint(NetHelpers.FreePort(), "relay-pw", NetHelpers.FreePort(), new InboundCredentials("back", "back-pw"));
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            Profiles = [p with { Server = "127.0.0.1", Port = serverPort, Tls = p.Tls with { AllowInsecure = true } }],
            ListenPort = mixed,
            Routing = new RoutingSettings { BypassLan = false },
            ClashApi = api,
            Traffic = new TrafficPolicy { Shaping = true, ShapeBackground = true, TrackProcesses = true },
            Shaper = shaper,
        };
        var config = cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!);
        using var clash = new JackiVpn.Cores.Hosting.ClashApiClient(api);
        await using var relay = new TrafficShaper(shaper, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, clash.GetConnectionsAsync);
        relay.SetLimits(new TrafficLimits { Classes = new Dictionary<string, ShapeRate> { [ShapeClasses.Background] = new(200 * 1024, 200 * 1024) } });
        relay.Start();
        var client = await cores.StartAsync(cores.SingBox, config);
        try
        {
            await client.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);
            using var http = NetHelpers.ClientVia($"http://127.0.0.1:{mixed}");
            var url = $"http://127.0.0.1:{NetHelpers.PortOf(target)}/probe";
            (await http.GetStringAsync(url, Ct)).Should().Be("via-relay");
            server.RecentOutput.Should().Contain(l => l.Contains($"127.0.0.1:{NetHelpers.PortOf(target)}", StringComparison.Ordinal), "the relayed request must reach the VPN server");

            var connections = await clash.GetConnectionsAsync(Ct);
            connections.Should().NotBeNull();
            relay.Relayed.Should().BePositive("the background class must pass the relay and come back through jv-back");
        }
        finally
        {
            await client.StopAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync();
        }
    }
}
