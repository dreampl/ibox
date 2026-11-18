using FluentAssertions;
using JackiVpn.Application.Automations;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.UnitTests.Application;

public class AutomationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 4, 5, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Value;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeEnv : IAutomationEnvironment
    {
        public ManualTime Clock { get; } = new(Now);

        public TimeProvider Time => Clock;

        public AutomationSettings Settings { get; set; } = new();

        public ConnectionStatus Status { get; set; } = new();

        public NetworkInfo Network { get; set; } = NetworkInfo.Unknown;

        public List<NodeHealthSnapshot> Snapshots { get; } = [];

        public List<Subscription> Subs { get; } = [];

        public List<string> Calls { get; } = [];

        public List<string> Notes { get; } = [];

        public event EventHandler<ConnectionStatus>? StatusChanged;

        public event EventHandler? NetworkChanged;

        public void RaiseNetwork() => NetworkChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseStatus() => StatusChanged?.Invoke(this, Status);

        public NetworkInfo CurrentNetwork() => Network;

        public IReadOnlyCollection<NodeHealthSnapshot> Health() => Snapshots;

        public Task<IReadOnlyDictionary<string, string>> ServerNamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Snapshots.ToDictionary(s => s.ProfileId, s => "name-" + s.ProfileId));

        public Task<IReadOnlyList<Subscription>> SubscriptionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Subscription>>(Subs);

        public Task RefreshSubscriptionAsync(string id, CancellationToken ct)
        {
            Calls.Add("refresh:" + id);
            return id == "bad" ? throw new HttpRequestException("offline") : Task.CompletedTask;
        }

        public Task DeleteServerAsync(string profileId, CancellationToken ct)
        {
            Calls.Add("delete:" + profileId);
            return Task.CompletedTask;
        }

        public Task<bool> ConnectAsync(CancellationToken ct)
        {
            Calls.Add("connect");
            return Task.FromResult(true);
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            Calls.Add("disconnect");
            return Task.CompletedTask;
        }

        public Task<bool> SwitchToAsync(string profileId, CancellationToken ct)
        {
            Calls.Add("switch:" + profileId);
            return Task.FromResult(true);
        }

        public void Notify(NotificationCategory category, string title, string message) => Notes.Add($"{category}: {message}");
    }

    private static (T Automation, AutomationLog Log) Start<T>(FakeEnv env, T automation)
        where T : IAutomation
    {
        var log = new AutomationLog();
        automation.Start(new AutomationRuntime(env, log, automation.Id, CancellationToken.None));
        return (automation, log);
    }

    private static NodeHealthSnapshot Node(string id, double score, int failures = 0, double latency = 100, double success = 1) => new()
    {
        ProfileId = id, Score = score, ConsecutiveFailures = failures, MedianLatencyMs = latency, SuccessRate = success,
        SampleCount = 5, SuccessCount = (int)(5 * success), LastSuccessUtc = Now.AddMinutes(-5), LastTestedUtc = Now.AddMinutes(-5),
    };

    [Fact]
    public void Manager_starts_only_enabled_automations_and_logs_on_off()
    {
        var env = new FakeEnv();
        using var manager = new AutomationManager(env);

        manager.Apply(new AutomationSettings().With(QuotaWatcherAutomation.AutomationId, true));
        manager.IsRunning(QuotaWatcherAutomation.AutomationId).Should().BeTrue();
        manager.IsRunning(ServerWatchdogAutomation.AutomationId).Should().BeFalse("everything is opt-in");

        manager.Apply(new AutomationSettings());
        manager.IsRunning(QuotaWatcherAutomation.AutomationId).Should().BeFalse();
        manager.Log.Entries.Select(e => e.Message).Should().Equal("Turned on.", "Turned off.");
        manager.Automations.Select(a => a.Id).Should().OnlyHaveUniqueItems().And.HaveCount(AutomationCatalog.All.Count);
    }

    [Fact]
    public async Task Watchdog_switches_from_a_failing_server_to_the_best_healthy_one()
    {
        var env = new FakeEnv { Status = new ConnectionStatus { State = ConnectionState.Connected, ProfileId = "a", ProfileName = "A" } };
        env.Snapshots.AddRange([Node("a", 10, failures: 4), Node("b", 50), Node("c", 80), Node("d", 99, failures: 1)]);
        var (watchdog, log) = Start(env, new ServerWatchdogAutomation());

        await watchdog.CheckAsync(Ct);
        await watchdog.CheckAsync(Ct);

        env.Calls.Should().Equal(["switch:c"], "the best healthy server, once (cooldown)");
        log.Entries.Should().ContainSingle(e => e.Message.Contains("name-c", StringComparison.Ordinal));
        watchdog.Stop();
    }

    [Fact]
    public async Task Watchdog_stays_when_healthy_or_smart_mode_handles_it()
    {
        var env = new FakeEnv { Status = new ConnectionStatus { State = ConnectionState.Connected, ProfileId = "a" } };
        env.Snapshots.AddRange([Node("a", 60, latency: 150), Node("b", 90, latency: 80)]);
        var (watchdog, _) = Start(env, new ServerWatchdogAutomation());
        await watchdog.CheckAsync(Ct);

        env.Snapshots[0] = Node("a", 10, failures: 5);
        env.Status = env.Status with { SmartModeActive = true };
        await watchdog.CheckAsync(Ct);

        env.Calls.Should().BeEmpty();
        ServerWatchdogAutomation.PickReplacement("a", [Node("a", 50, latency: 900), Node("b", 60, latency: 100)], Now)!.ProfileId.Should().Be("b", "much slower counts as degraded");
        watchdog.Stop();
    }

    [Theory]
    [InlineData(NetworkKind.Wifi, "Home", ConnectionState.Connected, TrustedNetworksAutomation.Decision.Disconnect)]
    [InlineData(NetworkKind.Wifi, "Cafe", ConnectionState.Disconnected, TrustedNetworksAutomation.Decision.Connect)]
    [InlineData(NetworkKind.Wifi, "Cafe", ConnectionState.Connected, TrustedNetworksAutomation.Decision.None)]
    [InlineData(NetworkKind.Wifi, "Home", ConnectionState.Disconnected, TrustedNetworksAutomation.Decision.None)]
    [InlineData(NetworkKind.Cellular, null, ConnectionState.Disconnected, TrustedNetworksAutomation.Decision.None)]
    [InlineData(NetworkKind.Unknown, null, ConnectionState.Connected, TrustedNetworksAutomation.Decision.None)]
    public void Trusted_networks_decision(NetworkKind kind, string? name, ConnectionState state, TrustedNetworksAutomation.Decision expected) =>
        TrustedNetworksAutomation.Decide(new NetworkInfo(kind, name), ["Home"], state).Should().Be(expected);

    [Fact]
    public async Task Trusted_networks_acts_once_per_network_change_so_the_user_can_override()
    {
        var env = new FakeEnv { Settings = new AutomationSettings { TrustedNetworks = ["Home"] }, Network = new NetworkInfo(NetworkKind.Wifi, "Cafe") };
        var (auto, log) = Start(env, new TrustedNetworksAutomation());
        await auto.OnNetworkAsync(Ct);
        env.Status = new ConnectionStatus { State = ConnectionState.Disconnected };
        await auto.OnNetworkAsync(Ct);

        env.Network = new NetworkInfo(NetworkKind.Wifi, "Home");
        env.Status = new ConnectionStatus { State = ConnectionState.Connected };
        await auto.OnNetworkAsync(Ct);

        env.Calls.Should().Equal(["connect", "disconnect"], "one action per network: staying on the Cafe network after the user disconnected changes nothing");
        log.Entries.Last().Message.Should().Contain("trusted Wi-Fi 'Home'");
        auto.Stop();
    }

    [Fact]
    public async Task Nightly_refresh_refreshes_all_and_only_reports_dead_servers_by_default()
    {
        var env = new FakeEnv();
        env.Subs.AddRange([Sub("s1"), Sub("bad"), Sub("off") with { Enabled = false }]);
        env.Snapshots.AddRange([Node("ok", 80), Node("dead", 0, failures: 9, success: 0) with { LastSuccessUtc = null }]);
        var (nightly, log) = Start(env, new NightlyRefreshAutomation());

        await nightly.RunNowAsync(Ct);

        env.Calls.Should().Equal("refresh:s1", "refresh:bad");
        log.Entries.Select(e => e.Message).Should().Contain("Refreshed 1 of 2 subscriptions.").And.Contain(m => m.Contains("stopped working: name-dead", StringComparison.Ordinal));
        nightly.Stop();
    }

    [Fact]
    public async Task Nightly_refresh_removes_dead_servers_when_asked_but_never_the_current_one()
    {
        var env = new FakeEnv { Settings = new AutomationSettings { RemoveDeadAfterDays = 2 }, Status = new ConnectionStatus { ProfileId = "dead-current" } };
        env.Snapshots.AddRange([
            Node("dead", 0, failures: 9) with { LastSuccessUtc = Now.AddDays(-3) },
            Node("dead-current", 0, failures: 9) with { LastSuccessUtc = Now.AddDays(-3) },
            Node("recent", 0, failures: 2) with { LastSuccessUtc = Now.AddDays(-1) },
        ]);
        var (nightly, _) = Start(env, new NightlyRefreshAutomation());

        await nightly.RunNowAsync(Ct);

        env.Calls.Should().Equal("delete:dead");
        NightlyRefreshAutomation.IsDue(Now, 4, null).Should().BeTrue();
        NightlyRefreshAutomation.IsDue(Now, 4, DateOnly.FromDateTime(Now.Date)).Should().BeFalse("once a night");
        NightlyRefreshAutomation.IsDue(Now, 5, null).Should().BeFalse();
        nightly.Stop();
    }

    [Fact]
    public async Task Quota_watcher_warns_once_per_threshold()
    {
        var env = new FakeEnv();
        env.Subs.AddRange([
            Sub("full") with { Metadata = new SubscriptionMetadata { UploadBytes = 1L << 30, DownloadBytes = 9L << 30, TotalBytes = 10L << 30 } },
            Sub("ending") with { Metadata = new SubscriptionMetadata { ExpireUtc = Now.AddDays(2) } },
            Sub("fine") with { Metadata = new SubscriptionMetadata { DownloadBytes = 1L << 30, TotalBytes = 10L << 30, ExpireUtc = Now.AddDays(30) } },
        ]);
        var (quota, log) = Start(env, new QuotaWatcherAutomation());

        await quota.CheckAsync(Ct);
        await quota.CheckAsync(Ct);

        env.Notes.Should().HaveCount(2);
        env.Notes.Should().Contain(n => n.StartsWith("TrafficAlmostFinished", StringComparison.Ordinal) && n.Contains("100% used", StringComparison.Ordinal));
        env.Notes.Should().Contain(n => n.StartsWith("SubscriptionExpired", StringComparison.Ordinal) && n.Contains("2 day", StringComparison.Ordinal));
        log.Entries.Should().HaveCount(2);
        quota.Stop();
    }

    [Fact]
    public void Wifi_names_are_read_from_netsh_and_nmcli()
    {
        const string netsh = "\r\n    Name                   : Wi-Fi\r\n    State                  : connected\r\n    SSID                   : Home Net\r\n    BSSID                  : aa:bb:cc:dd:ee:ff\r\n";
        NetworkIdentity.ParseNetsh(netsh).Should().Be(new NetworkInfo(NetworkKind.Wifi, "Home Net"));
        NetworkIdentity.ParseNetsh(netsh.Replace("connected", "disconnected", StringComparison.Ordinal)).Should().Be(NetworkInfo.Unknown);
        NetworkIdentity.ParseNmcli("no:Other\nyes:Cafe\\:5G\n").Should().Be(new NetworkInfo(NetworkKind.Wifi, "Cafe:5G"));
        NetworkIdentity.ParseNmcli("no:Other\n").Should().Be(NetworkInfo.Unknown);
    }

    private static Subscription Sub(string id) => new() { Id = id, Name = id, MaskedUrl = "https://x/***", Host = "x" };
}
