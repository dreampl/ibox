using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.IntegrationTests.Infrastructure;
using static JackiVpn.IntegrationTests.Infrastructure.DnsWire;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>
/// What apps actually get from the tunnel's DNS. The generated TUN config is run by the real sing-box with the
/// TUN inbound replaced by a loopback DNS listener (no admin, no system changes), and real DNS packets are sent.
/// </summary>
public sealed class DnsBehaviorTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_tunnel_ipv6_apps_get_ipv4_only()
    {
        cores.RequireCores();
        await using var dns = await StartAsync(Ipv6Mode.Disable);

        var a = await QueryAsync(dns.Port, "api.anthropic.com", TypeA);
        a.Rcode.Should().Be(0);
        a.Answers.Should().ContainSingle(x => x.Type == TypeA, "A records are answered (fake-IP, resolved by the server)");
        IPAddress.Parse(a.Answers[0].Data).GetAddressBytes()[0].Should().Be(198, "fake-IP range 198.18.0.0/15");

        var aaaa = await QueryAsync(dns.Port, "api.anthropic.com", TypeAaaa);
        aaaa.Rcode.Should().Be(0, "NOERROR, so resolvers do not treat the name as failing");
        aaaa.Answers.Should().BeEmpty("apps must not be handed IPv6 addresses the tunnel cannot carry");
    }

    [Fact]
    public async Task With_tunnel_ipv6_apps_get_both_families()
    {
        cores.RequireCores();
        await using var dns = await StartAsync(Ipv6Mode.Enable);

        (await QueryAsync(dns.Port, "claude.ai", TypeA)).Answers.Should().ContainSingle(x => x.Type == TypeA);
        var aaaa = await QueryAsync(dns.Port, "claude.ai", TypeAaaa);
        aaaa.Answers.Should().ContainSingle(x => x.Type == TypeAaaa);
        aaaa.Answers[0].Data.Should().StartWith("fc00", "fake-IPv6 range fc00::/18");
    }

    private async Task<DnsCore> StartAsync(Ipv6Mode ipv6)
    {
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.Tun,
            Profiles = [CoreFixture.ReferenceProfile()],
            Ipv6 = ipv6,
            Dns = new DnsSettings(),
        };
        var generated = cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!);
        var root = JsonNode.Parse(generated.Json)!.AsObject();

        // Same DNS and route policy; only the entry point changes from TUN to a loopback DNS listener.
        var port = NetHelpers.FreeUdpPort();
        root["inbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "dns-in", ["listen"] = "127.0.0.1", ["listen_port"] = port, ["network"] = "udp" });
        var rules = root["route"]!["rules"]!.AsArray();
        rules.Insert(0, new JsonObject { ["inbound"] = "dns-in", ["action"] = "hijack-dns" });
        root["route"]!.AsObject().Remove("auto_detect_interface");

        var core = await cores.StartRawAsync(CoreKind.SingBox, root.ToJsonString());
        await Task.Delay(800, Ct); // UDP listener has no connect-based readiness signal
        return new DnsCore(core, port);
    }

    private static async Task<DnsResponse> QueryAsync(int port, string name, ushort type)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = Query(id, name, type);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, port), Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                var response = Parse(result.Buffer);
                response.Id.Should().Be(id);
                return response;
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
            }
        }

        throw new TimeoutException("No DNS answer from the core.");
    }

    private sealed class DnsCore(JackiVpn.Cores.Hosting.CoreProcess core, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public ValueTask DisposeAsync() => core.DisposeAsync();
    }
}
