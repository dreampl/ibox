using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.Application.Automations;

public enum NetworkKind
{
    Unknown = 0,
    Wifi,
    Ethernet,
    Cellular,
    None,
}

/// <summary>The network the device is on. <see cref="Name"/> is the Wi-Fi name when the platform tells us.</summary>
public sealed record NetworkInfo(NetworkKind Kind, string? Name)
{
    public static readonly NetworkInfo Unknown = new(NetworkKind.Unknown, null);
}

/// <summary>What automations may see and do. One adapter per app (<see cref="AppAutomationEnvironment"/>); fakes in tests.</summary>
public interface IAutomationEnvironment
{
    TimeProvider Time { get; }

    AutomationSettings Settings { get; }

    ConnectionStatus Status { get; }

    event EventHandler<ConnectionStatus>? StatusChanged;

    event EventHandler? NetworkChanged;

    NetworkInfo CurrentNetwork();

    IReadOnlyCollection<NodeHealthSnapshot> Health();

    Task<IReadOnlyDictionary<string, string>> ServerNamesAsync(CancellationToken ct);

    Task<IReadOnlyList<Subscription>> SubscriptionsAsync(CancellationToken ct);

    Task RefreshSubscriptionAsync(string id, CancellationToken ct);

    Task DeleteServerAsync(string profileId, CancellationToken ct);

    Task<bool> ConnectAsync(CancellationToken ct);

    Task DisconnectAsync(CancellationToken ct);

    Task<bool> SwitchToAsync(string profileId, CancellationToken ct);

    void Notify(NotificationCategory category, string title, string message);
}

public sealed record AutomationLogEntry(DateTimeOffset Utc, string AutomationId, string Message);

/// <summary>What the automations did, newest last (kept in memory, the last <see cref="Capacity"/> entries).</summary>
public sealed class AutomationLog
{
    public const int Capacity = 200;
    private readonly Lock _gate = new();
    private readonly LinkedList<AutomationLogEntry> _entries = new();

    public event EventHandler<AutomationLogEntry>? Added;

    public IReadOnlyList<AutomationLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public void Add(AutomationLogEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }

        Added?.Invoke(this, entry);
    }
}

/// <summary>
/// One built-in automation. Implementations are light: they react to events (status, network) or a slow timer,
/// never poll fast, and write a line to the log for everything they do. Add new ones to <see cref="AutomationCatalog"/>.
/// </summary>
public interface IAutomation
{
    string Id { get; }

    /// <summary>Localization key prefix: <c>Auto_{Key}</c> (title) and <c>Auto_{Key}Hint</c> (description).</summary>
    string Key { get; }

    void Start(AutomationRuntime runtime);

    void Stop();
}

/// <summary>Handed to a running automation: its environment, a log writer and a token cancelled on stop.</summary>
public sealed class AutomationRuntime(IAutomationEnvironment env, AutomationLog log, string id, CancellationToken stopping)
{
    public IAutomationEnvironment Env { get; } = env;

    public CancellationToken Stopping { get; } = stopping;

    public void Log(string message) => log.Add(new AutomationLogEntry(Env.Time.GetUtcNow(), id, message));
}

/// <summary>Base with the plumbing: subscriptions are dropped and timers stopped on <see cref="Stop"/>.</summary>
public abstract class AutomationBase : IAutomation
{
    private CancellationTokenSource? _cts;
    private readonly List<Action> _cleanup = [];

    public abstract string Id { get; }

    public abstract string Key { get; }

    protected AutomationRuntime Runtime { get; private set; } = null!;

    public void Start(AutomationRuntime runtime)
    {
        Runtime = runtime;
        OnStart();
    }

    internal void Start(IAutomationEnvironment env, AutomationLog log)
    {
        _cts = new CancellationTokenSource();
        Start(new AutomationRuntime(env, log, Id, _cts.Token));
    }

    public void Stop()
    {
        foreach (var c in _cleanup)
        {
            c();
        }

        _cleanup.Clear();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    protected abstract void OnStart();

    protected void OnStop(Action cleanup) => _cleanup.Add(cleanup);

    /// <summary>Runs <paramref name="tick"/> every <paramref name="period"/> on the environment's clock.</summary>
    protected void Every(TimeSpan period, Func<CancellationToken, Task> tick, TimeSpan? first = null)
    {
        var timer = Runtime.Env.Time.CreateTimer(_ => Run(tick), null, first ?? period, period);
        OnStop(timer.Dispose);
    }

    /// <summary>Runs async work off the event thread; failures are logged, never thrown.</summary>
    protected void Run(Func<CancellationToken, Task> work)
    {
        var ct = Runtime.Stopping;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Runtime.Log("Failed: " + ex.Message);
            }
        }, CancellationToken.None);
    }
}

/// <summary>Every built-in automation, in UI order.</summary>
public static class AutomationCatalog
{
    public static IReadOnlyList<Func<IAutomation>> All { get; } =
    [
        () => new ServerWatchdogAutomation(),
        () => new TrustedNetworksAutomation(),
        () => new NightlyRefreshAutomation(),
        () => new QuotaWatcherAutomation(),
    ];
}

/// <summary>Starts the automations the user turned on and follows the settings as they change.</summary>
public sealed class AutomationManager : IDisposable
{
    private readonly IAutomationEnvironment _env;
    private readonly Dictionary<string, IAutomation> _all;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public AutomationManager(IAutomationEnvironment env, IEnumerable<IAutomation>? automations = null)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        Automations = (automations ?? AutomationCatalog.All.Select(f => f())).ToList();
        _all = Automations.ToDictionary(a => a.Id, StringComparer.Ordinal);
    }

    public AutomationLog Log { get; } = new();

    public IReadOnlyList<IAutomation> Automations { get; }

    public bool IsRunning(string id)
    {
        lock (_gate)
        {
            return _running.Contains(id);
        }
    }

    /// <summary>Call on start and whenever settings change: starts and stops automations to match.</summary>
    public void Apply(AutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            foreach (var (id, a) in _all)
            {
                var want = settings.IsEnabled(id);
                if (want && _running.Add(id))
                {
                    Log.Add(new AutomationLogEntry(_env.Time.GetUtcNow(), id, "Turned on."));
                    Start(a);
                }
                else if (!want && _running.Remove(id))
                {
                    a.Stop();
                    Log.Add(new AutomationLogEntry(_env.Time.GetUtcNow(), id, "Turned off."));
                }
            }
        }
    }

    private void Start(IAutomation a)
    {
        if (a is AutomationBase b)
        {
            b.Start(_env, Log);
        }
        else
        {
            a.Start(new AutomationRuntime(_env, Log, a.Id, CancellationToken.None));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var id in _running)
            {
                _all[id].Stop();
            }

            _running.Clear();
        }
    }
}
