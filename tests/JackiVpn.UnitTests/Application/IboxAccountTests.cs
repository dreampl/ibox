#pragma warning disable xUnit1051 // short in-memory fakes; nothing to cancel
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Application.Account;
using JackiVpn.Security;
using Xunit;

namespace JackiVpn.UnitTests.Application;

public sealed class IboxAccountTests
{
    /// <summary>Answers by path; records what the app sent.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Path, JsonObject Body, string? Auth, string? Inm)> Seen { get; } = [];

        public Func<string, JsonObject, HttpResponseMessage> Answer { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject ?? [];
            var path = request.RequestUri!.PathAndQuery;
            Seen.Add((path, body, request.Headers.Authorization?.Parameter, request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null));
            return Answer(path, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json, string? etag = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return r;
    }

    private static (IboxAccount Account, FakeServer Server, MemoryTokenStore Tokens) Panel()
    {
        var server = new FakeServer();
        var tokens = new MemoryTokenStore();
        return (new IboxAccount(new IboxHttp(new HttpClient(server), IboxEndpoints.Panel), tokens), server, tokens);
    }

    private const string App = """
        {"user":{"name":"Ali","username":"ali"},"bot":"Jacki_core_bot","subs":[{"lid":"L1","url":"https://sub.example/sub/_jp/nx/abc",
         "data":{"username":"ali_nl","status":"active","used_traffic":1073741824,"data_limit":10737418240,"expire":2000000000,"label":"هلند","multi":true},
         "opts":{"add_gb":[30,50,100],"to_unlim":[30],"switch":["us","de"]},
         "reqs":[{"id":"r1","kind":"extend","value":30,"status":"pending","stage":"accept","price":250000}],
         "nex":{"on":true,"member":true,"exit":"de","price":0,"exits":[{"b":"nl","label":"🇳🇱 هلند"},{"b":"de","label":"🇩🇪 آلمان"}]}}]}
        """;

    [Fact]
    public async Task Telegram_login_sends_only_the_verifier_hash_then_trades_the_verifier_for_a_token()
    {
        var (account, server, tokens) = Panel();
        var polls = 0;
        server.Answer = (path, body) => path switch
        {
            "/sub/_jp/api/ibox/login/start" => Json(HttpStatusCode.OK, """{"nonce":"N123456789012345678901","code":"0420","ttl":600,"url":"https://t.me/Jacki_core_bot?start=ibx_N1","tg":"tg://resolve?domain=Jacki_core_bot"}"""),
            "/sub/_jp/api/ibox/login/poll" when ++polls == 1 => Json(HttpStatusCode.Accepted, """{"state":"seen"}"""),
            "/sub/_jp/api/ibox/login/poll" => Json(HttpStatusCode.OK, """{"token":"ibx1.secret","user":{"id":7,"first_name":"Ali","username":"ali"}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        };

        account.SignedIn.Should().BeFalse("a person with no account must sign in with Telegram first");
        var challenge = await account.Login.StartAsync("Test PC");
        challenge.Code.Should().Be("0420");
        challenge.Link.Host.Should().Be("t.me");
        server.Seen[0].Body["vh"]!.GetValue<string>().Should().Be(TelegramLogin.Hash(challenge.Verifier)).And.NotBe(challenge.Verifier);

        var result = await account.Login.WaitAsync(challenge);
        server.Seen[^1].Body["v"]!.GetValue<string>().Should().Be(challenge.Verifier);
        result.User.Display.Should().Be("@ali");
        account.Accept(result);
        tokens.Get(IboxAccount.TokenName).Should().Be("ibx1.secret");
        account.SignedIn.Should().BeTrue();
    }

    [Fact]
    public async Task Official_login_opens_oauth_telegram_org_then_collects_the_session_with_the_same_verifier()
    {
        var (account, server, _) = Panel();
        server.Answer = (path, _) => path switch
        {
            "/sub/_jp/api/ibox/oidc/start" => Json(HttpStatusCode.OK, """{"nonce":"N123456789012345678901","ttl":600,"url":"https://oauth.telegram.org/auth?client_id=1&state=N1"}"""),
            "/sub/_jp/api/ibox/login/poll" => Json(HttpStatusCode.OK, """{"token":"ibx1.secret","user":{"id":7,"first_name":"Ali"}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        };

        var c = await account.Login.StartOfficialAsync("Test PC");
        c.Should().NotBeNull();
        c!.Official.Should().BeTrue();
        c.Link.Host.Should().Be("oauth.telegram.org");
        server.Seen[0].Body["vh"]!.GetValue<string>().Should().Be(TelegramLogin.Hash(c.Verifier));
        (await account.Login.WaitAsync(c)).Token.Should().Be("ibx1.secret");
        server.Seen[^1].Body["v"]!.GetValue<string>().Should().Be(c.Verifier);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"not set up","fallback":true}""")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    public async Task Official_login_is_null_when_the_server_has_none_so_the_bot_is_used(HttpStatusCode code, string body)
    {
        var (account, server, _) = Panel();
        server.Answer = (_, _) => Json(code, body);
        (await account.Login.StartOfficialAsync("pc")).Should().BeNull();
    }

    [Fact]
    public async Task Official_login_rejects_a_link_that_is_not_telegrams_own_page()
    {
        var (account, server, _) = Panel();
        server.Answer = (_, _) => Json(HttpStatusCode.OK, """{"nonce":"N123456789012345678901","url":"https://oauth.telegram.org.evil.example/auth"}""");
        await account.Invoking(a => a.Login.StartOfficialAsync("pc")).Should().ThrowAsync<LoginFailedException>();
    }

    [Fact]
    public async Task Login_rejects_a_non_telegram_link_and_reports_a_deny()
    {
        var (account, server, _) = Panel();
        server.Answer = (path, _) => path.EndsWith("start", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"nonce":"N123456789012345678901","code":"1","url":"https://evil.example/x"}""")
            : Json(HttpStatusCode.Forbidden, """{"state":"denied","error":"denied"}""");
        await account.Invoking(a => a.Login.StartAsync("pc")).Should().ThrowAsync<LoginFailedException>();

        var c = new LoginChallenge("N", "1", new Uri("https://t.me/x"), null, TimeSpan.FromMinutes(1), "v");
        await account.Invoking(a => a.Login.WaitAsync(c)).Should().ThrowAsync<LoginFailedException>().WithMessage("denied");
    }

    [Fact]
    public async Task Subscription_view_uses_bearer_and_etag_and_parses_the_mini_app_data()
    {
        var (account, server, tokens) = Panel();
        tokens.Set(IboxAccount.TokenName, "ibx1.t");
        server.Answer = (_, _) => server.Seen[^1].Inm == "\"e1\"" ? new HttpResponseMessage(HttpStatusCode.NotModified) : Json(HttpStatusCode.OK, App, "\"e1\"");

        var (r, view) = await account.GetAsync();
        r.ETag.Should().Be("\"e1\"");
        server.Seen[0].Auth.Should().Be("ibx1.t");
        var card = view!.Subscriptions.Should().ContainSingle().Subject;
        card.UsedFraction.Should().BeApproximately(0.1, 1e-9);
        card.Expires.Should().Be(DateTimeOffset.FromUnixTimeSeconds(2000000000));
        card.Options["add_gb"].Should().Equal(30, 50, 100);
        card.SwitchTo.Should().Equal("us", "de");
        card.Requests.Single().NeedsAccept.Should().BeTrue();
        card.Nex!.CanPickExit.Should().BeTrue();
        card.Nex.Exits.Select(x => x.Backend).Should().Equal("nl", "de");
        view.User!.Username.Should().Be("ali");

        var (again, none) = await account.GetAsync(r.ETag, waitSeconds: 25);
        again.NotModified.Should().BeTrue();
        none.Should().BeNull();
        server.Seen[^1].Path.Should().Be("/sub/_jp/api/ibox/app?wait=25");
    }

    [Fact]
    public async Task Actions_post_the_mini_app_fields_and_a_401_signs_out()
    {
        var (account, server, tokens) = Panel();
        tokens.Set(IboxAccount.TokenName, "ibx1.t");
        server.Answer = (p, _) => p.EndsWith("nexexit", StringComparison.Ordinal) ? Json(HttpStatusCode.Unauthorized, """{"error":"signed out"}""") : Json(HttpStatusCode.OK, "{\"ok\":true}");
        var signedOut = false;
        account.SignedOut += (_, _) => signedOut = true;

        (await account.RequestAsync("L1", "add_gb", 50)).Ok.Should().BeTrue();
        server.Seen[^1].Body.ToJsonString().Should().Be("""{"lid":"L1","kind":"add_gb","value":50}""");
        await account.NexAllowAsync("L1");
        server.Seen[^1].Path.Should().EndWith("/nexallow");
        (await account.NexExitAsync("L1", "de")).SignedOut.Should().BeTrue();
        signedOut.Should().BeTrue();
        account.SignedIn.Should().BeFalse();
        await account.Invoking(a => a.SendReceiptAsync("r", new byte[1_600_000])).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Unreachable_first_address_falls_back_to_the_relay()
    {
        var server = new FakeServer();
        server.Answer = (p, _) => p.StartsWith("/m/api/ibox/", StringComparison.Ordinal) && server.Seen.Count == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Json(HttpStatusCode.OK, "{\"ok\":true}");
        var http = new IboxHttp(new HttpClient(server), IboxEndpoints.Music);
        (await http.PostAsync("pass/state", null)).Ok.Should().BeTrue();
        http.Base.Host.Should().Be("m3.jackidrive.ir");
    }

    [Fact]
    public async Task Music_password_flow_and_library_links()
    {
        var server = new FakeServer();
        var tokens = new MemoryTokenStore();
        var music = new MusicClient(new IboxHttp(new HttpClient(server), IboxEndpoints.Music), tokens);
        server.Answer = (p, b) => p switch
        {
            "/m/api/ibox/pass/login" when b["pass"]!.GetValue<string>() == "correct horse" => Json(HttpStatusCode.OK, """{"token":"ibm1.x","user":{"id":7,"first_name":"A"},"pass_set":true}"""),
            "/m/api/ibox/pass/login" => Json(HttpStatusCode.Forbidden, """{"error":"wrong"}"""),
            "/m/api/ibox/pass/set" => Json(HttpStatusCode.OK, """{"ok":true}"""),
            "/m/api/ibox/me" => Json(HttpStatusCode.OK, """{"me":{"id":7,"first_name":"A"},"stream":"https://m3.jackidrive.ir:2083/m/","songs":[{"k":"k1","a":"Art","t":"Song","d":200,"s":"c/u.1.sig.mp3","img":"https://i.example/c.jpg"},{"k":"k2","a":"","t":"P","s":"p/tok"},{"k":"k3","t":"bad","s":"javascript:x"}],"likes":[],"pls":[{"id":"p1","name":"Mine","songs":[{"k":"k1","s":"c/u.1.sig.mp3"}]}],"resume":{"song":{"k":"k1","s":"c/u.1.sig.mp3"},"t":42.5}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        };

        MusicClient.PasswordProblem("short").Should().NotBeNull();
        MusicClient.PasswordProblem("12345678").Should().NotBeNull();
        MusicClient.PasswordProblem("correct horse").Should().BeNull();
        await music.Invoking(m => m.SetPasswordAsync("correct horse")).Should().ThrowAsync<InvalidOperationException>("no session yet");

        await music.Invoking(m => m.PasswordLoginAsync("@ali", "nope nope", "pc")).Should().ThrowAsync<LoginFailedException>();
        music.SignedIn.Should().BeFalse();
        (await music.PasswordLoginAsync("@ali", "correct horse", "pc")).Id.Should().Be(7);
        tokens.Get(MusicClient.TokenName).Should().Be("ibm1.x");

        await music.SetPasswordAsync("another pass", current: "correct horse");
        server.Seen[^1].Body["old"]!.GetValue<string>().Should().Be("correct horse");

        var lib = await music.LibraryAsync();
        lib.Songs[0].Stream.Should().Be(new Uri("https://m3.jackidrive.ir:2083/m/c/u.1.sig.mp3"));
        lib.Songs[1].Stream!.Host.Should().Be("radio-us.jackidrive.ir", "partner links stay on the US app");
        lib.Songs[2].Stream.Should().BeNull();
        lib.Playlists.Single().Songs.Single().Key.Should().Be("k1");
        lib.ResumeAt.Should().Be(42.5);
    }

    private sealed class FakeOut : IAudioOutput
    {
        public bool IsPlaying { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => TimeSpan.FromSeconds(200);

        public Uri? Opened { get; private set; }

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;

        public void Open(Uri stream, TimeSpan start)
        {
            Opened = stream;
            Position = start;
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Seek(TimeSpan to) => Position = to;

        public void End() => Ended?.Invoke(this, EventArgs.Empty);

        public void Fail() => Failed?.Invoke(this, "x");

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Player_queue_controls_and_now_playing()
    {
        var output = new FakeOut();
        var saved = new List<double>();
        using var player = new MusicPlayer(output, (_, s) => { saved.Add(s); return Task.CompletedTask; });
        var changes = 0;
        player.Changed += (_, _) => changes++;
        MusicTrack T(string k) => new(k, "A", k, 200, null, new Uri("https://x.example/" + k), "");
        player.Play([T("a"), new MusicTrack("nolink", "", "n", 0, null, null, ""), T("b")], 2);

        player.Track!.Key.Should().Be("b");
        player.Current.Should().Be(new JackiVpn.Application.Media.NowPlaying("b", "A", "IBOX", true));
        await player.NextAsync();
        player.Track!.Key.Should().Be("a", "the queue wraps and skips songs without a link");
        output.Position = TimeSpan.FromSeconds(10);
        await player.PreviousAsync();
        output.Position.Should().Be(TimeSpan.Zero);
        player.Track!.Key.Should().Be("a");
        await player.PreviousAsync();
        player.Track!.Key.Should().Be("b");
        player.Seek(TimeSpan.FromSeconds(999));
        output.Position.Should().Be(TimeSpan.FromSeconds(200));
        await player.PlayPauseAsync();
        player.IsPlaying.Should().BeFalse();
        saved.Should().NotBeEmpty();
        output.End();
        player.Track!.Key.Should().Be("a");
        output.Fail();
        player.Error.Should().Be("x");
        changes.Should().BeGreaterThan(4);
    }

    [Fact]
    public void Token_file_is_encrypted_with_the_platform_protector()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ibox-tok-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProtectedFileTokenStore(dir, new DpapiSecretProtector());
            store.Get("panel").Should().BeNull();
            store.Set("panel", "ibx1.secret-token");
            File.ReadAllText(Path.Combine(dir, "panel.tok")).Should().NotContain("secret-token");
            store.Get("panel").Should().Be("ibx1.secret-token");
            store.Remove("panel");
            store.Get("panel").Should().BeNull();
            store.Invoking(s => s.Set("../x", "t")).Should().Throw<ArgumentException>();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
