using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Automations;

/// <summary>
/// Moves to the fastest healthy server when the current one keeps failing or gets much slower. Checks once a
/// minute while connected; stays out of the way when Smart mode already does failover; at most one switch per
/// <see cref="Cooldown"/>.
/// </summary>
public sealed class ServerWatchdogAutomation : AutomationBase
{
    public const string AutomationId = "server-watchdog";
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastSwitch = DateTimeOffset.MinValue;

    public override string Id => AutomationId;

    public override string Key => "Watchdog";

    protected override void OnStart() => Every(TimeSpan.FromMinutes(1), CheckAsync);

    /// <summary>The server to move to, or null to stay. Pure: used by the timer and by tests.</summary>
    public static NodeHealthSnapshot? PickReplacement(string currentId, IReadOnlyCollection<NodeHealthSnapshot> health, DateTimeOffset now)
    {
        var current = health.FirstOrDefault(h => h.ProfileId == currentId);
        var healthy = health
            .Where(h => h.ProfileId != currentId && h.ConsecutiveFailures == 0 && h.SampleCount > 0 && h.SuccessRate >= 0.8
                && h.LastSuccessUtc is { } ok && now - ok < TimeSpan.FromHours(6))
            .OrderByDescending(h => h.Score)
            .FirstOrDefault();
        if (healthy is null || current is null)
        {
            return null;
        }

        var failing = current.ConsecutiveFailures >= 3;
        var muchSlower = current.MedianLatencyMs is { } cur && healthy.MedianLatencyMs is { } best && cur > 3 * best && cur - best > 300;
        return failing || muchSlower ? healthy : null;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        var env = Runtime.Env;
        var status = env.Status;
        var now = env.Time.GetUtcNow();
        if (status.State != ConnectionState.Connected || status.ProfileId is not { } current || status.SmartModeActive || now - _lastSwitch < Cooldown)
        {
            return;
        }

        if (PickReplacement(current, env.Health(), now) is not { } target)
        {
            return;
        }

        var names = await env.ServerNamesAsync(ct).ConfigureAwait(false);
        var to = names.GetValueOrDefault(target.ProfileId, target.ProfileId);
        _lastSwitch = now;
        var ok = await env.SwitchToAsync(target.ProfileId, ct).ConfigureAwait(false);
        Runtime.Log(ok ? $"'{status.ProfileName}' was failing or slow; switched to '{to}'." : $"Tried to switch to '{to}', but it did not connect.");
        if (ok)
        {
            env.Notify(NotificationCategory.AutomaticFailover, "Switched server", $"Now on {to}.");
        }
    }
}

/// <summary>Disconnects on trusted Wi-Fi and connects on any other Wi-Fi. Acts only when the network changes.</summary>
public sealed class TrustedNetworksAutomation : AutomationBase
{
    public const string AutomationId = "trusted-networks";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastNetwork;

    public override string Id => AutomationId;

    public override string Key => "Trusted";

    protected override void OnStart()
    {
        void Changed(object? s, EventArgs e) => Run(OnNetworkAsync);
        Runtime.Env.NetworkChanged += Changed;
        OnStop(() => Runtime.Env.NetworkChanged -= Changed);
        Run(OnNetworkAsync);
    }

    public enum Decision
    {
        None,
        Connect,
        Disconnect,
    }

    public static Decision Decide(NetworkInfo network, IReadOnlyList<string> trusted, ConnectionState state)
    {
        if (network.Kind != NetworkKind.Wifi || string.IsNullOrWhiteSpace(network.Name))
        {
            return Decision.None;
        }

        var isTrusted = trusted.Any(t => string.Equals(t.Trim(), network.Name.Trim(), StringComparison.Ordinal));
        return isTrusted switch
        {
            true when state is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Reconnecting => Decision.Disconnect,
            false when state is ConnectionState.Disconnected or ConnectionState.Faulted => Decision.Connect,
            _ => Decision.None,
        };
    }

    public async Task OnNetworkAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CheckNetworkAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckNetworkAsync(CancellationToken ct)
    {
        var env = Runtime.Env;
        var network = env.CurrentNetwork();
        var key = $"{network.Kind}:{network.Name}";
        if (key == _lastNetwork)
        {
            // Same network as last time: the user's own connect/disconnect there wins.
            return;
        }

        _lastNetwork = key;
        switch (Decide(network, env.Settings.TrustedNetworks, env.Status.State))
        {
            case Decision.Disconnect:
                await env.DisconnectAsync(ct).ConfigureAwait(false);
                Runtime.Log($"On trusted Wi-Fi '{network.Name}': disconnected.");
                break;
            case Decision.Connect:
                var ok = await env.ConnectAsync(ct).ConfigureAwait(false);
                Runtime.Log(ok ? $"On untrusted Wi-Fi '{network.Name}': connected." : $"On untrusted Wi-Fi '{network.Name}': connecting failed.");
                break;
        }
    }
}

/// <summary>Refreshes every subscription once a night and reports (optionally removes) servers that stopped working.</summary>
public sealed class NightlyRefreshAutomation : AutomationBase
{
    public const string AutomationId = "nightly-refresh";
    private DateOnly? _lastRun;

