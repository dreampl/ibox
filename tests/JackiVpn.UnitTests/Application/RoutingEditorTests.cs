using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JackiVpn.Application.Routing;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Application;

/// <summary>The node-based routing editor's loopback server: only its own page, with its token, can read or write.</summary>
public class RoutingEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RoutingRule Rule(string id, RuleAction action, params RuleCondition[] c) => new() { Id = id, Name = id, Action = action, Conditions = c };

    private static (RoutingEditorServer Server, SettingsStore Settings, HttpClient Http, string Url) Start()
    {
        var settings = new SettingsStore(new MemorySettings(), NullLogger.Instance);
        settings.Update(s => s with
        {
            Routing = s.Routing with { Rules = [Rule("ir", RuleAction.Direct, new RuleCondition(RuleConditionType.DomainSuffix, ["ir"]))] },
        });
        var server = new RoutingEditorServer(settings, NullLogger.Instance);
        var url = server.Open(AppTheme.Dark);
        var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url.Split('?')[0]) };
        return (server, settings, http, url);
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, object body, string? origin = null)
    {
        var m = new HttpRequestMessage(method, path) { Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Storage), Encoding.UTF8, "application/json") };
        if (origin is not null)
        {
            m.Headers.Add("Origin", origin);
        }

        return m;
    }

    [Fact]
    public async Task Serves_the_page_and_the_rules_only_with_the_token()
    {
        var (server, _, http, url) = Start();
        await using var _ = server;
        using var __ = http;
        url.Should().StartWith("http://127.0.0.1:").And.EndWith("?theme=dark");

        var page = await http.GetStringAsync(string.Empty, Ct);
        page.Should().Contain("Routing editor");
        var graph = await http.GetFromJsonAsync<RoutingGraphDocument>("api/graph", JsonDefaults.Storage, Ct);
        graph!.Rules.Should().ContainSingle(r => r.Id == "ir");
        graph.Version.Should().NotBeNullOrEmpty();

        using var bare = new HttpClient(new HttpClientHandler { UseProxy = false });
        (await bare.GetAsync($"http://127.0.0.1:{server.Port}/api/graph", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bare.GetAsync($"http://127.0.0.1:{server.Port}/{new string('0', 64)}/api/graph", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Other_sites_cannot_reach_it()
    {
        var (server, _, http, _) = Start();
        await using var _ = server;
        using var __ = http;

        using var rebind = new HttpRequestMessage(HttpMethod.Get, "api/graph");
        rebind.Headers.Host = "evil.example:" + server.Port;
        (await http.SendAsync(rebind, Ct)).StatusCode.Should().Be(HttpStatusCode.MisdirectedRequest);

        var doc = await http.GetFromJsonAsync<RoutingGraphDocument>("api/graph", JsonDefaults.Storage, Ct);
        using var cross = Json(HttpMethod.Put, "api/graph", doc!, origin: "https://evil.example");
        (await http.SendAsync(cross, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var form = new HttpRequestMessage(HttpMethod.Put, "api/graph") { Content = new StringContent("rules=1", Encoding.UTF8, "application/x-www-form-urlencoded") };
        (await http.SendAsync(form, Ct)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Saves_valid_rules_and_points_problems_at_their_rule()
    {
        var (server, settings, http, _) = Start();
        await using var _ = server;
        using var __ = http;
        var doc = (await http.GetFromJsonAsync<RoutingGraphDocument>("api/graph", JsonDefaults.Storage, Ct))!;

        var bad = doc with { Rules = [Rule("x", RuleAction.Block, new RuleCondition(RuleConditionType.IpCidr, ["not-an-ip"]))] };
        using (var r = await http.SendAsync(Json(HttpMethod.Put, "api/graph", bad), Ct))
        {
            r.StatusCode.Should().Be((HttpStatusCode)422);
            var result = await r.Content.ReadFromJsonAsync<RoutingSaveResult>(JsonDefaults.Storage, Ct);
            result!.Problems.Should().Contain(p => p.RuleId == "x");
        }

        var good = doc with
        {
            Rules = [Rule("tg", RuleAction.Proxy, new RuleCondition(RuleConditionType.ProcessName, ["telegram.exe"])), .. doc.Rules],
            Final = RuleActionType.Direct,
            Layout = new Dictionary<string, NodePoint> { ["r:tg"] = new(420, 30) },
        };
        using (var r = await http.SendAsync(Json(HttpMethod.Put, "api/graph", good, origin: $"http://127.0.0.1:{server.Port}"), Ct))
        {
            r.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        settings.Current.Routing.Rules.Select(r => r.Id).Should().Equal("tg", "ir");
        settings.Current.Routing.Final.Should().Be(RuleActionType.Direct);
        settings.Current.Routing.Preset.Should().Be(RoutingPreset.Custom);
        settings.Current.Ui.RoutingLayout["r:tg"].Should().Be(new NodePoint(420, 30));

        // The page loaded before the save is now stale: its next save must not overwrite.
        using (var r = await http.SendAsync(Json(HttpMethod.Put, "api/graph", doc), Ct))
        {
            r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        settings.Current.Routing.Rules.Should().HaveCount(2);
    }

    [Fact]
    public async Task Moving_nodes_alone_does_not_touch_the_rules()
    {
        var (server, settings, http, _) = Start();
        await using var _ = server;
        using var __ = http;
        var doc = (await http.GetFromJsonAsync<RoutingGraphDocument>("api/graph", JsonDefaults.Storage, Ct))!;
        var routing = settings.Current.Routing;

        using var r = await http.SendAsync(Json(HttpMethod.Put, "api/graph", doc with { Layout = new Dictionary<string, NodePoint> { ["final"] = new(1, 2) } }), Ct);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        settings.Current.Routing.Should().BeSameAs(routing, "a layout-only save must not reconnect");
        settings.Current.Ui.RoutingLayout.Should().ContainKey("final");
    }

    [Fact]
    public async Task Tests_a_destination_against_the_saved_rules()
    {
        var (server, _, http, _) = Start();
        await using var _ = server;
        using var __ = http;

        using var hit = await http.SendAsync(Json(HttpMethod.Post, "api/test", new RoutingTestRequest("www.digikala.ir", null, 443, "tcp", null)), Ct);
        var result = await hit.Content.ReadFromJsonAsync<RoutingTestResult>(JsonDefaults.Storage, Ct);
        result!.RuleId.Should().Be("ir");
        result.Action.Should().Be("Direct");

        using var miss = await http.SendAsync(Json(HttpMethod.Post, "api/test", new RoutingTestRequest("example.com", null, null, null, null)), Ct);
        (await miss.Content.ReadFromJsonAsync<RoutingTestResult>(JsonDefaults.Storage, Ct))!.RuleId.Should().BeNull();
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        private AppSettings _s = new();

        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(_s);

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            _s = settings;
            return Task.CompletedTask;
        }
    }
}
