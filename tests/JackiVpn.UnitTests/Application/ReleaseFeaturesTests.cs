using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JackiVpn.Application.Health;
using JackiVpn.Application.Sessions;
using JackiVpn.Domain.Profiles;
using JackiVpn.Infrastructure.Updates;
using JackiVpn.Routing;
using JackiVpn.Subscriptions.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Application;

/// <summary>Signed update feed, chained-backend address pinning, account info and shipped rule-sets.</summary>
public class ReleaseFeaturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static (string Private, string Public) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    private static byte[] Feed(string version, byte[] apk, string url = "https://connect.jackiairline.ir/update/9.9.9/app.apk") =>
        JsonSerializer.SerializeToUtf8Bytes(new AppUpdateFeed(1, version, DateTimeOffset.UtcNow, false,
            new Dictionary<string, string> { ["en"] = "Fixes", ["fa"] = "رفع اشکال" },
            new AppUpdateAsset(url.Replace(".apk", ".msi", StringComparison.Ordinal), Convert.ToHexStringLower(SHA256.HashData(apk)), apk.Length),
            new Dictionary<string, AppUpdateAsset> { ["arm64-v8a"] = new(url, Convert.ToHexStringLower(SHA256.HashData(apk)), apk.Length) }));

    private static AppUpdateService Service(byte[] feed, string signature, byte[] apk, string publicKey) =>
        new(NullLogger.Instance, "https://connect.jackiairline.ir/update/latest.json", publicKey, new StubHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/update/latest.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed) },
            "/update/latest.json.sig" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(apk) },
        }));

    [Fact]
    public async Task Signed_feed_offers_newer_version_and_download_is_verified()
    {
        var (priv, pub) = NewKey();
        var apk = RandomNumberGenerator.GetBytes(4096);
        var feed = Feed("1.2.0", apk);
        using var service = Service(feed, AppUpdateService.Sign(feed, priv), apk, pub);

        var update = await service.CheckAsync(new Version(1, 0, 0), "android", "arm64-v8a", Ct);
        update.Should().NotBeNull();
        update!.VersionText.Should().Be("1.2.0");
        update.Notes(persian: true).Should().Be("رفع اشکال");

        var dir = Path.Combine(Path.GetTempPath(), "jacki-upd-" + Guid.NewGuid().ToString("N")[..6]);
        var file = await service.DownloadAsync(update, dir, null, Ct);
        (await File.ReadAllBytesAsync(file, Ct)).Should().Equal(apk);
        Directory.Delete(dir, true);

        (await service.CheckAsync(new Version(1, 2, 0), "android", "arm64-v8a", Ct)).Should().BeNull("an equal version is not an update");
        (await service.CheckAsync(new Version(2, 0, 0), "android", "arm64-v8a", Ct)).Should().BeNull("never a downgrade");
    }

    [Fact]
    public async Task Tampered_or_foreign_signed_feed_is_rejected()
    {
        var (priv, pub) = NewKey();
        var (otherPriv, _) = NewKey();
        var apk = RandomNumberGenerator.GetBytes(128);
        var feed = Feed("1.2.0", apk);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(feed).Replace("1.2.0", "1.3.0", StringComparison.Ordinal));

        using var a = Service(tampered, AppUpdateService.Sign(feed, priv), apk, pub);
        await a.Invoking(s => s.CheckAsync(new Version(1, 0, 0), "android", "arm64-v8a", Ct)).Should().ThrowAsync<AppUpdateException>();

        using var b = Service(feed, AppUpdateService.Sign(feed, otherPriv), apk, pub);
        await b.Invoking(s => s.CheckAsync(new Version(1, 0, 0), "android", "arm64-v8a", Ct)).Should().ThrowAsync<AppUpdateException>();
    }

    [Fact]
    public async Task Installer_from_a_foreign_host_or_with_wrong_bytes_is_rejected()
    {
        var (priv, pub) = NewKey();
        var apk = RandomNumberGenerator.GetBytes(256);
        var foreign = Feed("1.2.0", apk, "https://evil.example.com/app.apk");
        using var a = Service(foreign, AppUpdateService.Sign(foreign, priv), apk, pub);
        await a.Invoking(s => s.CheckAsync(new Version(1, 0, 0), "android", "arm64-v8a", Ct)).Should().ThrowAsync<AppUpdateException>();

        var feed = Feed("1.2.0", apk);
        using var b = Service(feed, AppUpdateService.Sign(feed, priv), RandomNumberGenerator.GetBytes(256), pub);
        var update = await b.CheckAsync(new Version(1, 0, 0), "android", "arm64-v8a", Ct);
        var dir = Path.Combine(Path.GetTempPath(), "jacki-upd-" + Guid.NewGuid().ToString("N")[..6]);
        await b.Invoking(s => s.DownloadAsync(update!, dir, null, Ct)).Should().ThrowAsync<AppUpdateException>();
        Directory.GetFiles(dir).Should().BeEmpty("a rejected download is deleted");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Built_in_update_key_is_a_valid_public_key()
    {
        using var key = ECDsa.Create();
        key.Invoking(k => k.ImportFromPem(JackiVpn.Domain.ProductInfo.UpdatePublicKeyPem)).Should().NotThrow();
    }

    [Fact]
    public void Chained_backend_dials_a_real_address_and_keeps_the_name_for_tls_and_host()
    {
        SessionPlanFactory.IsFakeIp(IPAddress.Parse("198.18.0.5")).Should().BeTrue();
        SessionPlanFactory.IsFakeIp(IPAddress.Parse("198.19.255.1")).Should().BeTrue();
        SessionPlanFactory.IsFakeIp(IPAddress.Parse("104.16.1.1")).Should().BeFalse();

        var p = Link("vless://11111111-2222-4333-8444-555555555555@cdn.example.com:443?security=tls&type=xhttp&path=%2Fx#x");
        var pinned = SessionPlanFactory.PinServerAddress(p, [IPAddress.Parse("104.16.1.1")]);

        pinned.Server.Should().Be("104.16.1.1");
        pinned.Tls.ServerName.Should().Be("cdn.example.com");
        pinned.Transport.Host.Should().Be("cdn.example.com");
    }

    [Fact]
    public void PasarGuard_info_reports_device_limit_and_online_time()
    {
        var m = SubscriptionMetadataParser.FromPasarGuardInfo("""{"username":"u","status":"active","used_traffic":10,"hwid_limit":3,"online_at":"2026-10-02T10:20:30"}""", DateTimeOffset.UtcNow);

        m!.DeviceLimit.Should().Be(3);
        m.OnlineAtUtc.Should().Be(new DateTimeOffset(2026, 10, 2, 10, 20, 30, TimeSpan.Zero));
        m.AccountCheckedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Shipped_rule_set_is_used_when_nothing_is_cached()
    {
        var root = Path.Combine(Path.GetTempPath(), "jacki-rs-" + Guid.NewGuid().ToString("N")[..6]);
        var seed = Path.Combine(root, "seed");
        Directory.CreateDirectory(seed);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        File.Copy(Path.Combine(repo, "assets", "rulesets", "geoip-ir.srs"), Path.Combine(seed, "geoip-ir.srs"));
        var manager = new RuleSetManager(Path.Combine(root, "cache"), new NeverDownloader(), NullLogger.Instance) { SeedDirectory = seed };

        var status = await manager.GetLocalAsync(RoutingPresets.GeoIp("ir"), Ct);

        status.Available.Should().BeTrue();
        status.UsedStaleCache.Should().BeTrue("the shipped copy is refreshed in the background");
        File.Exists(status.LocalPath).Should().BeTrue();
        Directory.Delete(root, true);
    }

    [Fact]
    public void Official_subscriptions_are_also_tried_through_the_Iran_entry()
    {
        JackiVpn.Domain.Subscriptions.OfficialProviders.IranEntryUrl("https://cloud.jackidrive.ir:2053/sub/abc123/")
            .Should().Be("https://sub.jackidrive.ir:2083/sub/abc123/");
        JackiVpn.Domain.Subscriptions.OfficialProviders.IranEntryUrl("https://cloud.jacki-inc.ir:2053/sub/t/sing_box")
            .Should().Be("https://sub.jacki-inc.ir:2083/sub/t/sing_box");
        JackiVpn.Domain.Subscriptions.OfficialProviders.IranEntryUrl("https://sub.jackidrive.ir:2083/sub/abc/").Should().BeNull("already the entry");
        JackiVpn.Domain.Subscriptions.OfficialProviders.IranEntryUrl("https://panel.example.com/sub/abc/").Should().BeNull("only official panels");
        JackiVpn.Domain.Subscriptions.OfficialProviders.IranEntryUrl("https://cloud.jackidrive.ir:2053/api/x").Should().BeNull("only subscription paths");
    }

    [Fact]
    public void Relay_configs_are_recognised_from_the_remark()
    {
        ConfigTraits.IsRelay(Link("vless://11111111-2222-4333-8444-555555555555@193.151.151.14:443?security=reality&pbk=x&sni=shop.jackidrive.ir#%F0%9F%87%AE%F0%9F%87%B7%20Relay%20%E2%9A%A1reality")).Should().BeTrue();
        ConfigTraits.IsRelay(Link("vless://11111111-2222-4333-8444-555555555555@154.211.2.164:8443?security=reality&pbk=x&sni=shop.jackidrive.ir#%E2%9A%A1reality")).Should().BeFalse();
    }

    [Fact]
    public void Servers_that_carried_traffic_are_remembered_per_network()
    {
        var now = DateTimeOffset.UtcNow;
        var insight = new ProfileInsight { WorkedOn = new Dictionary<string, DateTimeOffset> { ["cell:43211"] = now.AddDays(-1), ["wifi"] = now.AddDays(-30) } };

        insight.WorkedRecentlyOn("cell:43211", now).Should().BeTrue();
        insight.WorkedRecentlyOn("wifi", now).Should().BeFalse("older than two weeks");
        insight.WorkedRecentlyOn("cell:43235", now).Should().BeFalse();
    }

    private sealed class NeverDownloader : IRuleSetDownloader
    {
        public Task<RuleSetDownload> DownloadAsync(Uri url, string? etag, string? lastModified, long maxBytes, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("No network while connecting.");
    }
}
