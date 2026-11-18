using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JackiVpn.Application.News;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Subscriptions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JackiVpn.UnitTests.News;

public sealed class BroadcastClientTests
{
    private const string Token = "TWFobmF6LDE3MzAwMDAwMDA8b2c3c2lnbmF0dXJl";

    /// <summary>A stand-in for the panel's news endpoint (the shape jacki_news.py answers with).</summary>
    private sealed class FakePanel : HttpMessageHandler
    {
        public List<(string Path, string Body, string? IfNoneMatch)> Seen { get; } = [];

        public List<Dictionary<string, object?>> Items { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool Offline { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var body = await request.Content!.ReadAsStringAsync(ct);
            var inm = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            Seen.Add((request.RequestUri!.AbsolutePath, body, inm));
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            using var doc = JsonDocument.Parse(body);
            if (request.RequestUri.AbsolutePath.EndsWith("/read", StringComparison.Ordinal))
            {
                foreach (var id in doc.RootElement.GetProperty("ids").EnumerateArray())
                {
                    Items.Where(i => (string)i["id"]! == id.GetString()).ToList().ForEach(i => i["read"] = true);
                }

                return Json(new { ok = true, unread = Items.Count(i => !(bool)i["read"]!) });
            }

            var json = JsonSerializer.Serialize(new { items = Items, unread = Items.Count(i => !(bool)i["read"]!) });
            var tag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16] + "\"";
            if (inm == tag)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(tag);
            return r;
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

        public void Add(string id, string text, string? title = null, long t = 1_763_400_000) =>
            Items.Add(new Dictionary<string, object?> { ["id"] = id, ["t"] = t, ["title"] = title ?? string.Empty, ["text"] = text, ["lvl"] = "info", ["read"] = false });
    }

    [Theory]
    [InlineData("https://sub.jackidrive.ir:2083/sub/" + Token, "https://sub.jackidrive.ir:2083/sub/_jp/api/news/")]
    [InlineData("https://cloud.jacki-inc.ir/sub/" + Token, "https://sub.jacki-inc.ir:2083/sub/_jp/api/news/")]
    public void Official_subscriptions_map_to_the_iran_entry_news_endpoint(string url, string endpoint)
    {
        var target = BroadcastClient.Target(url);

        target.Should().NotBeNull();
        target!.Value.Endpoint.ToString().Should().Be(endpoint);
        target.Value.Token.Should().Be(Token);
    }

    [Theory]
    [InlineData("https://example.com/sub/" + Token)]
    [InlineData("http://sub.jackidrive.ir:2083/sub/" + Token)]
    [InlineData("https://sub.jackidrive.ir:2083/sub/short")]
    [InlineData("https://sub.jackidrive.ir:2083/sub/_jp/api/news/list")]
    [InlineData(null)]
    public void Anything_else_gets_no_news(string? url) => BroadcastClient.Target(url).Should().BeNull();

    [Fact]
    public async Task List_sends_the_token_in_the_body_and_uses_the_etag()
    {
        var panel = new FakePanel();
        panel.Add("0a1b2c3d", "سرور هلند امشب به‌روز می‌شود", "اطلاعیه");
        var client = new BroadcastClient(new HttpClient(panel));
        var (endpoint, token) = BroadcastClient.Target("https://sub.jackidrive.ir:2083/sub/" + Token)!.Value;

        var first = await client.ListAsync(endpoint, token, null, TestContext.Current.CancellationToken);
        var again = await client.ListAsync(endpoint, token, first.ETag, TestContext.Current.CancellationToken);

        first.Items.Should().ContainSingle().Which.Should().Be(new BroadcastItem("0a1b2c3d", 1_763_400_000, "اطلاعیه", "سرور هلند امشب به‌روز می‌شود", "info", false));
        again.NotModified.Should().BeTrue();
        panel.Seen.Should().AllSatisfy(s => s.Path.Should().Be("/sub/_jp/api/news/list", "the token is never in a URL"));
        panel.Seen[0].Body.Should().Contain(Token);
        panel.Seen[1].IfNoneMatch.Should().Be(first.ETag);
    }

    [Fact]
    public async Task Unknown_subscription_is_reported_not_thrown()
    {
        var panel = new FakePanel { Status = HttpStatusCode.Unauthorized };
        var (endpoint, token) = BroadcastClient.Target("https://sub.jackidrive.ir:2083/sub/" + Token)!.Value;

        var list = await new BroadcastClient(new HttpClient(panel)).ListAsync(endpoint, token, null, TestContext.Current.CancellationToken);

        list.UnknownSubscription.Should().BeTrue();
        list.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Bad_items_from_the_server_are_dropped()
    {
        var panel = new FakePanel();
        panel.Add("not-an-id", "x");
        panel.Add("0a1b2c3d", "   ");
        panel.Add("ffffffff", "ok");
        var (endpoint, token) = BroadcastClient.Target("https://sub.jackidrive.ir:2083/sub/" + Token)!.Value;

        var list = await new BroadcastClient(new HttpClient(panel)).ListAsync(endpoint, token, null, TestContext.Current.CancellationToken);

        list.Items.Select(i => i.Id).Should().Equal("ffffffff");
    }

    private static (NewsService Service, FakePanel Panel) Service(params string[] urls)
    {
        var repo = Substitute.For<ISubscriptionRepository>();
        var subs = urls.Select((u, i) => new Subscription { Id = "s" + i, Name = "S" + i, MaskedUrl = "masked", Host = new Uri(u).Host }).ToList();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(subs);
        for (var i = 0; i < urls.Length; i++)
        {
            repo.GetSourceUrlAsync("s" + i, Arg.Any<CancellationToken>()).Returns(urls[i]);
        }

        var panel = new FakePanel();
        return (new NewsService(repo, new BroadcastClient(new HttpClient(panel)), NullLogger.Instance), panel);
    }

    [Fact]
    public async Task Service_shows_unread_newest_first_and_mark_read_clears_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (news, panel) = Service("https://sub.jackidrive.ir:2083/sub/" + Token, "https://example.com/sub/" + Token);
        panel.Add("00000001", "old", t: 100);
        panel.Add("00000002", "new", "Title", t: 200);
        var changes = 0;
        news.Changed += (_, _) => changes++;

        await news.RefreshAsync(ct);

        news.Unread.Select(n => n.Text).Should().Equal("new", "old");
        news.Unread.Should().AllSatisfy(n => n.Id.Should().NotContain(Token, "the subscription never reaches IBAR"));
        panel.Seen.Should().ContainSingle("only the official subscription is asked");
        changes.Should().Be(1);

        await news.ReadAsync([news.Unread[0].Id], ct);

        news.Unread.Select(n => n.Text).Should().Equal("old");
        panel.Items.Single(i => (string)i["id"]! == "00000002")["read"].Should().Be(true);
        await news.RefreshAsync(ct);
        news.Unread.Select(n => n.Text).Should().Equal("old");
    }

    [Fact]
    public async Task Same_subscription_twice_is_asked_once_and_offline_keeps_what_was_there()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = "https://sub.jackidrive.ir:2083/sub/" + Token;
        var (news, panel) = Service(url, url);
        panel.Add("00000001", "hello");

        await news.RefreshAsync(ct);
        panel.Offline = true;
        await news.RefreshAsync(ct);

        panel.Seen.Should().ContainSingle();
        news.Unread.Should().ContainSingle().Which.Text.Should().Be("hello");
    }
}
