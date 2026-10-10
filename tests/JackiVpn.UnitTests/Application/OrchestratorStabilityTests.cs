using FluentAssertions;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Notifications;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Application;

/// <summary>
/// Stability of a live session: the watchdog never tears down a connection that is visibly carrying data, does
/// reconnect a dead one, and Disconnect brings the tunnel down even while a connect is stuck.
/// </summary>
public sealed class OrchestratorStabilityTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-orch-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ProxyProfile _profile = Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0];
    private readonly FakeBackend _backend = new();
    private readonly FakeProbe _probe = new();

    public OrchestratorStabilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConnectionOrchestrator Create(TimeSpan? disconnectGate = null)
    {
        var settings = new AppSettings
        {
            Connection = new ConnectionSettings { Mode = ConnectionMode.SystemProxy, SmartMode = false, SelectedProfileId = _profile.Id },
            Smart = new SmartSettings { Failover = new FailoverPolicy { HealthCheckInterval = TimeSpan.FromMilliseconds(80), ConsecutiveFailuresToFailover = 3 } },
            Performance = new PerformanceSettings { AutoTune = false },
            Probe = new ProbeSettings { CheckPublicIp = false },
            Ipv6 = Ipv6Mode.Disable,
        };
        Func<AppSettings> s = () => settings;

        var profiles = Substitute.For<IProfileRepository>();
        profiles.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ProxyProfile>>([_profile]));
        profiles.GetAsync(_profile.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ProxyProfile?>(_profile));
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Subscription>>([]));

        var catalog = new CoreBinaryCatalog(Path.Combine(_dir, "cores"));
        var engine = Substitute.For<ICoreEngine>();
        var healthRepo = Substitute.For<IHealthHistoryRepository>();
        var insights = new InsightStore(Path.Combine(_dir, "insights.json"));
        var disconnects = new DisconnectTracker();
        return new ConnectionOrchestrator(new OrchestratorDependencies
        {
            Settings = s,
            Profiles = profiles,
            Subscriptions = subscriptions,
            History = Substitute.For<IConnectionHistoryRepository>(),
            Health = new HealthCheckService(catalog, engine, null, healthRepo, s, disconnects.Recent, _dir, NullLogger.Instance),
            RuleSets = new RuleSetManager(Path.Combine(_dir, "rulesets"), Substitute.For<IRuleSetDownloader>(), NullLogger.Instance),
            BackendFactory = _ => _backend,
            Tuner = new ConnectionTuner(catalog, engine, null, healthRepo, insights, s, _dir, NullLogger.Instance),
            Insights = insights,
            Notifications = new NotificationService(Substitute.For<INotificationSink>(), () => settings.Notifications, NullLogger.Instance),
            LiveProbe = _probe,
            Disconnects = disconnects,
            Logger = NullLogger.Instance,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(10),
            DisconnectGateTimeout = disconnectGate ?? TimeSpan.FromSeconds(10),
        });
    }

    [Fact]
    public async Task Failed_probes_do_not_tear_down_a_connection_that_is_carrying_data()
    {
        await using var orchestrator = Create();
        (await orchestrator.ConnectAsync(new ConnectRequest(), Ct)).Should().BeTrue();
        _probe.Succeed = false;

        // A big download keeps the tunnel busy; probes time out behind it (bufferbloat), data keeps arriving.
        long total = 0;
        for (var i = 0; i < 25; i++)
        {
            total += 512 * 1024;
            _backend.EmitTraffic(total);
            await Task.Delay(40, Ct);
        }

        _backend.Starts.Should().Be(1, "a connection that is delivering data must not be reconnected");
        orchestrator.State.Should().Be(ConnectionState.Connected);
    }

    [Fact]
    public async Task A_dead_connection_is_reconnected()
    {
        await using var orchestrator = Create();
        (await orchestrator.ConnectAsync(new ConnectRequest(), Ct)).Should().BeTrue();
        _probe.Succeed = false;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_backend.Starts < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        _backend.Starts.Should().BeGreaterThanOrEqualTo(2, "three failed checks with no traffic mean the tunnel is dead");
    }

    [Fact]
    public async Task Disconnect_brings_the_tunnel_down_even_while_a_connect_is_stuck()
    {
        await using var orchestrator = Create(disconnectGate: TimeSpan.FromMilliseconds(300));
        var release = new TaskCompletionSource();
        _backend.StartGate = release.Task; // the start ignores cancellation until released
        var connect = orchestrator.ConnectAsync(new ConnectRequest(), Ct);
        await WaitUntilAsync(() => _backend.Starts == 1);

        var disconnect = orchestrator.DisconnectAsync(Ct);
        await WaitUntilAsync(() => _backend.Stops >= 1);
        disconnect.IsCompleted.Should().BeFalse("bookkeeping waits for the stuck operation, but the tunnel is already down");

        release.SetResult();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await connect.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        orchestrator.State.Should().BeOneOf(ConnectionState.Disconnected, ConnectionState.Faulted);
    }

    [Theory]
    [InlineData(new[] { 100.0 }, 100.0, null)]
    [InlineData(new[] { 100.0, 900.0, 110.0 }, 110.0, 795.0)]
    [InlineData(new[] { 100.0, 120.0, 110.0, 130.0 }, 120.0, 15.0)]
    public void Latency_shows_the_median_of_three_and_the_jitter(double[] samples, double median, double? jitter)
    {
        var window = new Queue<double>();
        (double Median, double? Jitter) last = default;
        foreach (var s in samples)
        {
            last = LatencyStats.Add(window, s);
        }

        last.Median.Should().Be(median, "one queued request is not a lag spike");
        last.Jitter.Should().Be(jitter);
    }

    [Theory]
    [InlineData(ActivityOptimization.Auto, ActivityProfile.Gaming, ActivityProfile.Gaming)]
    [InlineData(ActivityOptimization.Off, ActivityProfile.Gaming, ActivityProfile.Normal)]
    [InlineData(ActivityOptimization.AlwaysStreaming, ActivityProfile.Normal, ActivityProfile.Streaming)]
    public void Activity_setting_overrides_detection(ActivityOptimization setting, ActivityProfile detected, ActivityProfile expected) =>
        ActivityRules.Effective(setting, detected).Should().Be(expected);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached.");
            }

            await Task.Delay(20, Ct);
        }
    }

    private sealed class FakeProbe : ILiveProbe
    {
        public bool Succeed { get; set; } = true;

        public Task<HealthSample> ProbeAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct) =>
            Task.FromResult(new HealthSample { TimestampUtc = DateTimeOffset.UtcNow, Success = Succeed, Failure = Succeed ? ProbeFailureKind.None : ProbeFailureKind.Timeout, TotalMs = Succeed ? 120 : null });

        public Task<ExitInfo?> LocateAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct) => Task.FromResult<ExitInfo?>(null);

        public Task<double?> PingAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct) => Task.FromResult<double?>(Succeed ? 120 : null);

        public void ResetPing()
        {
        }
    }

    private sealed class FakeBackend : IConnectionBackend
    {
        private int _starts;
        private int _stops;

        public Task? StartGate { get; set; }

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public ConnectionMode Mode => ConnectionMode.SystemProxy;

        public event EventHandler<BackendFault>? Faulted;

        public event EventHandler<TrafficSnapshot>? Traffic;

        public void EmitTraffic(long downloadTotal) => Traffic?.Invoke(this, new TrafficSnapshot(0, downloadTotal, 0, 512 * 1024, DateTimeOffset.UtcNow));

        public async Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _starts);
            if (StartGate is { } gate)
            {
                await gate; // deliberately ignores ct
            }

            return new BackendStartResult(CoreKind.SingBox, false, [], 10808);
        }

        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }

        public Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct) => Task.FromResult(false);

        public ValueTask DisposeAsync()
        {
            Faulted?.Invoke(this, new BackendFault("disposed", false, false));
            return ValueTask.CompletedTask;
        }
    }
}
