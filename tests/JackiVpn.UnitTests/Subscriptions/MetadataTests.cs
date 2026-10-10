using FluentAssertions;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Metadata;
using JackiVpn.Subscriptions.Providers;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Subscriptions;

public class MetadataTests
{
    private static Dictionary<string, string> H(params (string K, string V)[] kv) =>
        kv.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("upload=100; download=200; total=1000; expire=1893456000", 100L, 200L, 1000L, 1893456000L)]
    [InlineData("upload=100;download=200;total=1000;expire=1893456000", 100L, 200L, 1000L, 1893456000L)]
    [InlineData("download=2.5E3; upload=0; total=0; expire=0", 0L, 2500L, 0L, 0L)]
    [InlineData("upload=1, download=2, total=3, expire=", 1L, 2L, 3L, null)]
    [InlineData("TOTAL=5; Upload=1; foo=bar", 1L, null, 5L, null)]
    public void Userinfo_header_variants(string header, long? up, long? down, long? total, long? expire)
    {
        var info = SubscriptionMetadataParser.ParseUserInfo(header)!;

        info.Upload.Should().Be(up);
        info.Download.Should().Be(down);
        info.Total.Should().Be(total);
        info.ExpireUnixSeconds.Should().Be(expire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("upload=-5")]
    public void Userinfo_header_without_usable_values_is_null(string? header)
    {
        SubscriptionMetadataParser.ParseUserInfo(header).Should().BeNull();
    }

    [Fact]
    public void Limited_subscription_metadata()
    {
        var m = SubscriptionMetadataParser.FromHeaders(H(("subscription-userinfo", "upload=0; download=750; total=1000; expire=1893456000")), Now);

        m.TotalBytes.Should().Be(1000);
        m.IsUnlimitedTraffic.Should().BeFalse();
        m.UsedBytes.Should().Be(750);
        m.RemainingBytes.Should().Be(250);
        m.UsedPercent.Should().Be(75);
        m.ExpireUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1893456000));
        m.IsUnlimitedExpiry.Should().BeFalse();
        m.ComputeStatus(Now).Should().Be(SubscriptionStatus.Active);
        m.TimeRemaining(Now).Should().BePositive();
    }

    [Fact]
    public void Unlimited_traffic_and_expiry_from_zero_values()
    {
        var m = SubscriptionMetadataParser.FromHeaders(H(("subscription-userinfo", "upload=0; download=5; total=0; expire=0")), Now);

        m.IsUnlimitedTraffic.Should().BeTrue();
        m.IsUnlimitedExpiry.Should().BeTrue();
        m.TotalBytes.Should().BeNull();
        m.RemainingBytes.Should().BeNull();
        m.UsedPercent.Should().BeNull();
        m.TimeRemaining(Now).Should().BeNull();
        m.ComputeStatus(Now).Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public void Missing_metadata_stays_unknown()
    {
        var m = SubscriptionMetadataParser.FromHeaders(H(), Now);

        m.TotalBytes.Should().BeNull();
        m.IsUnlimitedTraffic.Should().BeNull();
        m.ExpireUtc.Should().BeNull();
        m.IsUnlimitedExpiry.Should().BeNull();
        m.UsedBytes.Should().BeNull();
        m.ComputeStatus(Now).Should().Be(SubscriptionStatus.Unknown);
    }

    [Fact]
    public void Expired_and_quota_exhausted_statuses()
    {
        var expired = SubscriptionMetadataParser.FromHeaders(H(("subscription-userinfo", "upload=0; download=1; total=100; expire=1600000000")), Now);
        expired.ComputeStatus(Now).Should().Be(SubscriptionStatus.Expired);

        var limited = SubscriptionMetadataParser.FromHeaders(H(("subscription-userinfo", "upload=60; download=40; total=100; expire=0")), Now);
        limited.ComputeStatus(Now).Should().Be(SubscriptionStatus.Limited);
    }

    [Fact]
    public void Title_interval_and_links()
    {
        var m = SubscriptionMetadataParser.FromHeaders(H(
            ("profile-title", "base64:" + B64("جکی VPN 🚀")),
            ("profile-update-interval", "12"),
            ("support-url", "https://t.me/support"),
            ("profile-web-page-url", "javascript:alert(1)"),
            ("announce", "base64:" + B64("hello"))), Now);

        m.Title.Should().Be("جکی VPN 🚀");
        m.ProviderUpdateInterval.Should().Be(TimeSpan.FromHours(12));
        m.SupportUrl.Should().Be("https://t.me/support");
        m.WebPageUrl.Should().BeNull("non-http(s) links from headers are dropped");
        m.Announcement.Should().Be("hello");
    }

    [Theory]
    [InlineData("""{"username":"u","status":"active","used_traffic":1073741824,"data_limit":10737418240,"expire":"2027-01-01T00:00:00Z"}""", "active", 10737418240L, false, false)]
    [InlineData("""{"username":"u","status":"active","used_traffic":5,"data_limit":null,"expire":null}""", "active", null, true, true)]
    [InlineData("""{"username":"u","status":"limited","used_traffic":5,"data_limit":0,"expire":0}""", "limited", null, true, true)]
    [InlineData("""{"username":"u","status":"expired","used_traffic":5,"data_limit":100,"expire":1600000000}""", "expired", 100L, false, false)]
    public void PasarGuard_info_json(string json, string status, long? total, bool unlimitedTraffic, bool unlimitedExpiry)
    {
        var m = SubscriptionMetadataParser.FromPasarGuardInfo(json, Now)!;

        m.ProviderStatus.Should().Be(status);
        m.TotalBytes.Should().Be(total);
        m.IsUnlimitedTraffic.Should().Be(unlimitedTraffic);
        m.IsUnlimitedExpiry.Should().Be(unlimitedExpiry);
        m.Username.Should().Be("u");
    }

    [Fact]
    public void PasarGuard_info_rejects_unrelated_json()
    {
        SubscriptionMetadataParser.FromPasarGuardInfo("""{"hello":"world"}""", Now).Should().BeNull();
        SubscriptionMetadataParser.FromPasarGuardInfo("not json", Now).Should().BeNull();
    }

    [Theory]
    [InlineData("https://panel.example.com/sub/AbCdEfGh12345678", "https://panel.example.com/sub/AbCdEfGh12345678", null)]
    [InlineData("https://panel.example.com/sub/AbCdEfGh12345678/", "https://panel.example.com/sub/AbCdEfGh12345678", null)]
    [InlineData("https://panel.example.com:8443/sub/AbCdEfGh12345678/links", "https://panel.example.com:8443/sub/AbCdEfGh12345678", "links")]
    [InlineData("https://panel.example.com/custom/path/AbCdEfGh12345678/sing_box", "https://panel.example.com/custom/path/AbCdEfGh12345678", "sing_box")]
    [InlineData("https://panel.example.com/sub/AbCdEfGh12345678/info", "https://panel.example.com/sub/AbCdEfGh12345678", "info")]
    [InlineData("https://panel.example.com/sub/AbCdEfGh12345678/links_base64?x=1", "https://panel.example.com/sub/AbCdEfGh12345678", "links_base64")]
    public void PasarGuard_endpoint_detection(string url, string expectedBase, string? expectedEndpoint)
    {
        PasarGuardEndpoints.TryParse(new Uri(url), out var pg).Should().BeTrue();

        pg!.BaseUrl.ToString().TrimEnd('/').Should().Be(expectedBase);
        pg.Endpoint.Should().Be(expectedEndpoint);
        pg.For("xray").ToString().Should().Be(expectedBase + "/xray");
    }

    [Theory]
    [InlineData("https://panel.example.com/")]
    [InlineData("https://panel.example.com/sub/short")]
    [InlineData("ftp://panel.example.com/sub/AbCdEfGh12345678")]
    public void Non_pasarguard_urls_are_not_matched(string url)
    {
        PasarGuardEndpoints.TryParse(new Uri(url), out _).Should().BeFalse();
    }

    [Fact]
    public void Content_order_prefers_user_then_previous_then_defaults()
    {
        PasarGuardEndpoints.ContentOrder("links", "xray").Should().StartWith(["xray", "links", "sing_box"]);
        PasarGuardEndpoints.ContentOrder(null, "info").First().Should().Be("sing_box", "meta endpoints are not content endpoints");
    }

    [Fact]
    public void Provider_detection_from_headers()
    {
        SubscriptionProviderDetector.IsPasarGuardHeaders(H(("profile-web-page-url", "https://p/sub/x"))).Should().BeTrue();
        SubscriptionProviderDetector.IsPasarGuardHeaders(H(("content-type", "text/plain"))).Should().BeFalse();
    }
}
