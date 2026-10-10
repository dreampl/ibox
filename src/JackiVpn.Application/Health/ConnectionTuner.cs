using JackiVpn.Cores;
using JackiVpn.Cores.Capabilities;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Networking.Probes;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Health;

/// <summary>What will actually be used for one connection.</summary>
public sealed record TunedProfile(ProxyProfile Profile, bool MuxEnabled, FragmentVariant Fragment, bool EarlyData = false);

/// <summary>Result of measuring one variant on fresh connections (every request pays the full handshake).</summary>
public sealed record VariantMeasurement(double? MedianMs, int Successes, int Samples);

/// <summary>
/// Per-server handshake and multiplexing tuning. Every variant runs in an isolated probe core and is measured
/// with several requests on <b>new</b> connections, because the handshake (TCP + TLS + transport) is exactly
/// what differs between variants:
/// <list type="number">
/// <item>TLS fragmentation: TCP-segment fragmentation waits between segments; TLS record fragmentation does
/// not. Record-only is chosen when it is at least as reliable and not slower — the server's own setting is
/// kept otherwise, and the choice is dropped as soon as the server starts failing.</item>
/// <item>WebSocket early data (0-RTT): the first request travels inside the upgrade request, saving a round
/// trip per new connection — used only if the server proved to support it reliably and it is faster.</item>
/// <item>MUX: only when it works and is clearly faster (it saves handshakes but adds head-of-line blocking).</item>
/// </list>
/// Results are cached per configuration fingerprint for <see cref="ResultTtl"/>.
/// </summary>
public sealed class ConnectionTuner
{
    public const int SamplesPerVariant = 5;

    /// <summary>MUX must be at least this much faster to be recommended.</summary>
    public const double MuxRequiredImprovement = 0.10;

    /// <summary>Record fragmentation may be at most this much slower and still be preferred (it is lighter).</summary>
    public const double RecordTolerance = 0.03;

    /// <summary>Early data changes what the server must support, so it must be measurably faster.</summary>
    public const double EarlyDataRequiredImprovement = 0.03;

    private readonly CoreBinaryCatalog _catalog;
    private readonly ICoreEngine _singBox;
    private readonly ICoreEngine? _xray;
    private readonly IHealthHistoryRepository _repo;
    private readonly InsightStore _insights;
    private readonly Func<AppSettings> _settings;
    private readonly string _workDir;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionTuner(CoreBinaryCatalog catalog, ICoreEngine singBox, ICoreEngine? xray, IHealthHistoryRepository repo, InsightStore insights, Func<AppSettings> settings, string workDir, ILogger logger, TimeProvider? time = null)
    {
        _catalog = catalog;
        _singBox = singBox;
        _xray = xray;
        _repo = repo;
        _insights = insights;
        _settings = settings;
        _workDir = workDir;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public static TimeSpan ResultTtl { get; } = TimeSpan.FromDays(1);

    /// <summary>The current network (fixes found by EndpointRescue apply per network).</summary>
    public Func<string> NetworkKey { get; init; } = static () => "default";

    public static MuxMode EffectiveMuxMode(ProxyProfile profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        return profile.Mux?.Mode is { } m && m != MuxMode.Auto ? m : settings.Connection.Mux;
    }

    /// <summary>The profile and MUX decision to use now, from cached measurements (never blocks on a benchmark).</summary>
    public async Task<TunedProfile> PrepareAsync(ProxyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = _settings();
        var (tuned, fragment, earlyData) = Prepare(profile, settings);
        var mux = EffectiveMuxMode(profile, settings) switch
        {
            MuxMode.On => true,
            MuxMode.Off => false,
            _ => await _repo.GetMuxBenchmarkAsync(profile.Fingerprint, ct).ConfigureAwait(false) is { Recommended: true } r && Fresh(r.MeasuredUtc),
        };
        return new TunedProfile(tuned, mux, fragment, earlyData);
    }

    /// <summary>The profile exactly as a connection would use it (for health probes, so scores match reality).</summary>
    public ProxyProfile PrepareForProbe(ProxyProfile profile) => Prepare(profile, _settings()).Profile;

    private (ProxyProfile Profile, FragmentVariant Fragment, bool EarlyData) Prepare(ProxyProfile profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var tuned = ApplyFix(ProfileTuner.ApplyStatic(profile, settings.Performance), _insights.Get(profile.Fingerprint)?.FixFor(NetworkKey(), _time.GetUtcNow()));
        if (!settings.Performance.AutoTune || _insights.Get(profile.Fingerprint) is not { TunedUtc: { } at } insight || !Fresh(at))
        {
            return (tuned, FragmentVariant.AsConfigured, false);
        }

        var fragment = insight.Fragment ?? FragmentVariant.AsConfigured;
        var earlyData = insight.EarlyData == true && ProfileTuner.CanUseEarlyData(tuned);
        tuned = ProfileTuner.ApplyEarlyData(ProfileTuner.ApplyFragment(tuned, fragment), earlyData);
        return (tuned, fragment, earlyData);
    }

    /// <summary>Applies a learned fix: dial another address (name kept for SNI/Host) and/or another fingerprint.</summary>
    public static ProxyProfile ApplyFix(ProxyProfile profile, EndpointFix? fix)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (fix is null)
        {
            return profile;
        }

        var p = profile;
        if (fix.Address is { } address && System.Net.IPAddress.TryParse(address, out var ip))
        {
            p = Sessions.SessionPlanFactory.PinServerAddress(p, [ip]);
        }

        if (fix.Fingerprint is { Length: > 0 } fp && p.Tls.Enabled)
        {
            p = p with { Tls = p.Tls with { Fingerprint = fp } };
        }

        return p;
    }

