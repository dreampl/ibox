using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using JackiVpn.Application.Connection;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.IntegrationTests.Infrastructure;
using JackiVpn.Networking.Probes;
using static JackiVpn.IntegrationTests.Infrastructure.DnsWire;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>
/// The Android core configuration (mixed inbound fed by an external TUN, with per-session SOCKS credentials),
/// run by the real sing-box on loopback. On a phone every app can reach 127.0.0.1, so the inbound must refuse
/// anyone without the credentials, while tun2socks (SOCKS5 TCP + UDP ASSOCIATE) and the app's own probes work.
/// </summary>
public sealed class AndroidFrontendTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private const string Sni = "edge.jacki-example.test";
    private static readonly InboundCredentials Auth = new("jacki-" + PortAllocator.NewApiSecret()[..12], PortAllocator.NewApiSecret());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CoreSessionPlan AndroidPlan(int listenPort, int serverPort)
    {
        var p = CoreFixture.ReferenceProfile();
        return new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            ExternalTun = true,
            InboundAuth = Auth,
            Profiles = [p with { Server = "127.0.0.1", Port = serverPort, Tls = p.Tls with { AllowInsecure = true } }],
            ListenPort = listenPort,
            Dns = new DnsSettings(),
            Routing = new RoutingSettings { BypassLan = false },
        };
    }

    [Fact]
    public async Task Inbound_refuses_other_apps_and_serves_the_authenticated_tunnel()
    {
        cores.RequireCores();
        using var target = NetHelpers.StartTarget("android-ok");
        using var cert = new TestCertificate(cores.WorkDir, Sni);
        var serverPort = NetHelpers.FreePort();
        await using var server = await cores.StartRawAsync(CoreKind.SingBox, NetHelpers.VlessWsTlsServerConfig(serverPort, Uuid, Sni, "/jacki-ws", cert));
        await server.WaitForPortAsync(serverPort, TimeSpan.FromSeconds(15), Ct);

        var mixed = NetHelpers.FreePort();
        var config = cores.SingBox.GenerateConfig(AndroidPlan(mixed, serverPort), cores.SingBoxCaps!);
        config.Secrets.Should().Contain(Auth.Password, "the SOCKS password is redacted from core output like any credential");
        var validation = await cores.SingBox.ValidateAsync(config, Ct);
        validation.IsValid.Should().BeTrue(validation.RedactedOutput);

        await using var core = await cores.StartAsync(cores.SingBox, config);
        await core.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);
        var url = new Uri($"http://127.0.0.1:{NetHelpers.PortOf(target)}/probe");

        // Another app without the credentials: HTTP proxy and SOCKS5 are both refused.
        using (var anonymous = NetHelpers.ClientVia($"http://127.0.0.1:{mixed}"))
        {
            (await IsServedAsync(anonymous, url)).Should().BeFalse("an HTTP proxy client without credentials must not get through");
        }

        using (var anonymousSocks = NetHelpers.ClientVia($"socks5://127.0.0.1:{mixed}"))
        {
            (await IsServedAsync(anonymousSocks, url)).Should().BeFalse("a SOCKS5 client without credentials must not get through");
        }

        (await SocksGreetingAsync(mixed, offerAuth: false)).Should().Be(0xFF, "SOCKS5 'no acceptable methods' for clients without credentials");

        // The app's own clients (live ping, exit lookup, speed test) authenticate through the registry.
        (await ProxyHttpProbe.RunAsync(new Uri($"http://127.0.0.1:{mixed}"), url, TimeSpan.FromSeconds(10), Ct)).Success.Should().BeFalse("not registered yet");
        LoopbackProxyCredentials.Set(mixed, Auth.Username, Auth.Password);
        try
        {
            var probe = await ProxyHttpProbe.RunAsync(new Uri($"http://127.0.0.1:{mixed}"), url, TimeSpan.FromSeconds(10), Ct);
            probe.Success.Should().BeTrue(probe.Detail);
            using var http = ConnectionHttp.Create(ConnectionMode.Tun, mixed, TimeSpan.FromSeconds(10));
            (await http.GetStringAsync(url, Ct)).Should().Be("android-ok");
        }
        finally
        {
            LoopbackProxyCredentials.Clear(mixed);
        }

        server.RecentOutput.Should().Contain(l => l.Contains($"127.0.0.1:{NetHelpers.PortOf(target)}", StringComparison.Ordinal),
            "requests must have traversed the VLESS server");
    }

    [Fact]
    public async Task Dns_through_authenticated_socks5_udp_associate_gets_tunnel_answers()
    {
        cores.RequireCores();
        var mixed = NetHelpers.FreePort();
        var config = cores.SingBox.GenerateConfig(AndroidPlan(mixed, NetHelpers.FreePort()), cores.SingBoxCaps!);
        await using var core = await cores.StartAsync(cores.SingBox, config);
        await core.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);

        // Exactly what tun2socks does for a DNS packet to the VPN's resolver address (172.19.0.2:53).
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, mixed, Ct);
        var stream = control.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, Ct);
        (await ReadExactAsync(stream, 2)).Should().Equal(5, 2);
        var user = Encoding.ASCII.GetBytes(Auth.Username);
        var pass = Encoding.ASCII.GetBytes(Auth.Password);
        await stream.WriteAsync((byte[])[1, (byte)user.Length, .. user, (byte)pass.Length, .. pass], Ct);
        (await ReadExactAsync(stream, 2)).Should().Equal([1, 0], "credentials accepted");

        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, Ct); // UDP ASSOCIATE 0.0.0.0:0
        var reply = await ReadExactAsync(stream, 4);
        reply[1].Should().Be(0, "UDP ASSOCIATE succeeded");
        var relayIp = reply[3] == 1 ? new IPAddress(await ReadExactAsync(stream, 4)) : new IPAddress(await ReadExactAsync(stream, 16));
        var relayPort = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(stream, 2));
        var relay = new IPEndPoint(relayIp.Equals(IPAddress.Any) ? IPAddress.Loopback : relayIp, relayPort);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        byte[] packet = [0, 0, 0, 1, 172, 19, 0, 2, 0, 53, .. Query(id, "api.anthropic.com", TypeA)];
        DnsResponse? answer = null;
        for (var attempt = 0; attempt < 5 && answer is null; attempt++)
        {
            await udp.SendAsync(packet, relay, Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                var headerLength = r.Buffer[3] switch { 1 => 10, 4 => 22, _ => 7 + r.Buffer[4] };
                answer = Parse(r.Buffer.AsSpan(headerLength));
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
            }
        }

        answer.Should().NotBeNull("DNS over SOCKS5 UDP must be answered — on Android every app's DNS takes this path");
        answer!.Id.Should().Be(id);
        answer.Answers.Should().ContainSingle(x => x.Type == TypeA);
        IPAddress.Parse(answer.Answers[0].Data).GetAddressBytes()[0].Should().Be(198, "port-53 hijack + fake-IP apply to the external TUN as to our own");
    }

    private static async Task<bool> IsServedAsync(HttpClient client, Uri url)
    {
        try
        {
            using var response = await client.GetAsync(url, Ct);
            return response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync(Ct) == "android-ok";
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<byte> SocksGreetingAsync(int port, bool offerAuth)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        var s = tcp.GetStream();
        await s.WriteAsync(new byte[] { 5, 1, offerAuth ? (byte)2 : (byte)0 }, Ct);
        var reply = await ReadExactAsync(s, 2);
        return reply[1];
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream s, int count)
    {
        var buffer = new byte[count];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await s.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }
}
