using FluentAssertions;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Providers;

namespace JackiVpn.IntegrationTests.Subscriptions;

/// <summary>PasarGuard provider against a fake panel over real HTTP. Every failure must be non-destructive.</summary>
public sealed class PasarGuardProviderTests : IDisposable
{
    private readonly FakePasarGuard _pg = new();
    private readonly SafeHttpFetcher _fetcher = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _pg.Dispose();
        _fetcher.Dispose();
    }

    private SubscriptionFetchRequest Request(string? etag = null, string? preferred = null, string? previousHash = null, TimeSpan? timeout = null) => new()
    {
        Url = _pg.BaseUrl,
        ETag = etag,
        PreferredEndpoint = preferred,
        PreviousContentHash = previousHash,
        Settings = new SubscriptionSettings { Timeout = timeout ?? TimeSpan.FromSeconds(10) },
        ParseContext = new ParseContext { OriginKind = ProfileOriginKind.Subscription, SubscriptionId = "sub-1" },
        Ownership = new OwnershipPolicy(),
    };

    private Task<SubscriptionFetchResult> Fetch(SubscriptionFetchRequest? r = null) =>
        new PasarGuardSubscriptionProvider(_fetcher, new ConfigParserPipeline()).FetchAsync(r ?? Request(), Ct);

    [Fact]
    public async Task Html_base_page_is_never_parsed_and_sing_box_endpoint_is_used()
    {
        _pg.Head().BaseGetHtml().Endpoint("sing_box", FakePasarGuard.SingBoxJson("🇩🇪 DE", "🇳🇱 NL"), contentType: "application/json");

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Updated);
        r.ResolvedEndpoint.Should().Be("sing_box");
        r.Format.Should().Be(ConfigFormat.SingBoxFull);
        r.Parsed!.Profiles.Select(p => p.Name).Should().Equal("🇩🇪 DE", "🇳🇱 NL");
        r.Parsed.Profiles[0].Tls.Fragment!.FallbackDelay.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    private static readonly IReadOnlyDictionary<string, string> Device =
        new DeviceIdentity("0123456789abcdef0123456789abcdef", "Windows", "10.0.26100", "Windows PC").Headers();

    [Fact]
    public async Task Device_id_headers_reach_the_panel_on_every_request()
    {
        _pg.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch(Request() with { DeviceHeaders = Device });

        r.Outcome.Should().Be(FetchOutcome.Updated);
        var requests = _pg.RequestHeaders().ToList();
        requests.Should().NotBeEmpty();
        requests.Should().OnlyContain(h => h.ContainsKey("x-hwid") && h["x-hwid"] == "0123456789abcdef0123456789abcdef");
        requests.Should().OnlyContain(h => h["x-device-os"] == "Windows" && h["x-device-model"] == "Windows PC");
    }

    [Fact]
    public async Task No_device_id_is_sent_unless_requested()
    {
        _pg.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        (await Fetch()).Outcome.Should().Be(FetchOutcome.Updated);
        _pg.RequestHeaders().Should().OnlyContain(h => !h.ContainsKey("x-hwid") && !h.ContainsKey("x-device-model"));
    }

    [Fact]
    public async Task Device_limit_reached_is_reported_and_nothing_is_parsed()
    {
        _pg.RefuseDevice("Device limit reached").Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch(Request() with { DeviceHeaders = Device });

        r.Outcome.Should().Be(FetchOutcome.Failed, "a refused device must never replace the stored servers");
        r.Failure.Should().Be(FetchFailureKind.DeviceLimitReached);
        r.DeviceLimit.Should().Be(DeviceLimitState.LimitReached);
        r.Error.Should().Contain("device limit");
        r.Parsed.Should().BeNull();
    }

    [Fact]
    public async Task Missing_device_id_on_a_limited_panel_is_explained()
    {
        _pg.RefuseDevice("HWID header required");

        var r = await Fetch();

        r.Failure.Should().Be(FetchFailureKind.DeviceIdRequired);
        r.DeviceLimit.Should().Be(DeviceLimitState.DeviceIdRequired);
        r.Error.Should().Contain("Send device ID");
    }

    [Fact]
    public async Task Head_metadata_is_extracted()
    {
        _pg.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch();

        r.Metadata.Title.Should().Be("Jacki Panel");
        r.Metadata.DownloadBytes.Should().Be(1073741824);
        r.Metadata.TotalBytes.Should().Be(10737418240);
        r.Metadata.ExpireUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1893456000));
        r.Metadata.ProviderUpdateInterval.Should().Be(TimeSpan.FromHours(12));
        r.Metadata.ComputeStatus(DateTimeOffset.UtcNow).Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task Head_not_allowed_falls_back_to_get_for_metadata()
    {
        _pg.Status(string.Empty, 405);
        _pg.Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Updated);
        r.Trace.Should().Contain(t => t.Contains("HEAD fallback"));
    }

    [Theory]
    [InlineData("links")]
    [InlineData("links_base64")]
    [InlineData("xray")]
    public async Task Each_content_endpoint_format_is_supported(string endpoint)
    {
        _pg.Head();
        var body = endpoint switch
        {
            "links" => FakePasarGuard.Links("one", "دو"),
            "links_base64" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(FakePasarGuard.Links("one", "دو"))),
            _ => FakePasarGuard.XrayJson("one", "دو"),
        };
        _pg.Endpoint(endpoint, body);

        var r = await Fetch(Request(preferred: endpoint));

        r.Outcome.Should().Be(FetchOutcome.Updated);
        r.ResolvedEndpoint.Should().Be(endpoint);
        r.Parsed!.Profiles.Select(p => p.Name).Should().Equal("one", "دو");
    }

    [Fact]
    public async Task Missing_endpoints_fall_back_in_order()
    {
        _pg.Head();
        _pg.Status("sing_box", 404).Status("xray", 404);
        _pg.Endpoint("links", FakePasarGuard.Links("fallback"));

        var r = await Fetch();

        r.ResolvedEndpoint.Should().Be("links");
        r.Parsed!.Profiles.Should().ContainSingle();
    }

    [Theory]
    [InlineData("expired", 5L, 100L, "2020-01-01T00:00:00Z", SubscriptionStatus.Expired)]
    [InlineData("limited", 100L, 100L, null, SubscriptionStatus.Limited)]
    [InlineData("active", 5L, null, null, SubscriptionStatus.Active)]
    public async Task Info_endpoint_drives_status_quota_and_expiry(string status, long used, long? limit, string? expire, SubscriptionStatus expected)
    {
        _pg.Head(userinfo: null).Info(status, used, limit, expire).Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch();

        r.Metadata.ComputeStatus(DateTimeOffset.UtcNow).Should().Be(expected);
        if (limit is null)
        {
            r.Metadata.IsUnlimitedTraffic.Should().BeTrue();
            r.Metadata.IsUnlimitedExpiry.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Unlimited_subscription_from_headers()
    {
        _pg.Head(userinfo: "upload=0; download=42; total=0; expire=0").Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch();

        r.Metadata.IsUnlimitedTraffic.Should().BeTrue();
        r.Metadata.IsUnlimitedExpiry.Should().BeTrue();
        r.Metadata.RemainingBytes.Should().BeNull();
    }

    [Theory]
    [InlineData(401, FetchFailureKind.Unauthorized)]
    [InlineData(403, FetchFailureKind.Forbidden)]
    [InlineData(404, FetchFailureKind.NotFound)]
    [InlineData(429, FetchFailureKind.RateLimited)]
    public async Task Auth_level_errors_stop_and_report(int status, FetchFailureKind kind)
    {
        _pg.Status(string.Empty, status, status == 429 ? TimeSpan.FromSeconds(120) : null);

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Failed);
        r.Failure.Should().Be(kind);
        r.HttpStatus.Should().Be(status);
        r.Parsed.Should().BeNull();
        if (status == 429)
        {
            r.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        }
    }

    [Fact]
    public async Task Server_errors_on_every_endpoint_fail_without_data()
    {
        _pg.Head();
        foreach (var e in new[] { "sing_box", "xray", "links", "links_base64", "clash_meta", string.Empty })
        {
            _pg.Status(e, 500);
        }

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Failed);
        r.Failure.Should().Be(FetchFailureKind.ServerError);
        r.Parsed.Should().BeNull();
    }

    [Fact]
    public async Task Timeout_is_reported_as_timeout()
    {
        _pg.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json", delay: TimeSpan.FromSeconds(5));

        var r = await Fetch(Request(timeout: TimeSpan.FromSeconds(1)));

        r.Outcome.Should().Be(FetchOutcome.Failed);
        r.Failure.Should().Be(FetchFailureKind.Timeout);
    }

    [Fact]
    public async Task Broken_responses_everywhere_are_parse_failures()
    {
        _pg.Head();
        foreach (var e in new[] { "sing_box", "xray", "links", "links_base64", "clash_meta" })
        {
            _pg.Endpoint(e, "%%% this is not a subscription %%%");
        }

        _pg.BaseGetHtml();

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Failed);
        r.Failure.Should().BeOneOf(FetchFailureKind.ParseFailed, FetchFailureKind.Html);
    }

    [Fact]
    public async Task Redirect_is_followed()
    {
        _pg.Head();
        _pg.Redirect("sing_box", _pg.Path("links"));
        _pg.Endpoint("links", FakePasarGuard.Links("redirected"));

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Updated);
        r.Parsed!.Profiles.Single().Name.Should().Be("redirected");
    }

    [Fact]
    public async Task Redirect_to_non_http_scheme_is_blocked()
    {
        _pg.Head();
        _pg.Redirect("sing_box", "file:///C:/Windows/win.ini");

        var r = await Fetch();

        r.Outcome.Should().Be(FetchOutcome.Failed);
        r.Failure.Should().Be(FetchFailureKind.Blocked);
    }

    [Fact]
    public async Task Etag_304_and_changed_etag()
    {
        _pg.Head();
        _pg.Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json", etag: "\"v1\"");
        _pg.NotModifiedWhen("sing_box", "\"v1\"");

        var first = await Fetch();
        first.ETag.Should().Be("\"v1\"");

        var second = await Fetch(Request(etag: first.ETag, preferred: first.ResolvedEndpoint, previousHash: first.ContentHash));
        second.Outcome.Should().Be(FetchOutcome.NotModified);
        second.HttpStatus.Should().Be(304);

        _pg.Server.Reset();
        _pg.Head();
        _pg.Endpoint("sing_box", FakePasarGuard.SingBoxJson("a", "b"), contentType: "application/json", etag: "\"v2\"");
        var third = await Fetch(Request(etag: "\"v1\"", preferred: "sing_box", previousHash: first.ContentHash));
        third.Outcome.Should().Be(FetchOutcome.Updated);
        third.ETag.Should().Be("\"v2\"");
        third.Parsed!.Profiles.Should().HaveCount(2);
    }

    [Fact]
    public async Task Same_body_without_etag_is_not_modified_by_content_hash()
    {
        _pg.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");
        var first = await Fetch();

        var second = await Fetch(Request(preferred: "sing_box", previousHash: first.ContentHash));

        second.Outcome.Should().Be(FetchOutcome.NotModified);
    }

    [Fact]
    public async Task Response_size_limit_is_enforced()
    {
        _pg.Head().Endpoint("sing_box", new string('x', 300_000));
        var request = Request() with { Settings = new SubscriptionSettings { MaxResponseBytes = 100_000, Timeout = TimeSpan.FromSeconds(10) } };

        var r = await Fetch(request);

        r.Outcome.Should().Be(FetchOutcome.Failed);
    }

    [Fact]
    public async Task Ownership_marker_header_is_captured()
    {
        _pg.Head(extra: new Dictionary<string, string> { ["X-Jacki-Profile"] = "v1" }).Endpoint("sing_box", FakePasarGuard.SingBoxJson("a"), contentType: "application/json");

        var r = await Fetch();

        r.ProviderHeaders.Should().ContainKey("X-Jacki-Profile");
        r.WasHttps.Should().BeFalse();
    }

    [Fact]
    public async Task Provider_detector_recognizes_pasarguard()
    {
        _pg.Head().Info("active", 1, null, null);

        var kind = await new SubscriptionProviderDetector(_fetcher).DetectAsync(_pg.BaseUrl, new SubscriptionSettings(), Ct);

        kind.Should().Be(SubscriptionProviderKind.PasarGuard);
    }

    [Fact]
    public async Task Generic_provider_handles_plain_and_rejects_html()
    {
        _pg.Endpoint("links", FakePasarGuard.Links("g1", "g2"));
        var generic = new GenericSubscriptionProvider(_fetcher, new ConfigParserPipeline());

        var ok = await generic.FetchAsync(Request() with { Url = _pg.BaseUrl + "/links" }, Ct);
        ok.Outcome.Should().Be(FetchOutcome.Updated);
        ok.Parsed!.Profiles.Should().HaveCount(2);

        _pg.BaseGetHtml();
        var html = await generic.FetchAsync(Request(), Ct);
        html.Outcome.Should().Be(FetchOutcome.Failed);
        html.Failure.Should().Be(FetchFailureKind.Html);
    }
}
