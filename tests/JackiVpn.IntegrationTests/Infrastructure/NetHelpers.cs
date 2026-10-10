using System.Net;
using System.Net.Sockets;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace JackiVpn.IntegrationTests.Infrastructure;

/// <summary>An <see cref="IWebProxy"/> that never bypasses (WebProxy always bypasses loopback targets).</summary>
public sealed class AlwaysProxy(Uri proxy) : IWebProxy
{
    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination) => proxy;

    public bool IsBypassed(Uri host) => false;
}

public static class NetHelpers
{
    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public static HttpClient ClientVia(string proxyUri, TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler { Proxy = new AlwaysProxy(new Uri(proxyUri)), UseProxy = true, PooledConnectionLifetime = TimeSpan.Zero })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };

    /// <summary>Loopback HTTP target that answers /probe with a known body.</summary>
    public static WireMockServer StartTarget(string body = "hello-jacki")
    {
        var port = FreePort();
        var server = WireMockServer.Start(new WireMockServerSettings { Urls = [$"http://127.0.0.1:{port}"] });
        server.Given(Request.Create().WithPath("/probe").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody(body));
        server.Given(Request.Create().WithPath("/generate_204").UsingGet()).RespondWith(Response.Create().WithStatusCode(204));
        return server;
    }

    public static int PortOf(WireMockServer server) => server.Ports[0];

    public static async Task<bool> WaitPortReleasedAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        return false;
    }

    public static string VlessWsTlsServerConfig(int port, string uuid, string sni, string path, TestCertificate cert, bool earlyData = false) => $$"""
    {
      "log": {"level": "info", "timestamp": false},
      "inbounds": [{
        "type": "vless", "tag": "vless-in", "listen": "127.0.0.1", "listen_port": {{port}},
        "users": [{"uuid": "{{uuid}}"}],
        "tls": {"enabled": true, "server_name": "{{sni}}", "alpn": ["http/1.1"],
                "certificate_path": {{System.Text.Json.JsonSerializer.Serialize(cert.CertPath)}},
                "key_path": {{System.Text.Json.JsonSerializer.Serialize(cert.KeyPath)}}},
        "transport": {"type": "ws", "path": "{{path}}"{{(earlyData ? ", \"max_early_data\": 2048, \"early_data_header_name\": \"Sec-WebSocket-Protocol\"" : string.Empty)}}}
      }],
      "outbounds": [{"type": "direct", "tag": "direct"}]
    }
    """;
}
