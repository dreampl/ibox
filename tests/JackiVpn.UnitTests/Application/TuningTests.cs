using FluentAssertions;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Application;

public class TuningTests
{
    private static ProxyProfile UserProfile() => Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0];

    [Fact]
    public void Users_sample_config_keeps_its_own_fingerprint_and_alpn()
    {
        var p = UserProfile();
        var tuned = ProfileTuner.ApplyStatic(p, new PerformanceSettings());

        tuned.Tls.Fingerprint.Should().Be("firefox");
        tuned.Tls.Alpn.Should().Equal("http/1.1");
        tuned.Should().BeSameAs(p, "nothing needed changing");
    }

    [Fact]
    public void Missing_fingerprint_gets_the_default_browser_hello()
    {
        var p = UserProfile() with { Tls = UserProfile().Tls with { Fingerprint = null } };
        ProfileTuner.ApplyStatic(p, new PerformanceSettings()).Tls.Fingerprint.Should().Be("chrome");
        ProfileTuner.ApplyStatic(p, new PerformanceSettings { DefaultFingerprint = "" }).Tls.Fingerprint.Should().BeNull();
        ProfileTuner.ApplyStatic(p, new PerformanceSettings { DefaultFingerprint = "not-a-browser" }).Tls.Fingerprint.Should().BeNull();
    }

    [Fact]
    public void Websocket_never_offers_h2()
    {
        var p = UserProfile() with { Tls = UserProfile().Tls with { Alpn = ["h2", "http/1.1"] } };
        ProfileTuner.ApplyStatic(p, new PerformanceSettings()).Tls.Alpn.Should().Equal("http/1.1");

        var onlyH2 = UserProfile() with { Tls = UserProfile().Tls with { Alpn = ["h2"] } };
        ProfileTuner.ApplyStatic(onlyH2, new PerformanceSettings()).Tls.Alpn.Should().Equal("http/1.1");
    }

    [Fact]
    public void Grpc_keeps_h2()
    {
        var p = UserProfile() with
        {
            Transport = new TransportOptions { Type = TransportType.Grpc, ServiceName = "g" },
            Tls = UserProfile().Tls with { Alpn = ["h2"] },
        };
        ProfileTuner.ApplyStatic(p, new PerformanceSettings()).Tls.Alpn.Should().Equal("h2");
    }

    [Fact]
    public void Tuning_keeps_identity_fingerprint_so_caches_stay_valid()
    {
        var p = UserProfile() with { Tls = UserProfile().Tls with { Fingerprint = null } };
        var tuned = ProfileTuner.ApplyFragment(ProfileTuner.ApplyStatic(p, new PerformanceSettings()), FragmentVariant.RecordOnly);
        tuned.Fingerprint.Should().Be(p.Fingerprint);
        tuned.Id.Should().Be(p.Id);
    }

    [Fact]
    public void Record_fragment_variant_replaces_tcp_fragmentation()
    {
        var p = UserProfile();
        ProfileTuner.CanUseRecordFragment(p).Should().BeTrue();
        var tuned = ProfileTuner.ApplyFragment(p, FragmentVariant.RecordOnly);

        tuned.Tls.Fragment!.Enabled.Should().BeFalse();
        tuned.Tls.Fragment.RecordFragment.Should().BeTrue();
        ProfileValidator.Validate(tuned).IsValid.Should().BeTrue();
        ProfileTuner.ApplyFragment(p, FragmentVariant.AsConfigured).Should().BeSameAs(p);
    }

    [Fact]
    public void Profiles_without_fragmentation_are_never_changed_by_fragment_tuning()
    {
        var p = UserProfile() with { Tls = UserProfile().Tls with { Fragment = null } };
        ProfileTuner.CanUseRecordFragment(p).Should().BeFalse();
        ProfileTuner.ApplyFragment(p, FragmentVariant.RecordOnly).Should().BeSameAs(p);
    }

    [Theory]
    [InlineData(5, 300.0, 5, 250.0, FragmentVariant.RecordOnly)]   // faster and reliable
    [InlineData(5, 300.0, 5, 305.0, FragmentVariant.RecordOnly)]   // within tolerance: lighter variant wins
    [InlineData(5, 300.0, 5, 340.0, FragmentVariant.AsConfigured)] // clearly slower
    [InlineData(5, 300.0, 3, 200.0, FragmentVariant.AsConfigured)] // faster but unreliable
    [InlineData(0, null, 4, 400.0, FragmentVariant.RecordOnly)]    // configured variant fails entirely
    [InlineData(5, 300.0, 0, null, FragmentVariant.AsConfigured)]
    public void Fragment_choice_prefers_reliable_and_not_slower(int confOk, double? confMs, int recOk, double? recMs, FragmentVariant expected) =>
        ConnectionTuner.ChooseFragment(new VariantMeasurement(confMs, confOk, 5), new VariantMeasurement(recMs, recOk, 5)).Should().Be(expected);

    [Theory]
    [InlineData(5, 400.0, 5, 200.0, true)]   // one round trip saved
    [InlineData(5, 400.0, 5, 395.0, false)]  // not measurably faster
    [InlineData(5, 400.0, 3, 200.0, false)]  // unreliable: server probably ignores early data
    [InlineData(5, 400.0, 0, null, false)]   // server does not support it at all
    public void Early_data_only_when_reliable_and_faster(int withoutOk, double withoutMs, int withOk, double? withMs, bool expected) =>
        ConnectionTuner.ChooseEarlyData(new VariantMeasurement(withoutMs, withoutOk, 5), new VariantMeasurement(withMs, withOk, 5)).Should().Be(expected);

    [Fact]
    public void Early_data_applies_only_to_websocket_configs_without_it()
    {
        var p = UserProfile() with { Transport = UserProfile().Transport with { MaxEarlyData = null } };
        ProfileTuner.CanUseEarlyData(p).Should().BeTrue();
        var tuned = ProfileTuner.ApplyEarlyData(p, enabled: true);
        tuned.Transport.MaxEarlyData.Should().Be(ProfileTuner.EarlyDataBytes);
        tuned.Transport.EarlyDataHeaderName.Should().Be("Sec-WebSocket-Protocol");
        ProfileValidator.Validate(tuned).IsValid.Should().BeTrue();

        ProfileTuner.ApplyEarlyData(p, enabled: false).Should().BeSameAs(p);
        var grpc = p with { Transport = new TransportOptions { Type = TransportType.Grpc, ServiceName = "g" } };
        ProfileTuner.CanUseEarlyData(grpc).Should().BeFalse();
        var configured = p with { Transport = p.Transport with { MaxEarlyData = 4096 } };
        ProfileTuner.ApplyEarlyData(configured, enabled: true).Transport.MaxEarlyData.Should().Be(4096, "the server's own setting wins");
    }

    [Theory]
    [InlineData(5, 200, 5, 300, true)]
    [InlineData(5, 280, 5, 300, false)] // < 10 % better
    [InlineData(2, 100, 5, 300, false)] // unreliable with MUX
    [InlineData(4, 200, 5, 300, false)] // fewer successes than without
    public void Mux_only_when_reliable_and_clearly_faster(int withOk, double withMs, int withoutOk, double withoutMs, bool expected) =>
        ConnectionTuner.ShouldUseMux(new VariantMeasurement(withMs, withOk, 5), new VariantMeasurement(withoutMs, withoutOk, 5)).Should().Be(expected);

    [Fact]
    public void Cloudflare_trace_gives_ip_and_country()
    {
        const string trace = "fl=123f45\nh=www.cloudflare.com\nip=172.86.111.133\nts=1759300000.1\nvisit_scheme=https\nuag=x\ncolo=EWR\nsliver=none\nhttp=http/2\nloc=US\ntls=TLSv1.3\n";
        HttpLiveProbe.ParseTrace(trace).Should().Be(("172.86.111.133", "US"));
        HttpLiveProbe.ParseTrace("ip=not-an-ip\nloc=XX\n").Should().Be(((string?)null, (string?)null));
        HttpLiveProbe.ParseTrace("ip=2001:db8::1\nloc=de\n").Should().Be(("2001:db8::1", "DE"));
    }

    [Fact]
    public void Geo_details_are_optional_and_bounded()
    {
        HttpLiveProbe.ParseGeo("""{"success":true,"city":"New York","country_code":"US","connection":{"isp":"Cloudzy"}}""")
            .Should().Be(("New York", "Cloudzy", "US"));
        HttpLiveProbe.ParseGeo("""{"success":false,"message":"limit"}""").Should().Be(((string?)null, (string?)null, (string?)null));
        HttpLiveProbe.CountryName("US").Should().Be("United States");
        HttpLiveProbe.CountryName(null).Should().BeNull();
    }

    [Fact]
    public void Random_proxy_port_is_upgraded_to_the_stable_default_but_a_chosen_port_is_kept()
    {
        var random = new AppSettings { SchemaVersion = 2, Connection = new ConnectionSettings { LocalProxyPort = 0 } };
        SettingsMigrations.Upgrade(random).Connection.LocalProxyPort.Should().Be(ConnectionSettings.DefaultLocalProxyPort);

        var chosen = new AppSettings { SchemaVersion = 2, Connection = new ConnectionSettings { LocalProxyPort = 2080 } };
        SettingsMigrations.Upgrade(chosen).Connection.LocalProxyPort.Should().Be(2080);
        new AppSettings().Connection.LocalProxyPort.Should().Be(10808);
    }

    [Fact]
    public void V1_settings_are_upgraded_to_latency_defaults_without_touching_custom_weights()
    {
        var v1 = new AppSettings
        {
            SchemaVersion = 1,
            Dns = new DnsSettings { FakeIpEnabled = false },
            Probe = new ProbeSettings { CheckPublicIp = false, PublicIpUrl = "https://api.ipify.org" },
            Smart = new SmartSettings { Weights = new ScoreWeights { Latency = 0.30, Jitter = 0.15, Handshake = 0.10, Stability = 0.15 } },
        };
        var up = SettingsMigrations.Upgrade(v1);
        up.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        up.Dns.FakeIpEnabled.Should().BeTrue();
        up.Probe.CheckPublicIp.Should().BeTrue();
        up.Probe.PublicIpUrl.Should().Contain("cdn-cgi/trace");
        up.Smart.Weights.Latency.Should().Be(0.35);

        var custom = v1 with { Smart = new SmartSettings { Weights = new ScoreWeights { Latency = 0.6 } } };
        SettingsMigrations.Upgrade(custom).Smart.Weights.Latency.Should().Be(0.6);
        SettingsMigrations.Upgrade(up).Should().Be(up, "upgrading is idempotent");
    }
}
