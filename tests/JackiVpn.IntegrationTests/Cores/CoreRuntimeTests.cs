using System.Diagnostics;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.IntegrationTests.Infrastructure;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>
/// Real traffic through real cores on loopback: a sing-box VLESS+WS+TLS server (self-signed cert) and a
/// WireMock HTTP target. Covers start → inbound ready → HTTP/SOCKS request through the proxy → verify →
/// stop → port released, the Xray chain, and crash detection.
/// </summary>
public sealed class CoreRuntimeTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private const string Sni = "edge.jacki-example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(CoreProcess Server, int Port, TestCertificate Cert)> StartServerAsync()
    {
        var cert = new TestCertificate(cores.WorkDir, Sni);
        var port = NetHelpers.FreePort();
        var server = await cores.StartRawAsync(CoreKind.SingBox, NetHelpers.VlessWsTlsServerConfig(port, Uuid, Sni, "/jacki-ws", cert));
        await server.WaitForPortAsync(port, TimeSpan.FromSeconds(15), Ct);
        return (server, port, cert);
    }

    private static Domain.Profiles.ProxyProfile ProfileFor(int serverPort)
    {
        var p = CoreFixture.ReferenceProfile();
        // Same shape as the owner's config; only the endpoint and trust differ for the loopback test server.
        return p with { Server = "127.0.0.1", Port = serverPort, Tls = p.Tls with { AllowInsecure = true } };
    }

    [Fact]
    public async Task SingBox_carries_http_and_socks_through_vless_ws_tls_utls_fragment_and_releases_port()
    {
        cores.RequireCores();
        using var target = NetHelpers.StartTarget();
        var (server, serverPort, cert) = await StartServerAsync();
        using var _ = cert;
        await using var __ = server;

        var mixed = NetHelpers.FreePort();
        var api = new ClashApiOptions(NetHelpers.FreePort(), PortAllocator.NewApiSecret());
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            Profiles = [ProfileFor(serverPort)],
            ListenPort = mixed,
            Routing = new RoutingSettings { BypassLan = false },
            ClashApi = api,
        };
        var config = cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!);
        config.Json.Should().Contain("\"fingerprint\": \"firefox\"").And.Contain("\"fragment\": true").And.Contain("\"fragment_fallback_delay\": \"20ms\"");

        var validation = await cores.SingBox.ValidateAsync(config, Ct);
        validation.IsValid.Should().BeTrue(validation.RedactedOutput);

        var client = await cores.StartAsync(cores.SingBox, config);
        try
        {
            await client.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);
            var url = $"http://127.0.0.1:{NetHelpers.PortOf(target)}/probe";

            using (var http = NetHelpers.ClientVia($"http://127.0.0.1:{mixed}"))
            {
                (await http.GetStringAsync(url, Ct)).Should().Be("hello-jacki");
            }

            using (var socks = NetHelpers.ClientVia($"socks5://127.0.0.1:{mixed}"))
            {
                (await socks.GetStringAsync(url, Ct)).Should().Be("hello-jacki");
            }

            server.RecentOutput.Should().Contain(l => l.Contains($"127.0.0.1:{NetHelpers.PortOf(target)}", StringComparison.Ordinal),
                "the request must have traversed the VLESS server, not gone direct");

            using var clash = new ClashApiClient(api);
            (await clash.GetVersionAsync(Ct)).Should().NotBeNullOrEmpty();
            (await clash.GetTotalsAsync(Ct))!.DownloadTotal.Should().BePositive();
        }
        finally
        {
            await client.StopAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync();
        }

        client.HasExited.Should().BeTrue();
        (await NetHelpers.WaitPortReleasedAsync(mixed, TimeSpan.FromSeconds(5))).Should().BeTrue("the inbound port must be released after stop");
        IsRunning(client.ProcessId).Should().BeFalse("no zombie core process may remain");
    }

    [Fact]
    public async Task Xray_backend_behind_singbox_frontend_carries_traffic()
    {
        cores.RequireCores();
        using var target = NetHelpers.StartTarget("via-xray");
        var (server, serverPort, cert) = await StartServerAsync();
        using var _ = cert;
        await using var __ = server;

        // Xray 26 has no allowInsecure: trust the self-signed test server by pinning its certificate.
        var baseProfile = ProfileFor(serverPort);
        var profile = baseProfile with { Tls = baseProfile.Tls with { AllowInsecure = false, CertificateSha256Pins = [cert.Sha256Hex] } };
        var backendPort = NetHelpers.FreePort();
        var backendConfig = cores.Xray.GenerateConfig(
            new CoreSessionPlan { Kind = SessionKind.ChainBackend, Profiles = [profile], ListenPort = backendPort }, cores.XrayCaps!);
        backendConfig.Json.Should().Contain("dialerProxy").And.Contain("\"fingerprint\": \"firefox\"").And.Contain("pinnedPeerCertSha256").And.NotContain("allowInsecure");
        var xv = await cores.Xray.ValidateAsync(backendConfig, Ct);
        xv.IsValid.Should().BeTrue(xv.RedactedOutput);

        await using var backend = await cores.StartAsync(cores.Xray, backendConfig);
        await backend.WaitForPortAsync(backendPort, TimeSpan.FromSeconds(15), Ct);

        var mixed = NetHelpers.FreePort();
        var frontConfig = cores.SingBox.GenerateConfig(new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            Profiles = [profile],
            ListenPort = mixed,
            Routing = new RoutingSettings { BypassLan = false },
            ChainBackend = new ChainBackendEndpoint(backendPort, cores.Catalog.PathOf(CoreKind.Xray)),
        }, cores.SingBoxCaps!);
        (await cores.SingBox.ValidateAsync(frontConfig, Ct)).IsValid.Should().BeTrue();

        await using var front = await cores.StartAsync(cores.SingBox, frontConfig);
        await front.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);
        using var http = NetHelpers.ClientVia($"http://127.0.0.1:{mixed}");

        (await http.GetStringAsync($"http://127.0.0.1:{NetHelpers.PortOf(target)}/probe", Ct)).Should().Be("via-xray");
        server.RecentOutput.Should().Contain(l => l.Contains($"127.0.0.1:{NetHelpers.PortOf(target)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Crash_of_core_is_detected_immediately()
    {
        cores.RequireCores();
        var mixed = NetHelpers.FreePort();
        var config = cores.SingBox.GenerateConfig(new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            Profiles = [ProfileFor(NetHelpers.FreePort())],
            ListenPort = mixed,
        }, cores.SingBoxCaps!);
        await using var core = await cores.StartAsync(cores.SingBox, config);
        await core.WaitForPortAsync(mixed, TimeSpan.FromSeconds(15), Ct);

        using (var p = Process.GetProcessById(core.ProcessId))
        {
            p.Kill();
        }

        var exited = await Task.WhenAny(core.Exited, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        exited.Should().BeSameAs(core.Exited, "a killed core must be detected");
        core.StopRequested.Should().BeFalse("this exit was a crash, not a requested stop");
    }

    [Fact]
    public async Task Startup_failure_is_reported_with_core_output_not_hung()
    {
        cores.RequireCores();
        var port = NetHelpers.FreePort();
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        blocker.Start();
        var config = cores.SingBox.GenerateConfig(new CoreSessionPlan
        {
            Kind = SessionKind.SystemProxy,
            Profiles = [ProfileFor(NetHelpers.FreePort())],
            ListenPort = port,
        }, cores.SingBoxCaps!);

        await using var core = await cores.StartAsync(cores.SingBox, config);
        await core.Exited.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        core.LastError().Should().NotBeNullOrEmpty();
        core.RecentOutput.Should().NotContain(l => l.Contains(Uuid, StringComparison.Ordinal), "core output is redacted");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