    public override string Id => AutomationId;

    public override string Key => "Nightly";

    protected override void OnStart() => Every(TimeSpan.FromMinutes(15), TickAsync, TimeSpan.FromMinutes(1));

    public static bool IsDue(DateTimeOffset localNow, int hour, DateOnly? lastRun) =>
        localNow.Hour == Math.Clamp(hour, 0, 23) && lastRun != DateOnly.FromDateTime(localNow.Date);

    /// <summary>Servers tested in the last day whose last success is older than <paramref name="days"/> (or that never worked).</summary>
    public static IReadOnlyList<string> DeadServers(IReadOnlyCollection<NodeHealthSnapshot> health, DateTimeOffset now, int days) =>
        health.Where(h => h.SampleCount >= 3 && h.LastTestedUtc is { } t && now - t < TimeSpan.FromDays(1)
                && (h.LastSuccessUtc is not { } ok ? h.SuccessCount == 0 : now - ok > TimeSpan.FromDays(Math.Max(1, days))))
            .Select(h => h.ProfileId).ToList();

    private Task TickAsync(CancellationToken ct)
    {
        var env = Runtime.Env;
        var local = env.Time.GetLocalNow();
        return IsDue(local, env.Settings.NightlyHour, _lastRun) ? RunNowAsync(ct) : Task.CompletedTask;
    }

    public async Task RunNowAsync(CancellationToken ct)
    {
        var env = Runtime.Env;
        _lastRun = DateOnly.FromDateTime(env.Time.GetLocalNow().Date);
        var subs = (await env.SubscriptionsAsync(ct).ConfigureAwait(false)).Where(s => s.Enabled).ToList();
        var failed = 0;
        foreach (var s in subs)
        {
            try
            {
                await env.RefreshSubscriptionAsync(s.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                Runtime.Log($"'{s.Name}' could not be refreshed: {ex.Message}");
            }
        }

        Runtime.Log($"Refreshed {subs.Count - failed} of {subs.Count} subscriptions.");

        var removeAfter = env.Settings.RemoveDeadAfterDays;
        var dead = DeadServers(env.Health(), env.Time.GetUtcNow(), removeAfter > 0 ? removeAfter : 3);
        if (dead.Count == 0)
        {
            return;
        }

        var names = await env.ServerNamesAsync(ct).ConfigureAwait(false);
        var current = env.Status.ProfileId;
        var shown = string.Join(", ", dead.Take(5).Select(id => names.GetValueOrDefault(id, id)));
        if (removeAfter <= 0)
        {
            Runtime.Log($"{dead.Count} servers stopped working: {shown}{(dead.Count > 5 ? "…" : string.Empty)}.");
            return;
        }

        var removed = 0;
        foreach (var id in dead.Where(id => id != current && names.ContainsKey(id)))
        {
            await env.DeleteServerAsync(id, ct).ConfigureAwait(false);
            removed++;
        }

        Runtime.Log($"Removed {removed} dead servers: {shown}{(dead.Count > 5 ? "…" : string.Empty)}.");
    }
}

/// <summary>Warns before a subscription's traffic runs out or it expires. Checks every 6 hours; one warning per threshold.</summary>
public sealed class QuotaWatcherAutomation : AutomationBase
{
    public const string AutomationId = "quota-watcher";
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public override string Id => AutomationId;

    public override string Key => "Quota";

    protected override void OnStart() => Every(TimeSpan.FromHours(6), CheckAsync, TimeSpan.FromSeconds(30));

    public async Task CheckAsync(CancellationToken ct)
    {
        var env = Runtime.Env;
        var now = env.Time.GetUtcNow();
        var settings = env.Settings;
        foreach (var s in await env.SubscriptionsAsync(ct).ConfigureAwait(false))
        {
            var m = s.Metadata;
            if (m.UsedPercent is { } used && used >= Math.Clamp(settings.QuotaWarnPercent, 1, 100) && _warned.Add($"{s.Id}:quota"))
            {
                var left = m.RemainingBytes is { } r ? $" ({r / (1024.0 * 1024 * 1024):0.##} GB left)" : string.Empty;
                Runtime.Log($"'{s.Name}' has used {used:0}% of its traffic{left}.");
                env.Notify(NotificationCategory.TrafficAlmostFinished, "Traffic almost used up", $"{s.Name}: {used:0}% used{left}.");
            }

            if (m.IsUnlimitedExpiry != true && m.ExpireUtc is { } exp)
            {
                var days = (exp - now).TotalDays;
                if (days <= Math.Max(0, settings.ExpiryWarnDays) && _warned.Add($"{s.Id}:expiry"))
                {
                    var text = days <= 0 ? "has expired" : $"expires in {Math.Ceiling(days):0} day(s)";
                    Runtime.Log($"'{s.Name}' {text}.");
                    env.Notify(NotificationCategory.SubscriptionExpired, "Subscription ending", $"{s.Name} {text}.");
                }
            }
        }
    }
}
