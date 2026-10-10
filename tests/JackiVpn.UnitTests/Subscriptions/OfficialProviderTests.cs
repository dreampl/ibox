using FluentAssertions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Ownership;
using JackiVpn.Subscriptions.Providers;

namespace JackiVpn.UnitTests.Subscriptions;

public class OfficialProviderTests
{
    [Theory]
    [InlineData("jacki-inc.ir", true)]
    [InlineData("sub.jacki-inc.ir", true)]
    [InlineData("panel.eu.jackidrive.ir", true)]
    [InlineData("JACKIAIRLINE.IR", true)]
    [InlineData("jackidrive.ir.", true)]
    [InlineData("fakejacki-inc.ir", false)]
    [InlineData("evil-jackidrive.ir", false)]
    [InlineData("jacki-inc.ir.evil.com", false)]
    [InlineData("jacki.com", false)]
    [InlineData("my-jacki-vpn.net", false)]
    [InlineData("", false)]
    public void Official_domain_match_is_by_label_never_by_substring(string host, bool official) =>
        NetText.IsHostInDomains(host, JackiVpn.Domain.ProductInfo.OfficialProviderDomains).Should().Be(official);

    [Theory]
    [InlineData("https://sub.jacki-inc.ir/sub/AbCdEf123456", true)]
    [InlineData("https://jackiairline.ir:8443/sub/x", true)]
    [InlineData("http://jacki-inc.ir/sub/x", false)]
    [InlineData("https://jacki-inc.ir.attacker.com/sub/x", false)]
    [InlineData("not a url", false)]
    public void Official_url_requires_https_on_an_official_domain(string url, bool official) =>
        OfficialProviders.IsOfficialUrl(url).Should().Be(official);

    [Fact]
    public void Official_subscriptions_are_owned_only_over_https()
    {
        var policy = new OwnershipPolicy();
        var https = ProfileOwnershipVerifier.VerifySubscription("panel.jackidrive.ir", true, new Dictionary<string, string>(), policy, DateTimeOffset.UtcNow);
        var http = ProfileOwnershipVerifier.VerifySubscription("panel.jackidrive.ir", false, new Dictionary<string, string>(), policy, DateTimeOffset.UtcNow);
        var lookalike = ProfileOwnershipVerifier.VerifySubscription("jackidrive.ir.example.com", true, new Dictionary<string, string>(), policy, DateTimeOffset.UtcNow);

        https.IsOwned.Should().BeTrue();
        https.Method.Should().Be(OwnershipMethod.OfficialDomain);
        new Subscription { Id = "s", Name = "n", MaskedUrl = "m", Host = "panel.jackidrive.ir", Ownership = https }.IsOfficial.Should().BeTrue();
        http.IsOwned.Should().BeFalse("over plain HTTP the configs could be rewritten in transit");
        lookalike.IsOwned.Should().BeFalse();
    }

    [Fact]
    public void Device_id_is_random_well_formed_and_headers_are_sanitized()
    {
        var a = DeviceIdentity.NewHwid();
        var b = DeviceIdentity.NewHwid();
        a.Should().MatchRegex("^[0-9a-f]{32}$");
        b.Should().NotBe(a);

        var headers = new DeviceIdentity(a, "Android", "15", "Google Pixel 6\r\nX-Evil: 1").Headers();
        headers[DeviceIdentity.HwidHeader].Should().Be(a);
        headers[DeviceIdentity.OsHeader].Should().Be("Android");
        headers[DeviceIdentity.OsVersionHeader].Should().Be("15");
        headers[DeviceIdentity.ModelHeader].Should().NotContain("\r").And.NotContain("\n");

        var invalid = () => new DeviceIdentity("short", "Windows", "10", "PC").Headers();
        invalid.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Settings_upgrade_creates_a_device_id_once_and_keeps_it()
    {
        var first = SettingsMigrations.Upgrade(new AppSettings { SchemaVersion = 3 });
        DeviceIdentity.IsValidHwid(first.Device.Hwid).Should().BeTrue();
        first.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        SettingsMigrations.Upgrade(first).Device.Hwid.Should().Be(first.Device.Hwid);
    }

    [Theory]
    [InlineData(403, "{\"detail\":\"Device limit reached\"}", null, true, DeviceLimitState.LimitReached)]
    [InlineData(403, "{\"detail\":\"HWID header required\"}", null, false, DeviceLimitState.DeviceIdRequired)]
    [InlineData(200, "", "x-hwid-max-devices-reached", true, DeviceLimitState.LimitReached)]
    [InlineData(200, "", "x-hwid-limit", true, DeviceLimitState.LimitReached)]
    [InlineData(404, "", "x-hwid-not-supported", false, DeviceLimitState.DeviceIdRequired)]
    [InlineData(200, "", "x-hwid-active", true, DeviceLimitState.Registered)]
    public void Provider_device_limit_signals_are_read(int status, string body, string? flag, bool sent, DeviceLimitState expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (flag is not null)
        {
            headers[flag] = "true";
        }

        var r = new FetchResponse { StatusCode = status, FinalUrl = new Uri("https://p.example.com/sub/x"), Headers = headers, Body = body };
        ProviderSupport.ReadDeviceLimit(r, sent)!.Value.State.Should().Be(expected);
    }

    [Fact]
    public void A_plain_forbidden_without_device_signals_is_not_a_device_limit()
    {
        var r = new FetchResponse { StatusCode = 403, FinalUrl = new Uri("https://p.example.com/sub/x"), Headers = new Dictionary<string, string>(), Body = "{\"detail\":\"User is disabled\"}" };
        ProviderSupport.ReadDeviceLimit(r, sentDeviceId: true).Should().BeNull();
    }

    [Theory]
    [InlineData("https://sub.jacki-inc.ir/sub/x", null, true)]
    [InlineData("https://panel.example.com/sub/x", null, false)]
    [InlineData("https://panel.example.com/sub/x", true, true)]
    [InlineData("https://jackidrive.ir/sub/x", false, false)]
    public void Device_id_goes_to_official_providers_by_default_and_to_others_only_when_allowed(string url, bool? choice, bool sends)
    {
        var sub = new Subscription { Id = "s", Name = "n", MaskedUrl = "m", Host = new Uri(url).Host, SendDeviceId = choice };
        JackiVpn.Application.Subscriptions.SubscriptionService.SendsDeviceId(sub, url).Should().Be(sends);
    }

    [Theory]
    [InlineData("https://a.example.com/x", "https://a.example.com/y", true)]
    [InlineData("https://a.example.com/x", "https://b.example.com/x", false)]
    [InlineData("http://127.0.0.1:5001/x", "http://127.0.0.1:5002/x", false)]
    [InlineData("https://a.example.com/x", "http://a.example.com/x", false)]
    public void Device_headers_only_follow_redirects_within_the_same_origin(string a, string b, bool same) =>
        SafeHttpFetcher.IsSameOrigin(new Uri(a), new Uri(b)).Should().Be(same);
}
