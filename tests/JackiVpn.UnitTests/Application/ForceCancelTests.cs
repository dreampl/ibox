using System.Diagnostics;
using FluentAssertions;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Notifications;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
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
/// Force cancel while connecting: in every phase a cancel aborts the work, stops (or kills) what started, never
/// leaves the kill switch holding, never blames the server, and is back to Disconnected within two seconds.
/// </summary>
public sealed class ForceCancelTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-cancel-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ProxyProfile _a = Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0];
    private readonly ProxyProfile _b;
    private readonly FakeBackend _backend = new();
    private readonly FakeProbe _probe = new();
    private readonly IHealthHistoryRepository _healthRepo = Substitute.For<IHealthHistoryRepository>();

    public ForceCancelTests()
    {
        Directory.CreateDirectory(_dir);
        _b = _a with { Id = _a.Id + "-b" };
    }

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

    /// <param name="smart">Two servers in smart mode (the pool test runs) instead of one selected server.</param>
    /// <param name="slowProfiles">Loading the server list waits until cancelled (the "preparing" phase).</param>
    /// <param name="slowPoolTest">The pool test waits until cancelled (the "testing" phase).</param>
    private ConnectionOrchestrator Create(bool smart = false, bool slowProfiles = false, bool slowPoolTest = false, bool killSwitch = true, TimeSpan? reconnectDelay = null, bool protectOnDrop = true)
    {
        var settings = new AppSettings
        {
            Connection = new ConnectionSettings { Mode = ConnectionMode.SystemProxy, SmartMode = smart, SelectedProfileId = _a.Id },
            KillSwitch = new KillSwitchSettings { Enabled = killSwitch, ProtectOnDrop = protectOnDrop },
            Smart = new SmartSettings { Failover = new FailoverPolicy { HealthCheckInterval = TimeSpan.FromHours(1) } },
            Performance = new PerformanceSettings { AutoTune = false },
            Probe = new ProbeSettings { CheckPublicIp = false },
            Ipv6 = Ipv6Mode.Disable,
        };
        Func<AppSettings> s = () => settings;

        var profiles = Substitute.For<IProfileRepository>();
        IReadOnlyList<ProxyProfile> all = smart ? [_a, _b] : [_a];
        profiles.GetAllAsync(Arg.Any<CancellationToken>()).Returns(ci => slowProfiles ? Forever<IReadOnlyList<ProxyProfile>>(ci.Arg<CancellationToken>()) : Task.FromResult(all));
        profiles.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(all.FirstOrDefault(p => p.Id == ci.Arg<string>())));
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Subscription>>([]));

        var catalog = new CoreBinaryCatalog(Path.Combine(_dir, "cores"));
        var engine = Substitute.For<ICoreEngine>();
        engine.Kind.Returns(CoreKind.SingBox);
        if (slowPoolTest)
        {
            engine.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(ci => Forever<CoreCapabilities>(ci.Arg<CancellationToken>()));
        }

        var insights = new InsightStore(Path.Combine(_dir, "insights.json"));
        var disconnects = new DisconnectTracker();
        return new ConnectionOrchestrator(new OrchestratorDependencies
        {
            Settings = s,
            Profiles = profiles,
            Subscriptions = subscriptions,
            History = Substitute.For<IConnectionHistoryRepository>(),
            Health = new HealthCheckService(catalog, engine, null, _healthRepo, s, disconnects.Recent, _dir, NullLogger.Instance),
            RuleSets = new RuleSetManager(Path.Combine(_dir, "rulesets"), Substitute.For<IRuleSetDownloader>(), NullLogger.Instance),
            BackendFactory = _ => _backend,
            Tuner = new ConnectionTuner(catalog, engine, null, _healthRepo, insights, s, _dir, NullLogger.Instance),
            Insights = insights,
            Notifications = new NotificationService(Substitute.For<INotificationSink>(), () => settings.Notifications, NullLogger.Instance),
            LiveProbe = _probe,
            Disconnects = disconnects,
            Logger = NullLogger.Instance,
            ReconnectBaseDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(10),
            CancelGateTimeout = TimeSpan.FromMilliseconds(800),
            CancelStopTimeout = TimeSpan.FromMilliseconds(400),
        });
    }

    private static async Task<T> Forever<T>(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Starts a connect, waits for <paramref name="reached"/>, cancels, and returns how long the cancel took.</summary>
    private static async Task<TimeSpan> CancelWhenAsync(ConnectionOrchestrator orchestrator, Func<bool> reached)
    {
        var connect = orchestrator.ConnectAsync(new ConnectRequest(), Ct);
        await WaitUntilAsync(reached);
        var sw = Stopwatch.StartNew();
        await orchestrator.DisconnectAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await WaitUntilAsync(() => orchestrator.State == ConnectionState.Disconnected);
        sw.Stop();
        (await connect.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeFalse("a cancelled connect does not report success");
        return sw.Elapsed;
    }

    [Fact]
    public async Task Cancel_while_preparing()
    {
        await using var orchestrator = Create(slowProfiles: true);
        var elapsed = await CancelWhenAsync(orchestrator, () => orchestrator.State == ConnectionState.Preparing);

        elapsed.Should().BeLessThan(Budget);
        _backend.Starts.Should().Be(0);
        orchestrator.Status.LastError.Should().BeNull("a cancel is not an error");
    }

    [Fact]
    public async Task Cancel_while_testing_servers()
    {
        await using var orchestrator = Create(smart: true, slowPoolTest: true);
        var elapsed = await CancelWhenAsync(orchestrator, () => orchestrator.State == ConnectionState.Testing);

        elapsed.Should().BeLessThan(Budget);
        _backend.Starts.Should().Be(0, "nothing starts after the pool test was cancelled");
    }

    [Fact]
    public async Task Cancel_while_the_core_starts()
    {
        _backend.StartBehaviour = ct => Task.Delay(Timeout.Infinite, ct);
        await using var orchestrator = Create();
        var elapsed = await CancelWhenAsync(orchestrator, () => _backend.Starts == 1);

        elapsed.Should().BeLessThan(Budget);
        _backend.IsRunning.Should().BeFalse("the started core is stopped");
        _backend.KillSwitchUp.Should().BeFalse("the kill switch never keeps blocking after a cancel");
        await _healthRepo.DidNotReceive().AppendAsync(Arg.Any<string>(), Arg.Is<HealthSample>(h => !h.Success), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_while_checking_that_data_flows()
    {
        _probe.Behaviour = ct => Task.Delay(Timeout.Infinite, ct);
        await using var orchestrator = Create();
        var elapsed = await CancelWhenAsync(orchestrator, () => _probe.Calls == 1);

        elapsed.Should().BeLessThan(Budget);
        _backend.IsRunning.Should().BeFalse();
        _backend.KillSwitchUp.Should().BeFalse();
        _backend.Stops.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_stop_that_hangs_is_escalated_to_a_kill()
    {
        _backend.StartBehaviour = ct => Task.Delay(Timeout.Infinite, ct);
        _backend.StopBehaviour = _ => Task.Delay(TimeSpan.FromSeconds(30)); // graceful stop hangs
        await using var orchestrator = Create();
        var elapsed = await CancelWhenAsync(orchestrator, () => _backend.Starts == 1);

        elapsed.Should().BeLessThan(Budget);
        _backend.Kills.Should().Be(1);
        _backend.IsRunning.Should().BeFalse("the kill took the core down");
        _backend.KillSwitchUp.Should().BeFalse("the kill lifted the kill switch");
    }

    [Fact]
    public async Task A_step_that_ignores_cancellation_cannot_hold_the_cancel()
    {
        var release = new TaskCompletionSource();
        _backend.StartBehaviour = _ => release.Task; // deliberately ignores the token
        _backend.StopBehaviour = _ => Task.Delay(TimeSpan.FromSeconds(30));
        await using var orchestrator = Create();
        var connect = orchestrator.ConnectAsync(new ConnectRequest(), Ct);
        await WaitUntilAsync(() => _backend.Starts == 1);

        var sw = Stopwatch.StartNew();
        await orchestrator.DisconnectAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sw.Elapsed.Should().BeLessThan(Budget + TimeSpan.FromMilliseconds(500));
        orchestrator.State.Should().Be(ConnectionState.Disconnected);
        _backend.IsRunning.Should().BeFalse();
        _backend.KillSwitchUp.Should().BeFalse();

        // When the stuck step finally returns, the cancelled connect unwinds without reconnecting anything.
        release.SetResult();
        (await connect.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeFalse();
        orchestrator.State.Should().Be(ConnectionState.Disconnected);
        _backend.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task A_dropped_connection_is_never_given_up_while_apps_are_held()
    {
        await using var orchestrator = Create(killSwitch: false);
        (await orchestrator.ConnectAsync(new ConnectRequest(), Ct)).Should().BeTrue();

        // The server stays unreachable for several rounds: IBOX keeps trying instead of giving up.
        var failing = 4;
        _backend.StartBehaviour = _ => Interlocked.Decrement(ref failing) >= 0 ? throw new BackendStartException("unreachable") : Task.CompletedTask;
        _backend.Crash();
        await WaitUntilAsync(() => orchestrator.Status.LastError?.Contains("trying again", StringComparison.Ordinal) == true);
        orchestrator.State.Should().NotBe(ConnectionState.Faulted, "a connection that was working is not given up");

        await WaitUntilAsync(() => orchestrator.State == ConnectionState.Connected);
        orchestrator.Status.KillSwitchEngaged.Should().BeFalse();
        _backend.Starts.Should().BeGreaterThan(4);
    }

    [Fact]
    public async Task Without_protection_a_lost_connection_ends_after_the_retries()
    {
        await using var orchestrator = Create(killSwitch: false, protectOnDrop: false);
        (await orchestrator.ConnectAsync(new ConnectRequest(), Ct)).Should().BeTrue();
        _backend.StartBehaviour = _ => throw new BackendStartException("unreachable");
        _backend.Crash();

        await WaitUntilAsync(() => orchestrator.State == ConnectionState.Faulted);
    }

    [Fact]
    public async Task Disconnect_ends_the_waiting_at_once()
    {
        await using var orchestrator = Create(killSwitch: false, reconnectDelay: TimeSpan.FromSeconds(2));
        (await orchestrator.ConnectAsync(new ConnectRequest(), Ct)).Should().BeTrue();
        _backend.StartBehaviour = _ => throw new BackendStartException("unreachable");
        _backend.Crash();
        await WaitUntilAsync(() => orchestrator.State == ConnectionState.Reconnecting);

        var sw = Stopwatch.StartNew();
        await orchestrator.DisconnectAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sw.Elapsed.Should().BeLessThan(Budget);
        orchestrator.State.Should().Be(ConnectionState.Disconnected);
        _backend.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_while_reconnecting()
    {
        await using var orchestrator = Create(reconnectDelay: TimeSpan.FromSeconds(30));
        (await orchestrator.ConnectAsync(new ConnectRequest(), Ct)).Should().BeTrue();
        _backend.Crash();
        await WaitUntilAsync(() => orchestrator.State == ConnectionState.Reconnecting);

        var sw = Stopwatch.StartNew();
        await orchestrator.DisconnectAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        sw.Elapsed.Should().BeLessThan(Budget, "a cancel does not wait out the reconnect backoff");
        orchestrator.State.Should().Be(ConnectionState.Disconnected);
        _backend.KillSwitchUp.Should().BeFalse();
        _backend.Starts.Should().Be(1, "no retry runs after the cancel");
    }

    [Fact]
    public async Task Rapid_connect_and_cancel_leaves_nothing_running()
    {
        _backend.StartBehaviour = async ct => await Task.Delay(Random.Shared.Next(0, 40), ct);
        await using var orchestrator = Create();
        for (var i = 0; i < 25; i++)
        {
            var connect = orchestrator.ConnectAsync(new ConnectRequest(), Ct);
            await Task.Delay(Random.Shared.Next(0, 30), Ct);
            await orchestrator.DisconnectAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await connect.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            orchestrator.State.Should().Be(ConnectionState.Disconnected, $"round {i}");
            _backend.IsRunning.Should().BeFalse($"no core may keep running after round {i}");
            _backend.KillSwitchUp.Should().BeFalse($"round {i}");
        }

        _backend.MaxConcurrentRunning.Should().BeLessThanOrEqualTo(1, "a new start never overlaps a running core");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached.");
            }

            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeProbe : ILiveProbe
    {
        private int _calls;

        public Func<CancellationToken, Task>? Behaviour { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public async Task<HealthSample> ProbeAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Behaviour is { } b)
            {
                await b(ct);
            }

            return new HealthSample { TimestampUtc = DateTimeOffset.UtcNow, Success = true, TotalMs = 100 };
        }

        public Task<ExitInfo?> LocateAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct) => Task.FromResult<ExitInfo?>(null);

        public Task<double?> PingAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct) => Task.FromResult<double?>(100);

        public void ResetPing()
        {
        }
    }

    /// <summary>Tracks what a real backend would leave behind: a running core and the kill switch.</summary>
    private sealed class FakeBackend : IConnectionBackend
    {
        private readonly Lock _gate = new();
        private int _starts;
        private int _stops;
        private int _kills;
        private int _running;
        private int _maxRunning;

        public Func<CancellationToken, Task>? StartBehaviour { get; set; }

        public Func<CancellationToken, Task>? StopBehaviour { get; set; }

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public int Kills => Volatile.Read(ref _kills);

        public bool IsRunning => Volatile.Read(ref _running) > 0;

        public int MaxConcurrentRunning => Volatile.Read(ref _maxRunning);

        public bool KillSwitchUp { get; private set; }

        public ConnectionMode Mode => ConnectionMode.SystemProxy;

        public event EventHandler<BackendFault>? Faulted;

        public event EventHandler<TrafficSnapshot>? Traffic;

        public void Crash()
        {
            TearDown();
            KillSwitchUp = true; // fail closed while the session recovers
            Faulted?.Invoke(this, new BackendFault("core exited", false, true));
        }

        public async Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _starts);
            KillSwitchUp = request.Settings.KillSwitch.Enabled;
            lock (_gate)
            {
                // Each connect gets a fresh backend in production; a start while a core still runs means one leaked.
                _running++;
                _maxRunning = Math.Max(_maxRunning, _running);
            }

            if (StartBehaviour is { } b)
            {
                await b(ct);
            }

            Traffic?.Invoke(this, new TrafficSnapshot(0, 0, 0, 0, DateTimeOffset.UtcNow));
            return new BackendStartResult(CoreKind.SingBox, false, [], 10808);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stops);
            if (StopBehaviour is { } b)
            {
                await b(ct);
            }

            TearDown();
        }

        public Task KillAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _kills);
            TearDown();
            return Task.CompletedTask;
        }

        private void TearDown()
        {
            lock (_gate)
            {
                _running = 0;
            }

            KillSwitchUp = false;
        }

        public Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct) => Task.FromResult(KillSwitchUp && !IsRunning);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
