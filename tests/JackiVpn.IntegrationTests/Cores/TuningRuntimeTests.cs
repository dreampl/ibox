using FluentAssertions;
using JackiVpn.Application.Health;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;
using JackiVpn.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>
/// Handshake tuning against a real sing-box VLESS+WS+TLS server on loopback: both fragmentation variants are
/// measured on fresh connections through real cores, and the faster reliable one is chosen and stored.
/// </summary>
public sealed class TuningRuntimeTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private const string Sni = "edge.jacki-example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tuner_measures_both_fragment_variants_and_prefers_the_faster_reliable_one()
    {
        cores.RequireCores();
        using var target = NetHelpers.StartTarget();
        using var cert = new TestCertificate(cores.WorkDir, Sni);
        var port = NetHelpers.FreePort();
        await using var server = await cores.StartRawAsync(CoreKind.SingBox, NetHelpers.VlessWsTlsServerConfig(port, Uuid, Sni, "/jacki-ws", cert));
        await server.WaitForPortAsync(port, TimeSpan.FromSeconds(15), Ct);

        var reference = CoreFixture.ReferenceProfile();
        var profile = reference with { Server = "127.0.0.1", Port = port, Tls = reference.Tls with { AllowInsecure = true } };
        var settings = new AppSettings
        {
            Probe = new ProbeSettings { ProbeUrls = [$"http://127.0.0.1:{NetHelpers.PortOf(target)}/probe"], Timeout = TimeSpan.FromSeconds(8) },
            Smart = new SmartSettings { MuxBenchmark = false },
        };
        var insights = new InsightStore(Path.Combine(cores.WorkDir, $"insights-{Guid.NewGuid():N}.json"));
        var tuner = new ConnectionTuner(cores.Catalog, cores.SingBox, cores.Xray, new MemoryHealthRepo(), insights, () => settings, cores.WorkDir, NullLogger.Instance);

        (await tuner.NeedsTuningAsync(profile, Ct)).Should().BeTrue("the profile uses TCP fragmentation and has no measurement yet");
        await tuner.TuneAsync(profile, Ct);

        var insight = insights.Get(profile.Fingerprint);
        insight.Should().NotBeNull();
        insight!.ConfiguredMedianMs.Should().NotBeNull("the configured variant must carry traffic through the real server");
        insight.RecordMedianMs.Should().NotBeNull("the record-fragment variant must carry traffic through the real server");
        TestContext.Current.TestOutputHelper?.WriteLine($"TCP fragment median {insight.ConfiguredMedianMs:0.0} ms, record fragment median {insight.RecordMedianMs:0.0} ms → {insight.Fragment}");
        insight.Fragment.Should().Be(ConnectionTuner.ChooseFragment(
            new VariantMeasurement(insight.ConfiguredMedianMs, ConnectionTuner.SamplesPerVariant, ConnectionTuner.SamplesPerVariant),
            new VariantMeasurement(insight.RecordMedianMs, ConnectionTuner.SamplesPerVariant, ConnectionTuner.SamplesPerVariant)));

        var prepared = await tuner.PrepareAsync(profile, Ct);
        prepared.Fragment.Should().Be(insight.Fragment!.Value);
        (await tuner.NeedsTuningAsync(profile, Ct)).Should().BeFalse("results are cached");

        tuner.Invalidate(profile);
        (await tuner.PrepareAsync(profile, Ct)).Fragment.Should().Be(FragmentVariant.AsConfigured, "a failing tuned server falls back to its own settings");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Early_data_is_only_used_when_the_server_supports_it(bool serverSupportsEarlyData)
    {
        cores.RequireCores();
        using var target = NetHelpers.StartTarget();
        using var cert = new TestCertificate(cores.WorkDir, Sni);
        var port = NetHelpers.FreePort();
        await using var server = await cores.StartRawAsync(CoreKind.SingBox, NetHelpers.VlessWsTlsServerConfig(port, Uuid, Sni, "/jacki-ws", cert, serverSupportsEarlyData));
        await server.WaitForPortAsync(port, TimeSpan.FromSeconds(15), Ct);

        var reference = CoreFixture.ReferenceProfile();
        var profile = reference with
        {
            Server = "127.0.0.1",
            Port = port,
            Tls = reference.Tls with { AllowInsecure = true, Fragment = null },
            Transport = reference.Transport with { MaxEarlyData = null, EarlyDataHeaderName = null },
        };
        ProfileTuner.CanUseEarlyData(profile).Should().BeTrue();
        var settings = new AppSettings
        {
            Probe = new ProbeSettings { ProbeUrls = [$"http://127.0.0.1:{NetHelpers.PortOf(target)}/probe"], Timeout = TimeSpan.FromSeconds(4) },
            Smart = new SmartSettings { MuxBenchmark = false },
        };
        var insights = new InsightStore(Path.Combine(cores.WorkDir, $"insights-{Guid.NewGuid():N}.json"));
        var tuner = new ConnectionTuner(cores.Catalog, cores.SingBox, cores.Xray, new MemoryHealthRepo(), insights, () => settings, cores.WorkDir, NullLogger.Instance);

        await tuner.TuneAsync(profile, Ct);
        var insight = insights.Get(profile.Fingerprint)!;
        TestContext.Current.TestOutputHelper?.WriteLine($"server early data={serverSupportsEarlyData}: with 0-RTT {insight.EarlyDataMedianMs:0.0} ms, without {insight.WithoutEarlyDataMedianMs:0.0} ms → {insight.EarlyData}");

        insight.WithoutEarlyDataMedianMs.Should().NotBeNull("the normal handshake always works");
        if (serverSupportsEarlyData)
        {
            insight.EarlyDataMedianMs.Should().NotBeNull("0-RTT requests must succeed through a server that supports them");
        }
        else
        {
            insight.EarlyData.Should().BeFalse("a server without early-data support must never get 0-RTT connections");
            (await tuner.PrepareAsync(profile, Ct)).EarlyData.Should().BeFalse();
        }
    }

    private sealed class MemoryHealthRepo : IHealthHistoryRepository
    {
        private readonly Dictionary<string, MuxBenchmarkResult> _mux = new(StringComparer.Ordinal);

        public Task AppendAsync(string profileId, HealthSample sample, CancellationToken ct) => Task.CompletedTask;

        public Task AppendManyAsync(IReadOnlyList<(string ProfileId, HealthSample Sample)> samples, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<HealthSample>> GetRecentAsync(string profileId, int maxCount, CancellationToken ct) => Task.FromResult<IReadOnlyList<HealthSample>>([]);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<HealthSample>>> GetRecentForAllAsync(int maxPerProfile, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<HealthSample>>>(new Dictionary<string, IReadOnlyList<HealthSample>>());

        public Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken ct) => Task.CompletedTask;

        public Task SaveMuxBenchmarkAsync(MuxBenchmarkResult result, CancellationToken ct)
        {
            _mux[result.ProfileFingerprint] = result;
            return Task.CompletedTask;
        }

        public Task<MuxBenchmarkResult?> GetMuxBenchmarkAsync(string profileFingerprint, CancellationToken ct) =>
            Task.FromResult(_mux.TryGetValue(profileFingerprint, out var r) ? r : null);
    }
}
