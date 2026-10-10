using System.Text;
using JackiVpn.IntegrationTests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace JackiVpn.IntegrationTests.Subscriptions;

/// <summary>A WireMock-based PasarGuard panel (routes mirror app/routers/subscription.py).</summary>
public sealed class FakePasarGuard : IDisposable
{
    public const string Token = "AbCdEfGh12345678ZzYy";
    public const string Uuid = "11111111-2222-4333-8444-555555555555";

    public FakePasarGuard()
    {
        var port = NetHelpers.FreePort();
        Server = WireMockServer.Start(new WireMockServerSettings { Urls = [$"http://127.0.0.1:{port}"] });
        BaseUrl = $"http://127.0.0.1:{port}/sub/{Token}";
    }

    public WireMockServer Server { get; }

    public string BaseUrl { get; }

    public string Path(string endpoint = "") => $"/sub/{Token}" + (endpoint.Length == 0 ? string.Empty : "/" + endpoint);

    public static string Links(params string[] names) => string.Join('\n', names.Select((n, i) =>
        $"vless://{Uuid}@n{i}.example.com:443?security=tls&type=ws&host=n{i}.example.com&path=%2Fws&sni=n{i}.example.com&fp=firefox#{Uri.EscapeDataString(n)}"));

    public static string SingBoxJson(params string[] names) =>
        "{\"outbounds\":[" + string.Join(',', names.Select((n, i) =>
            $"{{\"type\":\"vless\",\"tag\":\"{n}\",\"server\":\"104.16.0.{i + 1}\",\"server_port\":443,\"uuid\":\"{Uuid}\",\"transport\":{{\"type\":\"ws\",\"path\":\"/jacki-ws\",\"headers\":{{\"host\":\"edge.jacki-example.test\"}}}},\"tls\":{{\"enabled\":true,\"server_name\":\"edge.jacki-example.test\",\"utls\":{{\"enabled\":true,\"fingerprint\":\"firefox\"}},\"alpn\":[\"http/1.1\"],\"fragment\":true,\"fragment_fallback_delay\":\"20ms\"}}}}"))
        + ",{\"type\":\"direct\",\"tag\":\"direct\"}]}";

    public static string XrayJson(params string[] names) =>
        "[" + string.Join(',', names.Select((n, i) =>
            $"{{\"remarks\":\"{n}\",\"outbounds\":[{{\"tag\":\"proxy\",\"protocol\":\"trojan\",\"settings\":{{\"servers\":[{{\"address\":\"t{i}.example.com\",\"port\":443,\"password\":\"pw{i}\"}}]}},\"streamSettings\":{{\"network\":\"grpc\",\"security\":\"tls\",\"grpcSettings\":{{\"serviceName\":\"g\"}},\"tlsSettings\":{{\"serverName\":\"t{i}.example.com\"}}}}}}]}}")) + "]";

    public FakePasarGuard Head(int status = 200, string? userinfo = "upload=0; download=1073741824; total=10737418240; expire=1893456000", string? title = "Jacki Panel", int? intervalHours = 12, IDictionary<string, string>? extra = null)
    {
        var r = Response.Create().WithStatusCode(status);
        if (userinfo is not null)
        {
            r = r.WithHeader("subscription-userinfo", userinfo);
        }

        if (title is not null)
        {
            r = r.WithHeader("profile-title", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(title)));
        }

        if (intervalHours is { } h)
        {
            r = r.WithHeader("profile-update-interval", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        r = r.WithHeader("profile-web-page-url", BaseUrl);
        foreach (var (k, v) in extra ?? new Dictionary<string, string>())
        {
            r = r.WithHeader(k, v);
        }

        Server.Given(Request.Create().WithPath(Path()).UsingHead()).RespondWith(r);
        return this;
    }

    public FakePasarGuard BaseGetHtml()
    {
        Server.Given(Request.Create().WithPath(Path()).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/html").WithBody("<!DOCTYPE html><html><body>Your subscription</body></html>"));
        return this;
    }

    public FakePasarGuard Endpoint(string endpoint, string body, int status = 200, string contentType = "text/plain", string? etag = null, TimeSpan? delay = null)
    {
        var r = Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body);
        if (etag is not null)
        {
            r = r.WithHeader("ETag", etag);
        }

        if (delay is { } d)
        {
            r = r.WithDelay(d);
        }

        Server.Given(Request.Create().WithPath(Path(endpoint)).UsingGet()).AtPriority(10).RespondWith(r);
        return this;
    }

    public FakePasarGuard NotModifiedWhen(string endpoint, string etag)
    {
        Server.Given(Request.Create().WithPath(Path(endpoint)).UsingGet().WithHeader("If-None-Match", etag))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", etag));
        return this;
    }

    public FakePasarGuard Info(string status, long used, long? limit, string? expireIso)
    {
        var limitJson = limit is null ? "null" : limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expireJson = expireIso is null ? "null" : $"\"{expireIso}\"";
        Server.Given(Request.Create().WithPath(Path("info")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"id\":1,\"username\":\"jacki\",\"status\":\"{status}\",\"used_traffic\":{used},\"lifetime_used_traffic\":{used},\"data_limit\":{limitJson},\"expire\":{expireJson},\"created_at\":\"2026-01-01T00:00:00Z\",\"proxy_settings\":{{\"vless\":{{\"id\":\"{Uuid}\"}}}}}}"));
        return this;
    }

    public FakePasarGuard Status(string endpoint, int status, TimeSpan? retryAfter = null)
    {
        var r = Response.Create().WithStatusCode(status).WithBody("error");
        if (retryAfter is { } ra)
        {
            r = r.WithHeader("Retry-After", ((int)ra.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var req = Request.Create().WithPath(Path(endpoint));
        Server.Given(endpoint.Length == 0 ? req.UsingAnyMethod() : req.UsingGet()).AtPriority(5).RespondWith(r);
        return this;
    }

    /// <summary>PasarGuard ≥ 5.1 refusing a device: 403 with a JSON detail ("Device limit reached" / "HWID header required").</summary>
    public FakePasarGuard RefuseDevice(string detail)
    {
        Server.Given(Request.Create().WithPath(Path()).UsingAnyMethod()).AtPriority(3)
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json").WithBody($"{{\"detail\":\"{detail}\"}}"));
        return this;
    }

    public IEnumerable<IDictionary<string, string>> RequestHeaders() =>
        Server.LogEntries.Select(e => (IDictionary<string, string>)(e.RequestMessage?.Headers ?? new Dictionary<string, WireMock.Types.WireMockList<string>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));

    public FakePasarGuard Redirect(string fromEndpoint, string location)
    {
        Server.Given(Request.Create().WithPath(Path(fromEndpoint)).UsingGet()).AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", location));
        return this;
    }

    public void Dispose() => Server.Stop();
}
