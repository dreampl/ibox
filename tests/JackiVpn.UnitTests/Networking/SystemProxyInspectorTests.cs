using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using JackiVpn.Networking.SystemProxy;

namespace JackiVpn.UnitTests.Networking;

public class SystemProxyInspectorTests
{
    private sealed class FakeApi(ProxySnapshot lan) : IWinInetProxyApi
    {
        public ProxySnapshot Lan { get; private set; } = lan;
        public int Notified { get; private set; }

        public ProxySnapshot Query(string? connectionName) => Lan;

        public void Apply(ProxySnapshot snapshot) => Lan = snapshot;

        public void NotifyChanged() => Notified++;

        public IReadOnlyList<string> EnumerateRasConnections() => [];
    }

    private static ProxySnapshot Manual(string server) => new() { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagProxy, ProxyServer = server, ProxyBypass = "<local>" };

    [Theory]
    [InlineData("127.0.0.1:3067", 3067)]
    [InlineData("localhost:7890", 7890)]
    [InlineData("http=127.0.0.1:10809;https=127.0.0.1:10809", 10809)]
    [InlineData("http://127.0.0.1:2080", 2080)]
    [InlineData("[::1]:1080", 1080)]
    public void Loopback_proxy_addresses_are_recognised(string server, int port)
    {
        SystemProxyInspector.TryParseLoopback(server, out var p).Should().BeTrue();
        p.Should().Be(port);
    }

    [Theory]
    [InlineData("proxy.corp.example:8080")]
    [InlineData("10.0.0.1:3128")]
    [InlineData("127.0.0.1")]
    public void Non_loopback_or_portless_proxies_are_not_judged(string server) =>
        SystemProxyInspector.TryParseLoopback(server, out _).Should().BeFalse();

    [Fact]
    public async Task Dead_loopback_proxy_is_detected_and_live_one_is_not_called_dead()
    {
        var dead = PortThatNobodyListensOn();
        (await SystemProxyInspector.InspectAsync(new FakeApi(Manual($"127.0.0.1:{dead}")), null, CancellationToken.None))
            .Health.Should().Be(SystemProxyHealth.ForeignDead);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var live = ((IPEndPoint)listener.LocalEndpoint).Port;
        (await SystemProxyInspector.InspectAsync(new FakeApi(Manual($"127.0.0.1:{live}")), null, CancellationToken.None))
            .Health.Should().Be(SystemProxyHealth.ForeignLive);
        (await SystemProxyInspector.InspectAsync(new FakeApi(Manual($"127.0.0.1:{live}")), live, CancellationToken.None))
            .Health.Should().Be(SystemProxyHealth.Ours);
    }

    [Fact]
    public async Task No_manual_proxy_means_nothing_to_report()
    {
        (await SystemProxyInspector.InspectAsync(new FakeApi(new ProxySnapshot()), null, CancellationToken.None)).Health.Should().Be(SystemProxyHealth.None);
        (await SystemProxyInspector.InspectAsync(new FakeApi(new ProxySnapshot { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagAutoProxyUrl, AutoConfigUrl = "http://wpad/x.pac" }), null, CancellationToken.None))
            .Health.Should().Be(SystemProxyHealth.None);
    }

    [Fact]
    public void Turning_off_keeps_pac_and_bypass_and_notifies_apps()
    {
        var api = new FakeApi(Manual("127.0.0.1:3067") with { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagProxy | ProxySnapshot.FlagAutoProxyUrl, AutoConfigUrl = "http://wpad/x.pac" });
        SystemProxyInspector.DisableManualProxy(api);

        api.Lan.ProxyEnabled.Should().BeFalse();
        api.Lan.PacEnabled.Should().BeTrue();
        api.Lan.ProxyBypass.Should().Be("<local>");
        api.Notified.Should().Be(1);
    }

    private static int PortThatNobodyListensOn()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
