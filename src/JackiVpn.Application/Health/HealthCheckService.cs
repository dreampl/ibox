using System.Collections.Concurrent;
using JackiVpn.Application.Smart;
using JackiVpn.Cores;
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

public sealed record ProbeRequestOptions
{
    /// <summary>Ignore the cache TTL and probe every node.</summary>
    public bool Force { get; init; }

    public int? Samples { get; init; }
    public bool? Throughput { get; init; }
    public bool ProbeIpv6 { get; init; }

    /// <summary>
    /// Fast screening pass for smart connect: one request per node, short timeout, no separate direct
    /// DNS/TCP/TLS probe. Followed by a full measurement of the best few.
    /// </summary>
    public bool Quick { get; init; }
}

public sealed record ProbeProgress(string ProfileId, NodeHealthSnapshot Snapshot, int Completed, int Total);

public sealed record Ipv6ProbeResult(string ProfileId, bool Supported);

/// <summary>
/// Multi-stage node health checks: config validity → direct DNS/TCP/TLS → core startup → proxy handshake
/// → real HTTP requests through the proxy (several samples, alternating probe endpoints so one external
/// outage cannot fail every node). Nodes are tested in batches through one core process (one loopback
/// inbound per node) with bounded concurrency, cancellable at every step, results cached with a TTL.
/// </summary>
public sealed class HealthCheckService
{
    public const int AbsoluteMaxConcurrency = 6;