    /// <summary>
    /// Tests several variants of servers in one probe core (one inbound each), one real request plus a 64 KB data
    /// check per variant. Returns the median-free single latency per variant, or null when it failed.
    /// </summary>
    public async Task<IReadOnlyList<double?>> ProbeVariantsAsync(IReadOnlyList<ProxyProfile> variants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(variants);
        var results = new double?[variants.Count];
        if (variants.Count == 0)
        {
            return results;
        }

        var settings = _settings();
        var (engine, caps) = await EngineForAsync(variants[0], settings, ct).ConfigureAwait(false);
        if (engine is null || caps is null)
        {
            return results;
        }

        var ports = PortAllocator.GetFreeLoopbackPorts(variants.Count);
        var plan = new CoreSessionPlan { Kind = SessionKind.ProbeBatch, Profiles = variants, ProbePorts = ports, Dns = settings.Dns };
        GeneratedCoreConfig config;
        try
        {
            config = engine.GenerateConfig(plan, caps);
            if (!(await engine.ValidateAsync(config, ct).ConfigureAwait(false)).IsValid)
            {
                return results;
            }
        }
        catch (CoreConfigException)
        {
            return results;
        }

        try
        {
            using var binary = _catalog.Verify(engine.Kind);
            await using var core = await CoreProcess.StartAsync(binary, engine.BuildLaunchSpec(_workDir), config, _logger, ct).ConfigureAwait(false);
            await core.WaitForPortAsync(ports[^1], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var url = Uri.TryCreate(settings.Probe.ProbeUrls.FirstOrDefault(), UriKind.Absolute, out var u) ? u : new Uri("https://www.gstatic.com/generate_204");
            Uri.TryCreate(settings.Probe.DataCheckUrl, UriKind.Absolute, out var data);
            await Task.WhenAll(variants.Select(async (_, i) =>
            {
                var proxy = new Uri($"socks5://127.0.0.1:{ports[i]}");
                var r = await ProxyHttpProbe.RunAsync(proxy, url, settings.Probe.Timeout, ct).ConfigureAwait(false);
                if (r is { Success: true, TotalMs: { } ms } && (data is null || await ProxyHttpProbe.ThroughputKbpsAsync(proxy, data, 64 * 1024, settings.Probe.Timeout, ct).ConfigureAwait(false) is not null))
                {
                    results[i] = ms;
                }
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CoreStartException or CoreIntegrityException)
        {
            _logger.LogWarning("Variant probe core failed: {Error}", ex.Message);
        }

        return results;
    }

    /// <summary>Whether any handshake variant applies to this profile.</summary>
    public static bool HasHandshakeVariants(ProxyProfile profile) =>
        ProfileTuner.CanUseRecordFragment(profile) || ProfileTuner.CanUseEarlyData(profile);

    public async Task<bool> NeedsTuningAsync(ProxyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = _settings();
        if (!settings.Performance.AutoTune)
        {
            return false;
        }

        if (HasHandshakeVariants(profile) && (_insights.Get(profile.Fingerprint)?.TunedUtc is not { } tunedAt || !Fresh(tunedAt)))
        {
            return true;
        }

        if (settings.Smart.MuxBenchmark && EffectiveMuxMode(profile, settings) == MuxMode.Auto)
        {
            var cached = await _repo.GetMuxBenchmarkAsync(profile.Fingerprint, ct).ConfigureAwait(false);
            return cached is null || !Fresh(cached.MeasuredUtc);
        }

        return false;
    }

    /// <summary>The server failed while using a tuned variant: forget the choice and use its own settings again.</summary>
    public void Invalidate(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var network = NetworkKey();
        if (_insights.Get(profile.Fingerprint)?.Fixes is { } fixes && fixes.ContainsKey(network))
        {
            // The way around stopped working too: forget it so the next rescue looks again.
            _insights.Update(profile.Fingerprint, i => i with { Fixes = i.Fixes?.Where(kv => kv.Key != network).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) });
        }

        if (_insights.Get(profile.Fingerprint) is { } i && (i.Fragment == FragmentVariant.RecordOnly || i.EarlyData == true))
        {
            _insights.Update(profile.Fingerprint, x => x with { Fragment = null, EarlyData = null, TunedUtc = null });
            _logger.LogInformation("Handshake tuning reset for a failing server; using its own settings.");
        }
    }

    /// <summary>Runs every applicable measurement for the profile and stores the results.</summary>
    public async Task TuneAsync(ProxyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var (engine, caps) = await EngineForAsync(profile, settings, ct).ConfigureAwait(false);
            if (engine is null || caps is null)
            {
                return;
            }

            var baseProfile = ProfileTuner.ApplyStatic(profile, settings.Performance);
            var chosen = baseProfile;
            VariantMeasurement? chosenMeasurement = null;
            if (settings.Performance.AutoTune && ProfileTuner.CanUseRecordFragment(profile) && caps.Supports(CoreFeature.TlsRecordFragment))
            {
                var configured = await MeasureAsync(engine, caps, baseProfile, mux: false, settings, ct).ConfigureAwait(false);
                var recordProfile = ProfileTuner.ApplyFragment(baseProfile, FragmentVariant.RecordOnly);
                var record = await MeasureAsync(engine, caps, recordProfile, mux: false, settings, ct).ConfigureAwait(false);
                var variant = ChooseFragment(configured, record);
                _insights.Update(profile.Fingerprint, i => i with
                {
                    Fragment = variant,
                    ConfiguredMedianMs = configured.MedianMs,
                    RecordMedianMs = record.MedianMs,
                    TunedUtc = _time.GetUtcNow(),
                });
                _logger.LogInformation(
                    "Handshake tuning: TCP fragment {Tcp} ({TcpOk}/{N}), record fragment {Rec} ({RecOk}/{N}) → {Choice}.",
                    Ms(configured.MedianMs), configured.Successes, SamplesPerVariant, Ms(record.MedianMs), record.Successes, SamplesPerVariant, variant);
                (chosen, chosenMeasurement) = variant == FragmentVariant.RecordOnly ? (recordProfile, record) : (baseProfile, configured);
            }

            if (settings.Performance.AutoTune && ProfileTuner.CanUseEarlyData(chosen))
            {
                var without = chosenMeasurement ?? await MeasureAsync(engine, caps, chosen, mux: false, settings, ct).ConfigureAwait(false);
                var withProfile = ProfileTuner.ApplyEarlyData(chosen, enabled: true);
                var with = await MeasureAsync(engine, caps, withProfile, mux: false, settings, ct).ConfigureAwait(false);
                var useEarlyData = ChooseEarlyData(without, with);
                _insights.Update(profile.Fingerprint, i => i with
                {
                    EarlyData = useEarlyData,
                    EarlyDataMedianMs = with.MedianMs,
                    WithoutEarlyDataMedianMs = without.MedianMs,
                    TunedUtc = _time.GetUtcNow(),
                });
                _logger.LogInformation(
                    "Handshake tuning: WebSocket 0-RTT {With} ({WithOk}/{N}) vs {Without} ({WithoutOk}/{N}) → {Choice}.",
                    Ms(with.MedianMs), with.Successes, SamplesPerVariant, Ms(without.MedianMs), without.Successes, SamplesPerVariant, useEarlyData ? "early data on" : "off");
                if (useEarlyData)
                {
                    chosen = withProfile;
                }
            }

            if (settings.Smart.MuxBenchmark && EffectiveMuxMode(profile, settings) == MuxMode.Auto)
            {
                await BenchmarkMuxCoreAsync(profile, chosen, engine, caps, settings, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Explicit MUX benchmark (Advanced page).</summary>
    public async Task<MuxBenchmarkResult> BenchmarkMuxAsync(ProxyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var (engine, caps) = await EngineForAsync(profile, settings, ct).ConfigureAwait(false);
            var tuned = (await PrepareAsync(profile, ct).ConfigureAwait(false)).Profile;
            return engine is null || caps is null
                ? await SaveMuxAsync(new MuxBenchmarkResult(profile.Fingerprint, false, null, null, false, _time.GetUtcNow()), ct).ConfigureAwait(false)
                : await BenchmarkMuxCoreAsync(profile, tuned, engine, caps, settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static FragmentVariant ChooseFragment(VariantMeasurement configured, VariantMeasurement record)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(record);
        var reliable = record.Successes >= Math.Max(record.Samples - 1, configured.Successes);
        var notSlower = configured.MedianMs is null || (record.MedianMs is { } r && r <= configured.MedianMs.Value * (1 + RecordTolerance));
        return reliable && notSlower && record.MedianMs is not null ? FragmentVariant.RecordOnly : FragmentVariant.AsConfigured;
    }

    public static bool ChooseEarlyData(VariantMeasurement without, VariantMeasurement with)
    {
        ArgumentNullException.ThrowIfNull(without);
        ArgumentNullException.ThrowIfNull(with);
        var reliable = with.Successes >= Math.Max(with.Samples - 1, without.Successes);
        var faster = without.MedianMs is null || (with.MedianMs is { } w && w <= without.MedianMs.Value * (1 - EarlyDataRequiredImprovement));
        return reliable && faster && with.MedianMs is not null;
    }

    public static bool ShouldUseMux(VariantMeasurement withMux, VariantMeasurement withoutMux)
    {
        ArgumentNullException.ThrowIfNull(withMux);
        ArgumentNullException.ThrowIfNull(withoutMux);
        var works = withMux.Successes >= Math.Ceiling(withMux.Samples * 0.6);
        if (!works)
        {
            return false;
        }

        return withoutMux.MedianMs is null
            ? withMux.MedianMs is not null
            : withMux.MedianMs is { } w && w <= withoutMux.MedianMs.Value * (1 - MuxRequiredImprovement) && withMux.Successes >= withoutMux.Successes;
    }

    private async Task<MuxBenchmarkResult> BenchmarkMuxCoreAsync(ProxyProfile original, ProxyProfile tuned, ICoreEngine engine, CoreCapabilities caps, AppSettings settings, CancellationToken ct)
    {
        if (!CapabilityMatrix.MuxApplicable(tuned, caps))
        {
            return await SaveMuxAsync(new MuxBenchmarkResult(original.Fingerprint, false, null, null, false, _time.GetUtcNow()), ct).ConfigureAwait(false);
        }

        var without = await MeasureAsync(engine, caps, tuned, mux: false, settings, ct).ConfigureAwait(false);
        var with = await MeasureAsync(engine, caps, tuned, mux: true, settings, ct).ConfigureAwait(false);
        var recommended = ShouldUseMux(with, without);
        _logger.LogInformation("MUX benchmark: with {With} ({WithOk}/{N}), without {Without} ({WithoutOk}/{N}) → {Decision}.",
            Ms(with.MedianMs), with.Successes, SamplesPerVariant, Ms(without.MedianMs), without.Successes, SamplesPerVariant, recommended ? "MUX on" : "MUX off");
        return await SaveMuxAsync(new MuxBenchmarkResult(original.Fingerprint, with.Successes >= Math.Ceiling(SamplesPerVariant * 0.6), with.MedianMs, without.MedianMs, recommended, _time.GetUtcNow()), ct).ConfigureAwait(false);
    }

    private async Task<MuxBenchmarkResult> SaveMuxAsync(MuxBenchmarkResult result, CancellationToken ct)
    {
        await _repo.SaveMuxBenchmarkAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(ICoreEngine? Engine, CoreCapabilities? Caps)> EngineForAsync(ProxyProfile profile, AppSettings settings, CancellationToken ct)
    {
        var sbCaps = await _singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var xrCaps = _xray is null ? null : await _xray.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var selection = CoreSelector.Select(profile, settings.Connection.Core, SessionKind.ProbeBatch, false, sbCaps, xrCaps);
        if (!selection.IsUsable)
        {
            return (null, null);
        }

        return selection.Core == CoreKind.Xray ? (_xray, xrCaps) : (_singBox, sbCaps);
    }

    private async Task<VariantMeasurement> MeasureAsync(ICoreEngine engine, CoreCapabilities caps, ProxyProfile profile, bool mux, AppSettings settings, CancellationToken ct)
    {
        var failed = new VariantMeasurement(null, 0, SamplesPerVariant);
        var ports = PortAllocator.GetFreeLoopbackPorts(1);
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.ProbeBatch,
            Profiles = [profile],
            ProbePorts = ports,
            Dns = settings.Dns,
            MuxEnabled = mux,
        };

        GeneratedCoreConfig config;
        try
        {
            config = engine.GenerateConfig(plan, caps);
            if (!(await engine.ValidateAsync(config, ct).ConfigureAwait(false)).IsValid)
            {
                return failed;
            }
        }
        catch (CoreConfigException)
        {
            return failed;
        }

        try
        {
            using var binary = _catalog.Verify(engine.Kind);
            await using var core = await CoreProcess.StartAsync(binary, engine.BuildLaunchSpec(_workDir), config, _logger, ct).ConfigureAwait(false);
            await core.WaitForPortAsync(ports[0], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var proxy = new Uri($"socks5://127.0.0.1:{ports[0]}");
            var urls = settings.Probe.ProbeUrls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x : null).OfType<Uri>().ToList();
            if (urls.Count == 0)
            {
                return failed;
            }

            // Warm-up (server name resolution, route setup) is not counted for any variant.
            await ProxyHttpProbe.RunAsync(proxy, urls[0], settings.Probe.Timeout, ct).ConfigureAwait(false);
            var times = new List<double>();
            for (var i = 0; i < SamplesPerVariant; i++)
            {
                var r = await ProxyHttpProbe.RunAsync(proxy, urls[i % urls.Count], settings.Probe.Timeout, ct).ConfigureAwait(false);
                if (r is { Success: true, TotalMs: { } ms })
                {
                    times.Add(ms);
                }
            }

            return new VariantMeasurement(Smart.HealthStatistics.Median(times), times.Count, SamplesPerVariant);
        }
        catch (Exception ex) when (ex is CoreStartException or CoreIntegrityException)
        {
            _logger.LogWarning("Tuning core failed: {Error}", ex.Message);
            return failed;
        }
    }

    private bool Fresh(DateTimeOffset measuredUtc) => _time.GetUtcNow() - measuredUtc < ResultTtl;

    private static string Ms(double? ms) => ms is { } v ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "n/a";
}
