using System.Diagnostics;
using FluentAssertions;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Ipc;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.KillSwitch;
using JackiVpn.UnitTests.Cores;
using Microsoft.Extensions.Logging.Abstractions;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Application;

/// <summary>
/// "Disconnect must always work": Stop pre-empts a hanging start and a crash-restart loop, and forces the
/// tunnel down (cores killed, kill switch removed) when the session never yields.
/// </summary>
public sealed class TunnelStopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-stop-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeKillSwitch _killSwitch = new();
    private readonly List<FakeRunner> _runners = [];

    public TunnelStopTests() => Directory.CreateDirectory(_dir);

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

    private TunnelSessionManager Manager(Func<FakeRunner> runner, TimeSpan? restartDelay = null, TimeSpan? stopGate = null)
    {
        var plans = new SessionPlanFactory(new CoreBinaryCatalog(Path.Combine(_dir, "cores")));
        return new TunnelSessionManager(
            (req, ct) => plans.CreateAsync(req, GoldenConfigTests.SingBox114, null, ct),
            () =>
            {
                var r = runner();
                _runners.Add(r);
                return r;
            },
            _killSwitch,
            new NetworkStateJournal(Path.Combine(_dir, "journal.json")),
            Path.Combine(_dir, "rulesets"),
            [],
            NullLogger.Instance)
        {
            RestartBaseDelay = restartDelay ?? TimeSpan.FromMilliseconds(10),
            StopGateTimeout = stopGate ?? TimeSpan.FromSeconds(15),
            FlushDns = () => { },
        };
    }

    private static TunnelStartRequest Request(bool killSwitch) => new()
    {
        Profile = Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0],
        KillSwitch = new KillSwitchSettings { Enabled = killSwitch },
    };

    [Fact]
    public async Task Stop_preempts_a_start_that_hangs_waiting_for_the_core()
    {
        var manager = Manager(() => new FakeRunner { StartBehaviour = ct => Task.Delay(Timeout.Infinite, ct) });
        var start = manager.StartAsync(Request(killSwitch: true), CancellationToken.None);
        await WaitUntilAsync(() => _runners.Count == 1);

        var sw = Stopwatch.StartNew();
        await manager.StopAsync(CancellationToken.None);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        manager.Status.State.Should().Be(TunnelState.Stopped);
        _killSwitch.Engaged.Should().BeFalse("stopping must always give the network back");
        await FluentActions.Awaiting(() => start).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Stop_preempts_a_crash_restart_loop_holding_the_kill_switch()
    {
        var manager = Manager(() => new FakeRunner(), restartDelay: TimeSpan.FromMinutes(5));
        (await manager.StartAsync(Request(killSwitch: true), CancellationToken.None)).Started.Should().BeTrue();
        _killSwitch.Engaged.Should().BeTrue();

        _runners[0].Crash();
        await WaitUntilAsync(() => manager.Status.State == TunnelState.Restarting);
        _killSwitch.Engaged.Should().BeTrue("fail closed while the core restarts");

        var sw = Stopwatch.StartNew();
        await manager.StopAsync(CancellationToken.None);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "Disconnect must not wait out the restart backoff");
        manager.Status.State.Should().Be(TunnelState.Stopped);
        _killSwitch.Engaged.Should().BeFalse();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        _runners.Should().HaveCount(1, "no core may be relaunched after the user stopped");
    }

    [Fact]
    public async Task Stop_forces_the_tunnel_down_when_the_session_never_yields()
    {
        // A start that ignores cancellation entirely (e.g. stuck in a driver call).
        var manager = Manager(() => new FakeRunner { StartBehaviour = _ => Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None) }, stopGate: TimeSpan.FromMilliseconds(300));
        _ = manager.StartAsync(Request(killSwitch: true), CancellationToken.None);
        await WaitUntilAsync(() => _runners.Count == 1 && _killSwitch.Engaged);

        var sw = Stopwatch.StartNew();
        await manager.StopAsync(CancellationToken.None);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        manager.Status.State.Should().Be(TunnelState.Stopped);
        manager.Status.LastError.Should().Contain("force-stopped");
        _killSwitch.Engaged.Should().BeFalse("even a stuck session must not leave the network blocked");
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

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeKillSwitch : IKillSwitch
    {
        public bool Engaged { get; private set; }

        public void Enable(KillSwitchOptions options) => Engaged = true;

        public void Disable() => Engaged = false;

        public bool IsEngaged() => Engaged;
    }

    private sealed class FakeRunner : ISessionRunner
    {
        public Func<CancellationToken, Task>? StartBehaviour { get; init; }

        public event EventHandler<CoreExitInfo>? CoreExited;

        public bool IsRunning { get; private set; }

        public IReadOnlyList<int> ProcessIds => [];

        public async Task<IReadOnlyList<string>> StartAsync(SessionPlan plan, CancellationToken ct)
        {
            if (StartBehaviour is not null)
            {
                await StartBehaviour(ct);
            }

            IsRunning = true;
            return [];
        }

        public void Crash()
        {
            IsRunning = false;
            CoreExited?.Invoke(this, new CoreExitInfo(CoreKind.SingBox, 1, "boom", DateTimeOffset.UtcNow));
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task<CoreTrafficTotals?> GetTrafficAsync(CancellationToken ct) => Task.FromResult<CoreTrafficTotals?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