    private readonly CoreBinaryCatalog _catalog;
    private readonly ICoreEngine _singBox;
    private readonly ICoreEngine? _xray;
    private readonly IHealthHistoryRepository _repo;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string, int> _recentDisconnects;
    private readonly string _workDir;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, NodeHealthSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _ipv6 = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public HealthCheckService(
        CoreBinaryCatalog catalog,
        ICoreEngine singBox,
        ICoreEngine? xray,
        IHealthHistoryRepository repo,
        Func<AppSettings> settings,
        Func<string, int> recentDisconnects,
        string workDir,
        ILogger logger,
        TimeProvider? time = null)
    {
        _catalog = catalog;
        _singBox = singBox;
        _xray = xray;
        _repo = repo;
        _settings = settings;
        _recentDisconnects = recentDisconnects;
        _workDir = workDir;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public static TimeSpan QuickTimeout { get; } = TimeSpan.FromSeconds(4);

    /// <summary>Size of the real-data check: well past the ~20 KB after which throttled paths freeze.</summary>
    public const long DataCheckBytes = 64 * 1024;

    /// <summary>Maps a stored profile to what a real connection would use (fingerprint, tuned handshake).</summary>
    public Func<ProxyProfile, ProxyProfile>? PrepareProfile { get; set; }

    public event EventHandler<ProbeProgress>? Progress;

    public bool IsRunning => _runGate.CurrentCount == 0;

    public NodeHealthSnapshot Snapshot(string profileId) =>
        _snapshots.TryGetValue(profileId, out var s) ? s : NodeHealthSnapshot.Unknown(profileId);

    public IReadOnlyCollection<NodeHealthSnapshot> AllSnapshots => _snapshots.Values.ToArray();

    public bool? SupportsIpv6(string profileId) => _ipv6.TryGetValue(profileId, out var v) ? v : null;

    /// <summary>Loads persisted history so scores survive restarts.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var settings = _settings();
        IReadOnlyDictionary<string, IReadOnlyList<HealthSample>> history;
        try
        {
            history = await _repo.GetRecentForAllAsync(settings.Probe.HistoryWindow, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Health history is only a cache of measurements: never a reason not to start.
            _logger.LogWarning("Health history unavailable, starting without it: {Error}", ex.Message);
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var (id, samples) in history)
        {
            _snapshots[id] = HealthStatistics.Compute(id, samples, _recentDisconnects(id), settings.Smart.Weights, now, settings.Probe.HistoryWindow);
        }
    }

    public async Task<IReadOnlyList<NodeHealthSnapshot>> TestAsync(IReadOnlyList<ProxyProfile> profiles, ProbeRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(options);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var now = _time.GetUtcNow();
            var due = profiles
                .Where(p => p.SyncState != ProfileSyncState.RemovedByProvider)
                .Where(p => options.Force || !_snapshots.TryGetValue(p.Id, out var s) || s.LastTestedUtc is null || now - s.LastTestedUtc > settings.Probe.CacheTtl)
                .ToList();
            var total = due.Count;
            var completed = 0;
            void Report(string id)
            {
                var c = Interlocked.Increment(ref completed);
                Progress?.Invoke(this, new ProbeProgress(id, Snapshot(id), c, total));
            }

            var singBoxCaps = await _singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            var xrayCaps = _xray is null ? null : await _xray.GetCapabilitiesAsync(ct).ConfigureAwait(false);

            var sbGroup = new List<ProxyProfile>();
            var xrGroup = new List<ProxyProfile>();
            var dnsGroup = new List<ProxyProfile>();
            var pinned = await Task.WhenAll(due.Select(p => Connectivity.SecureDns.Shared.PinIfLocallyBlockedAsync(p, ct))).ConfigureAwait(false);
            foreach (var p in pinned)
            {
                var validation = ProfileValidator.Validate(p);
                if (!validation.IsValid)
                {
                    await RecordAsync(p.Id, Failure(ProbeFailureKind.InvalidConfig, validation.Errors.First().Message), settings, ct).ConfigureAwait(false);
                    Report(p.Id);
                    continue;
                }

                if (DnsTunnel.IsDnsTunnel(p))
                {
                    dnsGroup.Add(p);
                    continue;
                }

                var selection = CoreSelector.Select(p, settings.Connection.Core, SessionKind.ProbeBatch, false, singBoxCaps, xrayCaps);
                if (!selection.IsUsable)
                {
                    await RecordAsync(p.Id, Failure(ProbeFailureKind.Unsupported, selection.Reason), settings, ct).ConfigureAwait(false);
                    Report(p.Id);
                    continue;
                }

                (selection.Core == CoreKind.Xray ? xrGroup : sbGroup).Add(p);
            }

            var batchSize = Math.Clamp(settings.Probe.BatchSize, 1, 64);
            foreach (var batch in sbGroup.Chunk(batchSize))
            {
                await RunBatchAsync(_singBox, singBoxCaps, batch, options, settings, Report, ct).ConfigureAwait(false);
            }

            if (_xray is not null && xrayCaps is not null)
            {
                foreach (var batch in xrGroup.Chunk(batchSize))
                {
                    await RunBatchAsync(_xray, xrayCaps, batch, options, settings, Report, ct).ConfigureAwait(false);
                }
            }

            foreach (var p in dnsGroup)
            {
                await RunDnsTunnelAsync(p, singBoxCaps, options, settings, Report, ct).ConfigureAwait(false);
            }

            return profiles.Select(p => Snapshot(p.Id)).ToArray();
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// A DNS tunnel server: its own dnstt client for the test, then the usual sing-box probe through it. Slow by
    /// nature (every byte is a DNS query), so it gets a longer timeout and no direct TCP/TLS timing.
    /// </summary>
    private async Task RunDnsTunnelAsync(ProxyProfile profile, CoreCapabilities caps, ProbeRequestOptions options, AppSettings settings, Action<string> report, CancellationToken ct)
    {
        if (!_catalog.IsInstalled(CoreKind.Dnstt))
        {
            await RecordAsync(profile.Id, Failure(ProbeFailureKind.Unsupported, "The DNS tunnel client is not installed with this version of the app."), settings, ct).ConfigureAwait(false);
            report(profile.Id);
            return;
        }

        var resolver = await DnsTunnel.PickResolverAsync(_catalog, profile, _logger, ct).ConfigureAwait(false);
        if (resolver is null)
        {
            await RecordAsync(profile.Id, Failure(ProbeFailureKind.Timeout, "No DNS resolver reached the tunnel on this network."), settings, ct).ConfigureAwait(false);
            report(profile.Id);
            return;
        }

        var port = PortAllocator.GetFreeLoopbackPorts(1)[0];
        CoreProcess? client = null;
        try
        {
            client = await DnsTunnel.StartAsync(_catalog, DnsTunnel.Plan(_catalog, profile, port, resolver), _workDir, _logger, ct, background: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CoreStartException or CoreIntegrityException or ArgumentException)
        {
            await RecordAsync(profile.Id, Failure(ProbeFailureKind.CoreStartFailed, ex.Message), settings, ct).ConfigureAwait(false);
            report(profile.Id);
            return;
        }

        try
        {
            await RunBatchAsync(_singBox, caps, [DnsTunnel.AsLocalSocks(profile, port)], options, settings, report, ct, dnsTunnel: true).ConfigureAwait(false);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunBatchAsync(ICoreEngine engine, CoreCapabilities caps, ProxyProfile[] batch, ProbeRequestOptions options, AppSettings settings, Action<string> report, CancellationToken ct, bool dnsTunnel = false)
    {
        var ports = PortAllocator.GetFreeLoopbackPorts(batch.Length);
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.ProbeBatch,
            Profiles = PrepareProfile is { } prepare ? batch.Select(prepare).ToArray() : batch,
            ProbePorts = ports,
            Dns = settings.Dns,
        };

        GeneratedCoreConfig config;
        try
        {
            config = engine.GenerateConfig(plan, caps);
            var validation = await engine.ValidateAsync(config, ct).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                if (batch.Length > 1)
                {
                    // Isolate the broken profile(s) instead of failing the whole batch.
                    foreach (var single in batch)
                    {
                        await RunBatchAsync(engine, caps, [single], options, settings, report, ct, dnsTunnel).ConfigureAwait(false);
                    }

                    return;
                }

                await RecordAsync(batch[0].Id, Failure(ProbeFailureKind.InvalidConfig, validation.Summary), settings, ct).ConfigureAwait(false);
                report(batch[0].Id);
                return;
            }
        }
        catch (CoreConfigException ex)
        {
            foreach (var p in batch)
            {
                await RecordAsync(p.Id, Failure(ProbeFailureKind.Unsupported, ex.Message), settings, ct).ConfigureAwait(false);
                report(p.Id);
            }

            return;
        }

        CoreProcess? core = null;
        try
        {
            using var binary = _catalog.Verify(engine.Kind);
            core = await CoreProcess.StartAsync(binary, engine.BuildLaunchSpec(_workDir), config, _logger, ct, background: true).ConfigureAwait(false);
            await core.WaitForPortAsync(ports[^1], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CoreStartException or CoreIntegrityException)
        {
            foreach (var p in batch)
            {
                await RecordAsync(p.Id, Failure(ProbeFailureKind.CoreStartFailed, ex.Message), settings, ct).ConfigureAwait(false);
                report(p.Id);
            }

            if (core is not null)
            {
                await core.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }
        catch (OperationCanceledException)
        {
            // Cancelled while the probe core was starting (e.g. the user cancelled a connect): never leave it running.
            if (core is not null)
            {
                await core.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        try
        {
            var concurrency = Math.Clamp(settings.Probe.MaxConcurrency, 1, AbsoluteMaxConcurrency);
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var tasks = batch.Select(async (profile, i) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var samples = await ProbeOneAsync(profile, new Uri($"socks5://127.0.0.1:{ports[i]}"), options, settings, ct, dnsTunnel).ConfigureAwait(false);
                    foreach (var s in samples)
                    {
                        await RecordAsync(profile.Id, s, settings, ct).ConfigureAwait(false);
                    }

                    if (options.ProbeIpv6 && Uri.TryCreate(settings.Probe.Ipv6ProbeUrl, UriKind.Absolute, out var v6Url))
                    {
                        var v6 = await ProxyHttpProbe.RunAsync(new Uri($"socks5://127.0.0.1:{ports[i]}"), v6Url, settings.Probe.Timeout, ct).ConfigureAwait(false);
                        _ipv6[profile.Id] = v6.Success;
                    }
                }
                finally
                {
                    gate.Release();
                    report(profile.Id);
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            await core.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<HealthSample>> ProbeOneAsync(ProxyProfile p, Uri proxy, ProbeRequestOptions options, AppSettings settings, CancellationToken ct, bool dnsTunnel = false)
    {
        var samples = new List<HealthSample>();
        var timeout = dnsTunnel
            ? (settings.Probe.Timeout > DnsTunnel.ProbeTimeout ? settings.Probe.Timeout : DnsTunnel.ProbeTimeout)
            : options.Quick && settings.Probe.Timeout > QuickTimeout ? QuickTimeout : settings.Probe.Timeout;
        DirectProbeResult? direct = null;
        if (!p.IsUdpBasedProtocol && !options.Quick && !dnsTunnel)
        {
            direct = await DirectProbe.RunAsync(
                new EndpointTarget(p.Server, p.Port, p.Tls.Enabled && !p.Tls.IsReality, p.Tls.ServerName, p.Tls.Alpn, p.Tls.AllowInsecure),
                timeout, ct).ConfigureAwait(false);
        }

        var urls = settings.Probe.ProbeUrls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x : null).OfType<Uri>().ToList();
        if (urls.Count == 0)
        {
            urls.Add(new Uri("https://www.gstatic.com/generate_204"));
        }

        var count = options.Quick ? 1 : Math.Clamp(options.Samples ?? settings.Probe.SamplesPerNode, 1, 10);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var url = urls[i % urls.Count];
            var r = await ProxyHttpProbe.RunAsync(proxy, url, timeout, ct).ConfigureAwait(false);
            if (!r.Success && urls.Count > 1)
            {
                // A different endpoint distinguishes "node down" from "probe service down".
                r = await ProxyHttpProbe.RunAsync(proxy, urls[(i + 1) % urls.Count], timeout, ct).ConfigureAwait(false);
            }

            double? throughput = null;
            if (r.Success && i == 0 && (options.Throughput ?? settings.Probe.ThroughputTest) && Uri.TryCreate(settings.Probe.ThroughputUrl, UriKind.Absolute, out var tUrl))
            {
                throughput = await ProxyHttpProbe.ThroughputKbpsAsync(proxy, tUrl, 1_000_000, timeout * 2, ct).ConfigureAwait(false);
            }
            else if (r.Success && i == 0 && settings.Probe.DataCheck && Uri.TryCreate(settings.Probe.DataCheckUrl, UriKind.Absolute, out var dUrl))
            {
                // A handshake is not proof: some networks freeze connections to foreign servers after ~15–20 KB.
                // Only a server that delivers real data counts as working.
                throughput = await ProxyHttpProbe.ThroughputKbpsAsync(proxy, dUrl, DataCheckBytes, timeout, ct).ConfigureAwait(false);
                if (throughput is null)
                {
                    r = new HttpProbeResult(false, ProbeFailureKind.Timeout, r.TtfbMs, r.TotalMs, r.StatusCode, "Stalled after the handshake (no data after the first few KB)");
                }
            }

            samples.Add(new HealthSample
            {
                TimestampUtc = _time.GetUtcNow(),
                Success = r.Success,
                Failure = r.Failure,
                DnsMs = direct?.DnsMs,
                TcpMs = direct?.TcpMs,
                TlsMs = direct?.TlsMs,
                TtfbMs = r.TtfbMs,
                TotalMs = r.TotalMs,
                ThroughputKbps = throughput,
                ProbeHost = url.Host,
                Detail = r.Detail ?? (direct is { Success: false } ? direct.Detail : null),
            });
        }

        return samples;
    }

    private HealthSample Failure(ProbeFailureKind kind, string detail) => new()
    {
        TimestampUtc = _time.GetUtcNow(),
        Success = false,
        Failure = kind,
        Detail = Security.SecretRedactor.Redact(detail),
    };

    private async Task RecordAsync(string profileId, HealthSample sample, AppSettings settings, CancellationToken ct)
    {
        await _repo.AppendAsync(profileId, sample, ct).ConfigureAwait(false);
        var history = await _repo.GetRecentAsync(profileId, settings.Probe.HistoryWindow, ct).ConfigureAwait(false);
        _snapshots[profileId] = HealthStatistics.Compute(profileId, history, _recentDisconnects(profileId), settings.Smart.Weights, _time.GetUtcNow(), settings.Probe.HistoryWindow);
    }

    /// <summary>Feeds an observation from a live session (e.g. watchdog probe) into the history.</summary>
    public Task RecordLiveSampleAsync(string profileId, HealthSample sample, CancellationToken ct) => RecordAsync(profileId, sample, _settings(), ct);
}
