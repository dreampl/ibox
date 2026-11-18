using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.IntegrationTests.Infrastructure;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>
/// User rules decide the outbound in the real core. Everything is on loopback (a local VLESS server and local
/// HTTP targets behind a local mixed inbound); nothing on the host network is changed.
/// </summary>
public sealed class RoutingRuntimeTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private const string Sni = "edge.jacki-example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RoutingRule R(string id, RuleAction action, params RuleCondition[] c) => new() { Id = id, Name = id, Action = action, Conditions = c };

    [Fact]
    public async Task Direct_rule_skips_the_server_proxy_rule_uses_it_and_block_rule_refuses()
    {
        cores.RequireCores();
        using var directTarget = NetHelpers.StartTarget("direct-target");
        using var proxyTarget = NetHelpers.StartTarget("proxy-target");
        using var blockedTarget = NetHelpers.StartTarget("blocked-target");
        var directPort = NetHelpers.PortOf(directTarget);
        var proxyPort = NetHelpers.PortOf(proxyTarget);
        var blockedPort = NetHelpers.PortOf(blockedTarget);

        using var cert = new TestCertificate(cores.WorkDir, Sni);
        var serverPort = NetHelpers.FreePort();
        await using var server = await cores.StartRawAsync(CoreKind.SingBox, NetHelpers.VlessWsTlsServerConfig(serverPort, Uuid, Sni, "/jacki-ws", cert));
        await server.WaitForPortAsync(serverPort, TimeSpan.FromSeconds(15), Ct);

        var p = CoreFixture.ReferenceProfile();
        var profile = p with { Server = "127.0.0.1", Port = serverPort, Tls = p.Tls with { AllowInsecure = true } };
        var mixed = NetHelpers.FreePort();
        var api = new ClashApiOptions(NetHelpers.FreePort(), PortAllocator.NewApiSecret());
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            Profiles = [profile],
            ListenPort = mixed,
            ClashApi = api,
            Routing = new RoutingSettings
            {
                BypassLan = false,
                Final = RuleActionType.Proxy,
                Rules =
                [
                    R("block-port", RuleAction.Block, new RuleCondition(RuleConditionType.Port, [blockedPort.ToString(System.Globalization.CultureInfo.InvariantCulture)])),
                    R("direct-port", RuleAction.Direct, new RuleCondition(RuleConditionType.IpCidr, ["127.0.0.0/8"]), new RuleCondition(RuleConditionType.Port, [directPort.ToString(System.Globalization.CultureInfo.InvariantCulture)])),
                    R("disabled-direct-all", RuleAction.Direct, new RuleCondition(RuleConditionType.IpCidr, ["127.0.0.0/8"])) with { Enabled = false },
                ],
            },
        };
        var config = cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!);
        var check = await cores.SingBox.ValidateAsync(config, Ct);
        check.IsValid.Should().BeTrue(check.RedactedOutput);

        await using var client = await cores.StartAsync(cores.SingBox, config);
        await client.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);
        using var http = NetHelpers.ClientVia($"http://127.0.0.1:{mixed}", TimeSpan.FromSeconds(10));

        (await http.GetStringAsync($"http://127.0.0.1:{proxyPort}/probe", Ct)).Should().Be("proxy-target");
        (await http.GetStringAsync($"http://127.0.0.1:{directPort}/probe", Ct)).Should().Be("direct-target");
        var blocked = async () => await http.GetStringAsync($"http://127.0.0.1:{blockedPort}/probe", Ct);
        await blocked.Should().ThrowAsync<Exception>("the block rule rejects the connection");

        // The VLESS server logs every destination it dials: only the proxied target may be there.
        await Task.Delay(300, Ct);
        server.RecentOutput.Should().Contain(l => l.Contains($"127.0.0.1:{proxyPort}", StringComparison.Ordinal), "the proxy rule (final) goes through the server");
        server.RecentOutput.Should().NotContain(l => l.Contains($"127.0.0.1:{directPort}", StringComparison.Ordinal), "the direct rule must not touch the server");
        server.RecentOutput.Should().NotContain(l => l.Contains($"127.0.0.1:{blockedPort}", StringComparison.Ordinal), "blocked traffic goes nowhere");
        blockedTarget.LogEntries.Should().BeEmpty("the blocked target was never reached");
        directTarget.LogEntries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Xray_accepts_a_backend_config_with_user_rules()
    {
        cores.RequireCores();
        var config = cores.Xray.GenerateConfig(new CoreSessionPlan
        {
            Kind = SessionKind.ChainBackend,
            Profiles = [CoreFixture.ReferenceProfile()],
            ListenPort = NetHelpers.FreePort(),
            Routing = new RoutingSettings
            {
                Rules =
                [
                    R("ads", RuleAction.Block, new RuleCondition(RuleConditionType.DomainKeyword, ["doubleclick"])),
                    R("ir", RuleAction.Direct, new RuleCondition(RuleConditionType.DomainSuffix, [".ir"]), new RuleCondition(RuleConditionType.Network, ["tcp"])),
                    R("re", RuleAction.Direct, new RuleCondition(RuleConditionType.DomainRegex, ["^cdn[0-9]+\\.example\\.com$"])),
                    R("games", RuleAction.Direct, new RuleCondition(RuleConditionType.PortRange, ["27000:27100"]), new RuleCondition(RuleConditionType.Protocol, ["quic"])),
                    R("lan", RuleAction.Proxy, new RuleCondition(RuleConditionType.IpCidr, ["10.0.0.0/8", "fd00::/8"])),
                ],
            },
        }, cores.XrayCaps!);
        config.Json.Should().Contain("keyword:doubleclick").And.Contain("domain:ir").And.Contain("blackhole");

        var result = await cores.Xray.ValidateAsync(config, Ct);

        result.IsValid.Should().BeTrue(result.RedactedOutput);
    }
}
