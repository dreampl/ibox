using System.Net.NetworkInformation;
using JackiVpn.Application.Health;
using JackiVpn.Application.Notifications;
using JackiVpn.Application.Smart;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Ipc;
#if WINDOWS
using JackiVpn.Networking.Monitoring;
#endif
using JackiVpn.Routing;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Ownership;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Connection;

public sealed record ConnectRequest
{
    /// <summary>Manual mode: the profile to connect. Smart mode: the anchor that selects the pool.</summary>
    public string? ProfileId { get; init; }

    /// <summary>null = follow <see cref="ConnectionSettings.SmartMode"/>.</summary>
    public bool? Smart { get; init; }

    /// <summary>Smart mode: restrict the pool to one subscription (default: the anchor's subscription).</summary>
    public string? SubscriptionId { get; init; }
}

public sealed class ConnectionException(string message) : Exception(message);

/// <summary>Collaborators of the orchestrator, grouped so the constructor stays readable and testable.</summary>
public sealed record OrchestratorDependencies
{
    public required Func<AppSettings> Settings { get; init; }
    public required IProfileRepository Profiles { get; init; }
    public required ISubscriptionRepository Subscriptions { get; init; }
    public required IConnectionHistoryRepository History { get; init; }
    public required HealthCheckService Health { get; init; }
    public required RuleSetManager RuleSets { get; init; }
    public required Func<ConnectionMode, IConnectionBackend> BackendFactory { get; init; }
    public required ConnectionTuner Tuner { get; init; }
    public required InsightStore Insights { get; init; }
    public required NotificationService Notifications { get; init; }
    public required ILiveProbe LiveProbe { get; init; }

    /// <summary>Require one real request through the tunnel before reporting "connected" (off only in tests).</summary>
    public bool VerifyData { get; init; } = true;

    /// <summary>The best clean resolver found on this network (DNS search), used to look up server addresses.</summary>
    public Func<DnsServerEndpoint?>? BestDns { get; init; }
    public required DisconnectTracker Disconnects { get; init; }
#if WINDOWS
    public NetworkChangeMonitor? NetworkMonitor { get; init; }
#endif
    public required ILogger Logger { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Identity of the network the device is on (Android: <c>cell:&lt;MCC-MNC&gt;</c> or <c>wifi</c>), used to try
    /// first what worked on this network before. Platforms without a cheap identity keep one shared key.
    /// </summary>
    public Func<string> NetworkKey { get; init; } = static () => "default";

    /// <summary>Blocks, workplace routes and relay classes for the next session (null = none).</summary>
    public Func<Domain.Usage.TrafficPolicy?> Traffic { get; init; } = static () => null;

    /// <summary>Relay limits a new session starts with.</summary>
    public Func<Domain.Usage.TrafficLimits> Limits { get; init; } = static () => Domain.Usage.TrafficLimits.None;

    /// <summary>Finds another way to reach servers that fail on this network (secure DNS, clean CDN IP, other TLS fingerprint).</summary>
    public Connectivity.EndpointRescue? Rescue { get; init; }

    /// <summary>Base delay of the reconnect backoff (1×, 2×, 4×…). Shortened in tests.</summary>
    public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long Disconnect waits for a running operation to yield before forcing the tunnel down.</summary>
    public TimeSpan DisconnectGateTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The desktop-side connection brain. Every operation that changes the session (connect, disconnect,
/// switch, recover) runs under one gate so they never interleave; disconnect first cancels any in-flight
/// connect so the user is never stuck waiting. The state machine rejects illegal transitions, and every
/// asynchronous event (core exit, network change, watchdog verdict) is checked against the session it was
/// raised for, so late events from an old session cannot disturb a new one.
/// </summary>
public sealed class ConnectionOrchestrator : IAsyncDisposable
{
    public const int MaxCandidates = 3;

    /// <summary>Upper bound of nodes probed before a smart connect (the rest keep their cached scores).</summary>
    public const int PoolTestLimit = 30;

    /// <summary>After the quick screen, this many of the best nodes get a full multi-sample measurement.</summary>
    public const int RefineCount = 3;

    /// <summary>Interval of the live latency measurement shown on the dashboard.</summary>
    public static TimeSpan PingInterval { get; } = TimeSpan.FromSeconds(4);

    private readonly OrchestratorDependencies _d;
    private readonly ConnectionStateMachine _state;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _statusGate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _switches = new();
    private CancellationTokenSource? _connectCts;
    private Session? _session;
    private Session? _rememberedSession;
    private ConnectionStatus _status = new();

    /// <summary>Downloaded through a session before its server counts as working on the current network.</summary>
    private const long WorkedBytes = 512 * 1024;

    public ConnectionOrchestrator(OrchestratorDependencies dependencies, ConnectionStateMachine? state = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _d = dependencies;
        _state = state ?? new ConnectionStateMachine(dependencies.Time);
        _state.StateChanged += (_, change) => Publish(s => s with { State = change.Current });
#if WINDOWS
        if (_d.NetworkMonitor is { } monitor)
        {
            monitor.Changed += (_, e) => NotifyNetworkChanged(e.BecameUnavailable, e.Reason);
        }
#endif
    }

    public event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>The device moved to another network (Wi-Fi ↔ mobile, SIM change); raised after the change settled.</summary>
    public event EventHandler<string>? NetworkChanged;

    public ConnectionState State => _state.State;

    public ConnectionStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    private ILogger Log => _d.Logger;

    private DateTimeOffset Now => _d.Time.GetUtcNow();

    // ------------------------------------------------------------------ connect

    /// <summary>Connects; returns false (with <see cref="ConnectionStatus.LastError"/> set) on failure.</summary>
    public async Task<bool> ConnectAsync(ConnectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        Volatile.Write(ref _connectCts, cts);
        try
        {
            await _op.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!ConnectionStateRules.IsIdle(_state.State))
            {
                Log.LogInformation("Connect ignored: already {State}.", _state.State);
                return _state.State == ConnectionState.Connected;
            }

            return await ConnectLockedAsync(request, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _connectCts, null, cts);
            _op.Release();
        }
    }

    private async Task<bool> ConnectLockedAsync(ConnectRequest request, CancellationToken ct)
    {
        var settings = _d.Settings();
        var mode = settings.Connection.Mode;

        // A faulted session may still hold the kill switch. Reuse its backend in the same mode so the
        // protection stays in place across the reconnect; otherwise tear it down first.
        IConnectionBackend? backend = null;
        if (_session is { } old)
        {
            if (old.Backend.Mode == mode)
            {
                backend = old.Backend;
                await DetachSessionAsync(old, stopBackend: false, ConnectionOutcome.Failed, old.LastError).ConfigureAwait(false);
            }
            else
            {
                await DetachSessionAsync(old, stopBackend: true, ConnectionOutcome.Failed, old.LastError).ConfigureAwait(false);
            }
        }

        var generation = _state.Transition(ConnectionState.Preparing, "connect").Generation;
        Publish(_ => new ConnectionStatus { State = ConnectionState.Preparing, Mode = mode });
        var ownBackend = backend is null;
        backend ??= _d.BackendFactory(mode);
        Volatile.Write(ref _startingBackend, backend);
        try
        {
            var pool = await BuildPoolAsync(request, settings, ct).ConfigureAwait(false);
            var smart = pool.Smart;
            var routing = await PrepareRuleSetsAsync(settings, mode, ct).ConfigureAwait(false);

            var tested = 0;
            if (smart && pool.Profiles.Count > 1)
            {
                _state.Transition(ConnectionState.Testing, "testing servers");
                tested = await SmartTestAsync(pool.Profiles, pool.OwnedIds, settings, ct).ConfigureAwait(false);
            }

            var candidates = smart ? Rank(pool.Profiles, excluded: CooledDown(), pool.OwnedIds) : pool.Profiles;
            var rescueTried = false;
            if (candidates.Count == 0 && _d.Rescue is { } rescue)
            {
                // Every server failed here: look for a way around the block (encrypted DNS, clean CDN address,
                // another TLS fingerprint) for the most promising ones, then test them again.
                rescueTried = true;
                var rescued = await rescue.RescueAsync(ChooseProfilesToTest(pool.Profiles, pool.OwnedIds).Take(3).ToList(), ct).ConfigureAwait(false);
                if (rescued.Count > 0)
                {
                    await _d.Health.TestAsync(rescued, new ProbeRequestOptions { Force = true, Samples = 1 }, ct).ConfigureAwait(false);
                    candidates = Rank(rescued, CooledDown(), pool.OwnedIds) is { Count: > 0 } ranked ? ranked : rescued;
                }
            }

            if (candidates.Count == 0)
            {
                throw new ConnectionException("No usable server: every server in the pool failed its health check.");
            }

            _state.Transition(ConnectionState.Connecting, "starting tunnel");
            var session = new Session(generation, backend, pool.Profiles, smart, pool.OwnedIds) { TestedCount = tested };
            AttachEvents(session);
            var errors = new List<string>();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await StartOnAsync(session, candidate, routing, settings, ct).ConfigureAwait(false);
                    _session = session;
                    _state.Transition(ConnectionState.Connected, "connected");
                    await OnConnectedAsync(session, routing.Warnings, settings, isSwitch: false).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (IsStartFailure(ex))
                {
                    var error = SecretRedactor.Redact(ex.Message);
                    errors.Add($"{candidate.DisplayName}: {error}");
                    Log.LogWarning("Connecting to {Profile} failed: {Error}", candidate.DisplayName, error);
                    await RecordFailureAsync(candidate, error, ct).ConfigureAwait(false);
                }
            }

            if (!rescueTried && _d.Rescue is { } late)
            {
                foreach (var candidate in await late.RescueAsync(candidates.Take(2).ToList(), ct).ConfigureAwait(false))
                {
                    try
                    {
                        await StartOnAsync(session, candidate, routing, settings, ct).ConfigureAwait(false);
                        _session = session;
                        _state.Transition(ConnectionState.Connected, "connected");
                        await OnConnectedAsync(session, routing.Warnings, settings, isSwitch: false).ConfigureAwait(false);
                        return true;
                    }
                    catch (Exception ex) when (IsStartFailure(ex))
                    {
                        errors.Add($"{candidate.DisplayName}: {SecretRedactor.Redact(ex.Message)}");
                    }
                }
            }

            DetachEvents(session);
            _session = session; // keeps the backend reachable so Disconnect can release a holding kill switch
            session.LastError = errors.Count == 1 ? errors[0] : "All candidate servers failed. " + string.Join(" | ", errors);
            _d.Notifications.Notify(NotificationCategory.ConnectionFailed, "Connection failed", errors.FirstOrDefault() ?? "Unknown error");
            await FailAsync(session.LastError).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Disconnect (or shutdown) while connecting: unwind cleanly.
            await SafeStopAsync(backend).ConfigureAwait(false);
            await backend.DisposeAsync().ConfigureAwait(false);
            _session = null;
            _state.TryTransition(ConnectionState.Disconnecting, "cancelled", out _);
            _state.TryTransition(ConnectionState.Disconnected, "cancelled", out _);
            Publish(_ => new ConnectionStatus());
            return false;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            // Preparation failed before anything started (no servers, unsafe routing, I/O…).
            var error = SecretRedactor.Redact(ex.Message);
            Log.LogWarning("Connect failed during preparation: {Error}", error);
            if (ownBackend)
            {
                await SafeStopAsync(backend).ConfigureAwait(false);
                await backend.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }
            else
            {
                // Reused faulted backend (possibly holding the kill switch): keep it for Disconnect.
                _session = new Session(generation, backend, [], false, new HashSet<string>()) { LastError = error };
            }

            _d.Notifications.Notify(NotificationCategory.ConnectionFailed, "Connection failed", error);
            await FailAsync(error).ConfigureAwait(false);
            return false;
        }
        finally
        {
            Volatile.Write(ref _startingBackend, null);
        }
    }

    /// <summary>The backend a connect is bringing up (before it becomes the session's), for a forced disconnect.</summary>
    private IConnectionBackend? _startingBackend;

    private static bool IsStartFailure(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    private async Task FailAsync(string error)
    {
        if (!_state.TryTransition(ConnectionState.Faulted, error, out _))
        {
            Log.LogDebug("Faulted transition not allowed from {State}.", _state.State);
        }

        // Report what the backend actually does, never what the setting merely asks for.
        var holding = false;
        if (_session?.Backend is { } backend)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                holding = await backend.IsKillSwitchHoldingAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogDebug("Kill switch state query failed: {Error}", ex.Message);
            }
        }

        Publish(s => s with { LastError = error, KillSwitchEngaged = holding, Traffic = null });
        if (holding)
        {
            _d.Notifications.Notify(NotificationCategory.KillSwitchActivated, "Kill switch active", "Internet access is blocked until you reconnect or disconnect.");
        }
    }

    // ------------------------------------------------------------------ pool & preparation

    private sealed record Pool(IReadOnlyList<ProxyProfile> Profiles, bool Smart, IReadOnlySet<string> OwnedIds);

    private async Task<Pool> BuildPoolAsync(ConnectRequest request, AppSettings settings, CancellationToken ct)
    {
        var all = (await _d.Profiles.GetAllAsync(ct).ConfigureAwait(false))
            .Where(p => p.SyncState != ProfileSyncState.RemovedByProvider)
            .ToList();
        var anchorId = request.ProfileId ?? settings.Connection.SelectedProfileId;
        var anchor = anchorId is null ? null : all.FirstOrDefault(p => p.Id == anchorId);
        var smart = request.Smart ?? settings.Connection.SmartMode;

        List<ProxyProfile> pool;
        if (smart)
        {
            var subscriptionId = request.SubscriptionId ?? anchor?.Origin.SubscriptionId;
            pool = subscriptionId is null ? all : all.Where(p => p.Origin.SubscriptionId == subscriptionId).ToList();
        }
        else
        {
            if (anchor is null)
            {
                throw new ConnectionException(anchorId is null ? "Select a server first." : "The selected server no longer exists.");
            }

            pool = [anchor];
        }

        if (pool.Count == 0)
        {
            throw new ConnectionException("There are no servers to connect to. Add a subscription or import a config.");
        }

        var subscriptions = (await _d.Subscriptions.GetAllAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pool)
        {
            var ownership = p.Origin.SubscriptionId is { } sid && subscriptions.TryGetValue(sid, out var sub)
                ? sub.Ownership
                : ProfileOwnershipVerifier.VerifySingleProfile(p, settings.Ownership);
            if (ownership.IsOwned)
            {
                owned.Add(p.Id);
            }
        }

        return new Pool(pool, smart && pool.Count > 1, owned);
    }

    /// <summary>
    /// Two-phase selection so a large pool does not mean a long wait: a quick one-request screen of every
    /// stale node (bounded concurrency, short timeout), then a full multi-sample measurement of only the best
    /// few — the ones that will actually be chosen between. Fresh cached results are reused as they are.
    /// Returns how many nodes were considered.
    /// </summary>
    private async Task<int> SmartTestAsync(IReadOnlyList<ProxyProfile> pool, IReadOnlySet<string> official, AppSettings settings, CancellationToken ct)
    {
        var toTest = ChooseProfilesToTest(pool, official);
        var now = Now;
        var stale = toTest.Where(p => _d.Health.Snapshot(p.Id) is not { LastTestedUtc: { } at } || now - at > settings.Probe.CacheTtl)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (stale.Count > 0)
        {
            await _d.Health.TestAsync(toTest, new ProbeRequestOptions { Quick = true }, ct).ConfigureAwait(false);
            var refine = Rank(toTest, CooledDown(), official).Where(p => stale.Contains(p.Id)).Take(RefineCount).ToList();
            if (refine.Count > 0)
            {
                await _d.Health.TestAsync(refine, new ProbeRequestOptions { Force = true, Samples = 2, ProbeIpv6 = settings.Ipv6 == Ipv6Mode.Auto }, ct).ConfigureAwait(false);
            }
        }

        return toTest.Count;
    }

    /// <summary>
    /// Score bonus of official/owned servers when ranking: they win unless another server is clearly better on
    /// measurement (more than this many points), so official infrastructure is preferred without ever choosing
    /// a measurably worse or failing server.
    /// </summary>
    public const double OfficialPreference = 8;

    /// <summary>Bonus of a server that already carried real traffic on the current network (last 14 days).</summary>
    public const double NetworkPreference = 6;

    /// <summary>Bonus of relay (Iranian bridge) configs on networks known to throttle direct foreign paths.</summary>
    public const double RelayPreference = 6;

    /// <summary>Mobile networks where direct paths to foreign servers stall (MCI); the relay is the reliable path there.</summary>
    private static readonly string[] RelayFirstNetworks = ["cell:43211"];

    /// <summary>Ranking bonus from what is known about this server on the current network.</summary>
    private double Preference(ProxyProfile p, IReadOnlySet<string> official, string network)
    {
        var bonus = official.Contains(p.Id) ? OfficialPreference : 0;
        if (_d.Insights.Get(p.Fingerprint) is { } insight && insight.WorkedRecentlyOn(network, Now))
        {
            bonus += NetworkPreference;
        }

        if (ConfigTraits.IsRelay(p) && RelayFirstNetworks.Contains(network, StringComparer.Ordinal))
        {
            bonus += RelayPreference;
        }

        return bonus;
    }

    /// <summary>
    /// Favourites, servers that worked on this network, official servers and previously good nodes first; never
    /// more than <see cref="PoolTestLimit"/>.
    /// </summary>
    private List<ProxyProfile> ChooseProfilesToTest(IReadOnlyList<ProxyProfile> pool, IReadOnlySet<string> official)
    {
        var network = _d.NetworkKey();
        return pool.Select(p => (Profile: p, Snapshot: _d.Health.Snapshot(p.Id)))
            .OrderByDescending(x => x.Profile.User.IsFavorite)
            .ThenByDescending(x => Preference(x.Profile, official, network))
            .ThenByDescending(x => x.Snapshot.Label is not (HealthLabel.Unknown or HealthLabel.Failed))
            .ThenByDescending(x => x.Snapshot.Score)
            .Take(PoolTestLimit)
            .Select(x => x.Profile)
            .ToList();
    }

    /// <summary>
    /// Best measured nodes first (official ones get <see cref="OfficialPreference"/>); unmeasured nodes only fill
    /// the remaining slots; failed nodes never.
    /// </summary>
    private List<ProxyProfile> Rank(IReadOnlyList<ProxyProfile> pool, IReadOnlySet<string> excluded, IReadOnlySet<string> official)
    {
        var byId = pool.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var network = _d.NetworkKey();
        var snapshots = pool.Where(p => !excluded.Contains(p.Id)).Select(p => _d.Health.Snapshot(p.Id)).ToList();
        return snapshots.Where(s => s.Label is not (HealthLabel.Failed or HealthLabel.Unknown))
            .OrderByDescending(s => s.Score + Preference(byId[s.ProfileId], official, network))
            .ThenBy(s => s.MedianLatencyMs ?? double.MaxValue)
            .Concat(snapshots.Where(s => s.Label == HealthLabel.Unknown))
            .Take(MaxCandidates)
            .Select(s => byId[s.ProfileId])
            .ToList();
    }

    private IReadOnlySet<string> CooledDown()
    {
        var now = Now;
        lock (_cooldownUntil)
        {
            return _cooldownUntil.Where(kv => kv.Value > now).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        }
    }

    private void Cooldown(string profileId, TimeSpan duration)
    {
        lock (_cooldownUntil)
        {
            _cooldownUntil[profileId] = Now + duration;
        }
    }

    internal sealed record PreparedRouting(
        IReadOnlyDictionary<string, string> Paths,
        IReadOnlyList<RuleSetPayload> Payloads,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Downloads/refreshes every rule-set the enabled rules reference. Missing rule-sets make the core skip
    /// those conditions, which is harmless for Direct/Block rules (traffic stays in the tunnel) but would
    /// send traffic outside the tunnel for Proxy rules when the final action is Direct — that case refuses
    /// to connect instead of silently leaking.
    /// </summary>
    private async Task<PreparedRouting> PrepareRuleSetsAsync(AppSettings settings, ConnectionMode mode, CancellationToken ct)
    {
        var routing = settings.Routing;
        var enabledRules = routing.Rules.Where(r => r.Enabled).ToList();
        var referenced = ReferencedTags(enabledRules);
        if (referenced.Count == 0)
        {
            return new PreparedRouting(new Dictionary<string, string>(), [], []);
        }

        var definitions = routing.RuleSets
            .Concat(RoutingPresets.ImpliedRuleSets(enabledRules, routing.RuleSets.ToList()))
            .Where(d => referenced.Contains(d.Tag))
            .ToList();
        // Never wait for downloads while connecting (before the VPN is up they may be blocked or take minutes):
        // use the local copy (cached or shipped with the app) now and refresh in the background.
        var statuses = new List<RuleSetStatus>();
        foreach (var def in definitions.Where(d => d.Enabled))
        {
            statuses.Add(await _d.RuleSets.GetLocalAsync(def, ct).ConfigureAwait(false));
        }

        var refresh = definitions.Where(d => d.Enabled && statuses.Any(s => s.Tag == d.Tag && (!s.Available || s.UsedStaleCache))).ToList();
        if (refresh.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Give the tunnel a moment so the refresh goes through it when it is up.
                    await Task.Delay(TimeSpan.FromSeconds(8), _lifetime.Token).ConfigureAwait(false);
                    await _d.RuleSets.EnsureAsync(refresh, force: false, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.LogDebug("Background rule-set refresh ended: {Error}", ex.Message);
                }
            }, CancellationToken.None);
        }

        var warnings = new List<string>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var payloads = new List<RuleSetPayload>();
        foreach (var status in statuses)
        {
            if (!status.Available || status.LocalPath is null || status.Entry is null)
            {
                warnings.Add($"Rule-set '{status.Tag}' is unavailable ({status.Error}); rules using it are skipped.");
                continue;
            }


            paths[status.Tag] = status.LocalPath;
            if (mode == ConnectionMode.Tun)
            {
                // The service never reads user-writable paths: send the verified bytes instead.
                var bytes = await File.ReadAllBytesAsync(status.LocalPath, ct).ConfigureAwait(false);
                payloads.Add(new RuleSetPayload
                {
                    Tag = status.Tag,
                    Format = status.Entry.Format,
                    Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                    ContentBase64 = Convert.ToBase64String(bytes),
                });
            }
        }

        var missing = referenced.Where(t => !paths.ContainsKey(t)).ToHashSet(StringComparer.Ordinal);
        foreach (var t in missing.Where(t => statuses.All(s => s.Tag != t)))
        {
            warnings.Add($"Rule-set '{t}' is referenced but not defined; rules using it are skipped.");
        }

        if (routing.Final == RuleActionType.Direct)
        {
            var leaking = enabledRules.Where(r => r.Action.Type == RuleActionType.Proxy && ReferencedTags([r]).Any(missing.Contains)).ToList();
            if (leaking.Count > 0)
            {
                throw new ConnectionException(
                    $"Rule '{leaking[0].Name}' sends traffic through the VPN but its rule-set is unavailable, and unmatched traffic goes direct. " +
                    "Connecting now would send that traffic outside the VPN. Update the rule-sets or change the routing.");
            }
        }

        return new PreparedRouting(paths, payloads, warnings);
    }

    private static HashSet<string> ReferencedTags(IEnumerable<RoutingRule> rules)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in rules.SelectMany(r => r.Conditions))
        {
            foreach (var v in c.Values.Select(v => v.Trim()).Where(v => v.Length > 0 && !v.StartsWith('!')))
            {
                switch (c.Type)
                {
                    case RuleConditionType.GeoIp:
                        tags.Add(RoutingPresets.GeoIpTag(v));
                        break;
                    case RuleConditionType.GeoSite:
                        tags.Add(RoutingPresets.GeoSiteTag(v));
                        break;
                    case RuleConditionType.RuleSet:
                        tags.Add(v);
                        break;
                }
            }
        }

        return tags;
    }

    // ------------------------------------------------------------------ session start

    private async Task StartOnAsync(Session session, ProxyProfile profile, PreparedRouting routing, AppSettings settings, CancellationToken ct)
    {
        // Fingerprint/ALPN hardening and the measured handshake variant are applied here; the stored
        // profile stays as the user/provider defined it.
        var tuned = await _d.Tuner.PrepareAsync(profile, ct).ConfigureAwait(false);
        if (settings.Connectivity.AutoPickDns && settings.Dns.Bootstrap.Transport == DnsTransport.Local && _d.BestDns?.Invoke() is { } bestDns)
        {
            // Server names are looked up with the fastest clean resolver of this network instead of the system's.
            settings = settings with { Dns = settings.Dns with { Bootstrap = bestDns } };
        }

        var result = await session.Backend.StartAsync(new BackendStartRequest
        {
            Profile = tuned.Profile,
            Settings = settings,
            TunnelSupportsIpv6 = _d.Health.SupportsIpv6(profile.Id),
            MuxEnabled = tuned.MuxEnabled,
            RuleSetPaths = routing.Paths,
            RuleSetPayloads = routing.Payloads,
            Traffic = _d.Traffic(),
            Limits = _d.Limits(),
        }, ct).ConfigureAwait(false);

        _d.LiveProbe.ResetPing();
        session.Epoch++;
        session.Tuned = tuned;
        session.Profile = profile;
        session.Start = result;
        session.Routing = routing;
        session.ConsecutiveFailures = 0;
        session.ConnectedSince = Now;
        session.Traffic = null;

        // A running core is not a working connection (a dead or blocked server still "starts"): one real request
        // must come back through the tunnel before this counts as connected. Otherwise the caller moves on to the
        // next server or to the rescue (encrypted DNS, clean CDN address, another fingerprint).
        if (_d.VerifyData)
        {
            var sample = await _d.LiveProbe.ProbeAsync(session.Backend.Mode, result.LocalProxyPort, settings.Probe, ct).ConfigureAwait(false);
            if (!sample.Success)
            {
                throw new ConnectionException($"The tunnel started but no data came back through {profile.DisplayName} ({sample.Failure}).");
            }

            await Swallow(() => _d.Health.RecordLiveSampleAsync(profile.Id, sample, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private async Task OnConnectedAsync(Session session, IReadOnlyList<string> routingWarnings, AppSettings settings, bool isSwitch)
    {
        var profile = session.Profile!;
        var start = session.Start!;
        session.History = new ConnectionHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ProfileId = profile.Id,
            StartedUtc = Now,
            Mode = session.Backend.Mode,
            Core = start.Core,
            Outcome = ConnectionOutcome.Connected,
        };
        await Swallow(() => _d.History.AppendAsync(session.History, CancellationToken.None)).ConfigureAwait(false);
        await Swallow(() => _d.Profiles.UpsertAsync(profile with { User = profile.User with { LastUsedUtc = Now } }, CancellationToken.None)).ConfigureAwait(false);

        var warnings = start.Warnings.Concat(routingWarnings).ToList();
        if (session.Backend.Mode == ConnectionMode.SystemProxy)
        {
            warnings.Add("System Proxy mode: applications that ignore the Windows proxy settings are not tunneled. Use TUN mode for full coverage.");
        }

        // Last known exit for this server is shown immediately; a fresh lookup replaces it moments later.
        var known = _d.Insights.Get(profile.Fingerprint)?.Exit;
        var snapshot = _d.Health.Snapshot(profile.Id);
        Publish(_ => new ConnectionStatus
        {
            State = ConnectionState.Connected,
            ProfileId = profile.Id,
            ProfileName = profile.DisplayName,
            Protocol = profile.Protocol.ToString(),
            Mode = session.Backend.Mode,
            Core = start.Core,
            ConnectedSinceUtc = session.ConnectedSince,
            SmartModeActive = session.Smart,
            JackiSmartConnectActive = session.JackiSmart(profile.Id),
            KillSwitchEngaged = settings.KillSwitch.Enabled && session.Backend.Mode == ConnectionMode.Tun,
            PublicIp = known?.Ip,
            ExitCountryCode = known?.CountryCode,
            ExitCountryName = known?.CountryName,
            ExitCity = known?.City,
            ExitIsp = known?.Isp,
            SelectionReason = SelectionReason(session, snapshot),
            LocalProxyPort = start.LocalProxyPort,
            Warnings = warnings,
            Activity = EffectiveActivity,
        });

        if (!isSwitch)
        {
            _d.Notifications.Notify(NotificationCategory.Connected, "Connected", $"{profile.DisplayName} ({session.Backend.Mode})");
            session.WatchdogTask = Task.Run(() => WatchdogAsync(session, session.Watchdog.Token));
            session.PingTask = Task.Run(() => PingLoopAsync(session, session.Watchdog.Token));
        }

        _ = Task.Run(() => PostConnectAsync(session, session.Epoch, settings));
    }

    private string SelectionReason(Session session, NodeHealthSnapshot snapshot)
    {
        var latency = snapshot.MedianLatencyMs is { } ms ? $" · {ms:0} ms" : string.Empty;
        var tuning = (session.Tuned is { Fragment: FragmentVariant.RecordOnly } ? " · fast handshake" : string.Empty)
            + (session.Tuned is { EarlyData: true } ? " · 0-RTT" : string.Empty);
        var mux = session.Tuned is { MuxEnabled: true } ? " · MUX" : string.Empty;
        if (!session.Smart)
        {
            return "Your selected server" + latency + tuning + mux;
        }

        var who = session.JackiSmart(session.Profile!.Id) ? Domain.ProductInfo.SmartConnectName : "Smart";
        return session.TestedCount > 1
            ? $"{who}: best of {session.TestedCount} servers{latency}{tuning}{mux}"
            : $"{who}: best known server{latency}{tuning}{mux}";
    }

    /// <summary>
    /// Live latency on a kept-alive connection (what a "ping" through the VPN would show). The median of the last
    /// three samples is shown with their spread as jitter, so one queued request does not look like a lag spike.
    /// </summary>
    private async Task PingLoopAsync(Session session, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_session == session && _state.State == ConnectionState.Connected && !_background)
                {
                    var ms = await _d.LiveProbe.PingAsync(session.Backend.Mode, session.Start?.LocalProxyPort, _d.Settings().Probe, ct).ConfigureAwait(false);
                    failures = ms is null ? failures + 1 : 0;
                    if (ms is { } sample)
                    {
                        var (median, jitter) = LatencyStats.Add(session.RecentPings, sample);
                        if (_session == session)
                        {
                            Publish(s => s.State == ConnectionState.Connected ? s with { LatencyMs = median, JitterMs = jitter } : s);
                        }
                    }
                    else if (_session == session && failures >= 2)
                    {
                        session.RecentPings.Clear();
                        Publish(s => s.State == ConnectionState.Connected ? s with { LatencyMs = null, JitterMs = null } : s);
                    }
                }

                await Task.Delay(PingInterval, _d.Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogDebug("Latency probe failed: {Error}", ex.Message);
            }
        }
    }

    /// <summary>Re-reads where traffic exits (also after a server switch).</summary>
    public async Task RefreshLocationAsync(CancellationToken ct)
    {
        if (_session is { Profile: { } profile } session && _state.State == ConnectionState.Connected)
        {
            await LocateAsync(session, profile, session.Epoch, _d.Settings(), ct).ConfigureAwait(false);
        }
    }

    private async Task LocateAsync(Session session, ProxyProfile profile, int epoch, AppSettings settings, CancellationToken ct)
    {
        var exit = await _d.LiveProbe.LocateAsync(session.Backend.Mode, session.Start?.LocalProxyPort, settings.Probe, ct).ConfigureAwait(false);
        if (exit is null || session.Epoch != epoch || _session != session)
        {
            return;
        }

        _d.Insights.Update(profile.Fingerprint, i => i with { Exit = exit });
        Publish(s => s with
        {
            PublicIp = exit.Ip,
            ExitCountryCode = exit.CountryCode,
            ExitCountryName = exit.CountryName,
            ExitCity = exit.City,
            ExitIsp = exit.Isp,
        });
        Log.LogInformation("Exit location: {Country} ({Code}){City}.", exit.CountryName ?? "unknown", exit.CountryCode ?? "??", exit.City is null ? string.Empty : ", " + exit.City);
    }

    /// <summary>Background work that must never delay or break the connection.</summary>
    private async Task PostConnectAsync(Session session, int epoch, AppSettings settings)
    {
        var ct = session.Watchdog.Token;
        var profile = session.Profile!;
        try
        {
            if (settings.Probe.CheckPublicIp)
            {
                await LocateAsync(session, profile, epoch, settings, ct).ConfigureAwait(false);
            }

            if (settings.Ipv6 == Ipv6Mode.Auto && _d.Health.SupportsIpv6(profile.Id) is null)
            {
                await _d.Health.TestAsync([profile], new ProbeRequestOptions { Force = true, Samples = 1, ProbeIpv6 = true }, ct).ConfigureAwait(false);
            }

            // Tuning opens many test connections: never while a game or stream is running.
            if (!QuietForOptionalWork && await _d.Tuner.NeedsTuningAsync(profile, ct).ConfigureAwait(false))
            {
                // Measured in isolated probe cores (outside the tunnel); applied from the next connection on.
                await _d.Tuner.TuneAsync(profile, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LogDebug("Post-connect task failed: {Error}", SecretRedactor.Redact(ex.Message));
        }
    }

    // ------------------------------------------------------------------ adopt a running tunnel

    /// <summary>
    /// The service kept a TUN session running while this app was closed or crashed: show it as connected,
    /// resume monitoring and failover, instead of pretending to be disconnected while traffic is tunneled.
    /// Unknown profiles are stopped (nothing may run that the user cannot see).
    /// </summary>
#if WINDOWS
    public async Task<bool> TryAdoptAsync(ServiceTunnelBackend backend, ServiceStatus status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(status);
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null || _state.State != ConnectionState.Disconnected || status.State is not (TunnelState.Running or TunnelState.Restarting))
            {
                return false;
            }

            var profile = status.ProfileId is { } id ? await _d.Profiles.GetAsync(id, ct).ConfigureAwait(false) : null;
            if (profile is null)
            {
                Log.LogWarning("The service is running a tunnel for an unknown server; stopping it.");
                await SafeStopAsync(backend).ConfigureAwait(false);
                return false;
            }

            var settings = _d.Settings();
            var generation = _state.Transition(ConnectionState.Preparing, "re-attaching to running tunnel").Generation;
            var pool = await BuildPoolAsync(new ConnectRequest { ProfileId = profile.Id }, settings, ct).ConfigureAwait(false);
            var routing = await PrepareRuleSetsAsync(settings, ConnectionMode.Tun, ct).ConfigureAwait(false);
            _state.Transition(ConnectionState.Connecting, "re-attaching");
            var session = new Session(generation, backend, pool.Profiles, pool.Smart, pool.OwnedIds)
            {
                Profile = profile,
                Start = new BackendStartResult(status.Core ?? CoreKind.SingBox, status.Chained, [], null),
                Routing = routing,
                ConnectedSince = status.SinceUtc ?? Now,
            };
            session.Epoch++;
            AttachEvents(session);
            _session = session;
            _state.Transition(ConnectionState.Connected, "re-attached");
            await OnConnectedAsync(session, routing.Warnings, settings, isSwitch: false).ConfigureAwait(false);
            Log.LogInformation("Re-attached to the tunnel the service kept running ({Profile}).", profile.DisplayName);
            return true;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            Log.LogWarning("Could not re-attach to the running tunnel: {Error}", SecretRedactor.Redact(ex.Message));
            _session = null;
            _state.TryTransition(ConnectionState.Disconnecting, "re-attach failed", out _);
            _state.TryTransition(ConnectionState.Disconnected, "re-attach failed", out _);
            return false;
        }
        finally
        {
            _op.Release();
        }
    }
#endif

    // ------------------------------------------------------------------ disconnect

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        Volatile.Read(ref _connectCts)?.Cancel();
        _session?.Watchdog.Cancel();
        if (!await _op.WaitAsync(_d.DisconnectGateTimeout, ct).ConfigureAwait(false))
        {
            // Every operation observes the cancellation above, so this is a backstop: never let a stuck
            // operation keep the tunnel (and possibly a blocking kill switch) up after the user said stop.
            Log.LogError("Disconnect: an operation did not yield within {Timeout}s; forcing the tunnel down first.", _d.DisconnectGateTimeout.TotalSeconds);
            if ((_session?.Backend ?? Volatile.Read(ref _startingBackend)) is { } stuck)
            {
                await SafeStopAsync(stuck).ConfigureAwait(false);
            }

            Publish(s => s with { LastError = "Disconnecting… (forced the tunnel down; finishing up)" });
            await _op.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var session = _session;
            if (session is null)
            {
                if (_state.State == ConnectionState.Faulted)
                {
                    _state.TryTransition(ConnectionState.Disconnected, "dismissed", out _);
                    Publish(_ => new ConnectionStatus());
                }

                return;
            }

            _state.TryTransition(ConnectionState.Disconnecting, "user disconnect", out _);
            var wasConnected = session.History is not null;
            _stopNotConfirmed = false;
            await DetachSessionAsync(session, stopBackend: true, ConnectionOutcome.Disconnected, null).ConfigureAwait(false);
            _state.TryTransition(ConnectionState.Disconnected, "disconnected", out _);
            Publish(_ => _stopNotConfirmed ? new ConnectionStatus { LastError = StopNotConfirmedMessage } : new ConnectionStatus());
            if (wasConnected)
            {
                _d.Notifications.Notify(NotificationCategory.Disconnected, "Disconnected", "The VPN connection was closed.");
            }
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>Ends a session's bookkeeping; optionally stops its backend (releases proxy / kill switch).</summary>
    private async Task DetachSessionAsync(Session session, bool stopBackend, ConnectionOutcome outcome, string? error)
    {
        session.Watchdog.Cancel();
        DetachEvents(session);
        await CloseHistoryAsync(session, outcome, error).ConfigureAwait(false);
        if (stopBackend)
        {
            await SafeStopAsync(session.Backend).ConfigureAwait(false);
            await session.Backend.DisposeAsync().ConfigureAwait(false);
        }

        if (_session == session)
        {
            _session = null;
        }
    }

    private async Task CloseHistoryAsync(Session session, ConnectionOutcome outcome, string? error)
    {
        if (session.History is not { } h)
        {
            return;
        }

        session.History = null;
        var closed = h with
        {
            EndedUtc = Now,
            Outcome = outcome,
            Error = error is null ? null : SecretRedactor.Redact(error),
            BytesUp = session.Traffic?.UploadBytesTotal ?? 0,
            BytesDown = session.Traffic?.DownloadBytesTotal ?? 0,
        };
        await Swallow(() => _d.History.UpdateAsync(closed, CancellationToken.None)).ConfigureAwait(false);
    }

    public const string StopNotConfirmedMessage =
        "The VPN did not confirm that it stopped. If the internet does not work, use \"Emergency network restore\" (tray menu or Start menu).";

    /// <summary>Stops a backend; false when it could not confirm (the user is then told how to restore the network).</summary>
    private async Task<bool> SafeStopAsync(IConnectionBackend backend)
    {
        try
        {
            // The service answers within ~15 s even when the session is stuck (it then force-stops).
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await backend.StopAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LogWarning("Stopping the {Mode} backend failed: {Error}", backend.Mode, SecretRedactor.Redact(ex.Message));
            _stopNotConfirmed = true;
            return false;
        }
    }

    private volatile bool _stopNotConfirmed;

    // ------------------------------------------------------------------ traffic control

    /// <summary>Open connections of the running session (per-app usage), or null.</summary>
    public async Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct)
    {
        if (_session is not { } session || _state.State != ConnectionState.Connected)
        {
            return null;
        }

        try
        {
            return await session.Backend.GetConnectionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Log.LogDebug("Connection list unavailable: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>New relay limits for the running session (mode switch; never reconnects).</summary>
    public async Task ApplyTrafficLimitsAsync(Domain.Usage.TrafficLimits limits, CancellationToken ct)
    {
        if (_session is { } session && _state.State == ConnectionState.Connected)
        {
            await session.Backend.SetTrafficLimitsAsync(limits, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Restarts the session on the same server (a setting that lives in the core configuration changed).</summary>
    public async Task<bool> ReconnectAsync(string reason, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not { Profile: { } current } session || _state.State != ConnectionState.Connected)
            {
                return false;
            }

            Log.LogInformation("Reconnecting on the same server: {Reason}.", reason);
            return await SwitchLockedAsync(session, current, reason, ConnectionOutcome.Disconnected, ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    // ------------------------------------------------------------------ manual switch

    /// <summary>User picked another server while connected.</summary>
    public async Task<bool> SwitchToAsync(string profileId, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not { } session || _state.State != ConnectionState.Connected)
            {
                return false;
            }

            var target = await _d.Profiles.GetAsync(profileId, ct).ConfigureAwait(false) ?? throw new ConnectionException("The selected server no longer exists.");
            return await SwitchLockedAsync(session, target, "user switch", ConnectionOutcome.Disconnected, ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task<bool> SwitchLockedAsync(Session session, ProxyProfile target, string reason, ConnectionOutcome previousOutcome, CancellationToken ct)
    {
        var settings = _d.Settings();
        var previous = session.Profile!;
        if (!_state.TryTransition(ConnectionState.SwitchingServer, reason, out _))
        {
            return false;
        }

        await CloseHistoryAsync(session, previousOutcome, null).ConfigureAwait(false);
        try
        {
            await StartOnAsync(session, target, session.Routing ?? new PreparedRouting(new Dictionary<string, string>(), [], []), settings, ct).ConfigureAwait(false);
            _state.Transition(ConnectionState.Connected, "switched");
            RecordSwitch();
            await OnConnectedAsync(session, session.Routing?.Warnings ?? [], settings, isSwitch: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            var error = SecretRedactor.Redact(ex.Message);
            Log.LogWarning("Switching to {Target} failed: {Error}; returning to {Previous}.", target.DisplayName, error, previous.DisplayName);
            await RecordFailureAsync(target, error, CancellationToken.None).ConfigureAwait(false);
            return await RecoverLockedAsync(session, $"switch to {target.DisplayName} failed", ConnectionOutcome.Failed, preferProfile: previous).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ recovery & failover

    private void AttachEvents(Session session)
    {
        session.FaultHandler = (_, fault) =>
        {
            var epoch = session.Epoch;
            _ = Task.Run(() => OnBackendFaultAsync(session, epoch, fault));
        };
        session.TrafficHandler = (_, t) =>
        {
            session.Traffic = t;
            Publish(s => s.State == ConnectionState.Connected ? s with { Traffic = t } : s);
        };
        session.Backend.Faulted += session.FaultHandler;
        session.Backend.Traffic += session.TrafficHandler;
    }

    private static void DetachEvents(Session session)
    {
        if (session.FaultHandler is not null)
        {
            session.Backend.Faulted -= session.FaultHandler;
        }

        if (session.TrafficHandler is not null)
        {
            session.Backend.Traffic -= session.TrafficHandler;
        }
    }

    private async Task OnBackendFaultAsync(Session session, int epoch, BackendFault fault)
    {
        var profile = session.Profile;
        if (profile is null)
        {
            return;
        }

        _d.Disconnects.Record(profile.Id);
        if (fault.WillRetryInternally)
        {
            // The service restarts the core itself (kill switch holding); just tell the user.
            _d.Notifications.Notify(NotificationCategory.CoreCrashed, "Connection interrupted", "The VPN core stopped unexpectedly and is being restarted.");
            return;
        }

        try
        {
            await _op.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_session != session || session.Epoch != epoch || _state.State is not (ConnectionState.Connected or ConnectionState.Reconnecting or ConnectionState.SwitchingServer))
            {
                return;
            }

            Log.LogWarning("Backend fault on {Profile}: {Reason}", profile.DisplayName, SecretRedactor.Redact(fault.Reason));
            _d.Notifications.Notify(NotificationCategory.CoreCrashed, "Connection interrupted", fault.KillSwitchHolding
                ? "The VPN core stopped. The kill switch is blocking traffic while reconnecting."
                : "The VPN core stopped. Reconnecting…");
            await RecoverLockedAsync(session, fault.Reason, ConnectionOutcome.CoreCrashed, preferProfile: profile).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// Reconnect with exponential backoff; in smart mode, after one failed retry move on to the best other
    /// nodes (the failed one goes on cooldown). Gives up into <see cref="ConnectionState.Faulted"/>.
    /// Caller holds the operation gate.
    /// </summary>
    private async Task<bool> RecoverLockedAsync(Session session, string reason, ConnectionOutcome outcome, ProxyProfile preferProfile)
    {
        var settings = _d.Settings();
        var ct = session.Watchdog.Token;
        if (!_state.TryTransition(ConnectionState.Reconnecting, reason, out _)
            && _state.State != ConnectionState.Reconnecting)
        {
            return false;
        }

        await CloseHistoryAsync(session, outcome, reason).ConfigureAwait(false);
        var routing = session.Routing ?? new PreparedRouting(new Dictionary<string, string>(), [], []);
        var attempts = Math.Max(1, session.Smart ? Math.Min(1, settings.Connection.MaxReconnectAttempts) : settings.Connection.MaxReconnectAttempts);
        string? lastError = null;
        try
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var delay = TimeSpan.FromTicks(_d.ReconnectBaseDelay.Ticks * (1L << Math.Min(attempt - 1, 5)));
                Publish(s => s with { LastError = $"Reconnecting (attempt {attempt}/{attempts})…" });
                await Task.Delay(delay, _d.Time, ct).ConfigureAwait(false);
                try
                {
                    await StartOnAsync(session, preferProfile, routing, settings, ct).ConfigureAwait(false);
                    _state.Transition(ConnectionState.Connected, "reconnected");
                    await OnConnectedAsync(session, routing.Warnings, settings, isSwitch: true).ConfigureAwait(false);
                    Log.LogInformation("Reconnected to {Profile} after {Attempt} attempt(s).", preferProfile.DisplayName, attempt);
                    return true;
                }
                catch (Exception ex) when (IsStartFailure(ex))
                {
                    lastError = SecretRedactor.Redact(ex.Message);
                    await RecordFailureAsync(preferProfile, lastError, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (session.Smart)
            {
                Cooldown(preferProfile.Id, settings.Smart.Failover.FailedNodeCooldown);
                var excluded = CooledDown().Append(preferProfile.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var candidate in Rank(session.Pool, excluded, session.OwnedIds))
                {
                    if (!_state.TryTransition(ConnectionState.SwitchingServer, "failover", out _))
                    {
                        break;
                    }

                    try
                    {
                        await StartOnAsync(session, candidate, routing, settings, ct).ConfigureAwait(false);
                        _state.Transition(ConnectionState.Connected, "failed over");
                        RecordSwitch();
                        await OnConnectedAsync(session, routing.Warnings, settings, isSwitch: true).ConfigureAwait(false);
                        _d.Notifications.Notify(NotificationCategory.AutomaticFailover, "Switched server", $"{preferProfile.DisplayName} stopped working; now using {candidate.DisplayName}.");
                        return true;
                    }
                    catch (Exception ex) when (IsStartFailure(ex))
                    {
                        lastError = SecretRedactor.Redact(ex.Message);
                        await RecordFailureAsync(candidate, lastError, CancellationToken.None).ConfigureAwait(false);
                        Cooldown(candidate.Id, settings.Smart.Failover.FailedNodeCooldown);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect requested during recovery; DisconnectAsync finishes the teardown.
            return false;
        }

        session.LastError = lastError ?? reason;
        session.Watchdog.Cancel();
        _d.Notifications.Notify(NotificationCategory.ConnectionFailed, "Connection lost", settings.KillSwitch.Enabled
            ? "Could not reconnect. The kill switch keeps blocking traffic until you disconnect."
            : "Could not reconnect. Your connection is no longer protected.");
        await FailAsync($"Could not restore the connection: {session.LastError}").ConfigureAwait(false);
        return false;
    }

    private async Task RecordFailureAsync(ProxyProfile profile, string error, CancellationToken ct)
    {
        // A tuned handshake variant that stops working is dropped; the server's own settings are used next.
        _d.Tuner.Invalidate(profile);
        await RecordFailureSampleAsync(profile, error, ct).ConfigureAwait(false);
    }

    private async Task RecordFailureSampleAsync(ProxyProfile profile, string error, CancellationToken ct) =>
        await Swallow(() => _d.Health.RecordLiveSampleAsync(profile.Id, new HealthSample
        {
            TimestampUtc = Now,
            Success = false,
            Failure = ProbeFailureKind.CoreStartFailed,
            Detail = error,
        }, ct)).ConfigureAwait(false);

    private void RecordSwitch()
    {
        lock (_switches)
        {
            _switches.Enqueue(Now);
        }
    }

    private int SwitchesInLastHour()
    {
        var cutoff = Now - TimeSpan.FromHours(1);
        lock (_switches)
        {
            while (_switches.TryPeek(out var t) && t < cutoff)
            {
                _switches.Dequeue();
            }

            return _switches.Count;
        }
    }

    // ------------------------------------------------------------------ watchdog

    /// <summary>
    /// Real requests through the live connection at the configured interval. Failures feed the hysteresis
    /// failover engine; proactive (non-failure) moves happen only between owned (Jacki) nodes.
    /// </summary>
    /// <summary>Minimum spacing of background re-tests of the owned pool during a session (bursts cause lag spikes).</summary>
    public static TimeSpan PoolRefreshInterval { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Downloaded bytes between two checks that prove the tunnel is alive even if a probe timed out.</summary>
    public const long AliveTrafficBytes = 64 * 1024;

    /// <summary>Above this download rate the user is busy: background pool tests wait (bytes/s).</summary>
    public const long BusyBytesPerSecond = 256 * 1024;

    private async Task WatchdogAsync(Session session, CancellationToken ct)
    {
        var nextPoolRefresh = Now + Max(_d.Settings().Probe.CacheTtl, PoolRefreshInterval);
        session.WatchdogDownloadMark = session.Traffic?.DownloadBytesTotal ?? 0;
        while (!ct.IsCancellationRequested)
        {
            var settings = _d.Settings();
            try
            {
                var interval = settings.Smart.Failover.HealthCheckInterval;
                await Task.Delay(_background ? interval * 4 : interval, _d.Time, ct).ConfigureAwait(false);
                if (_session != session || _state.State != ConnectionState.Connected || session.Profile is not { } current)
                {
                    continue;
                }

                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    continue; // no network at all is not the node's fault; the network monitor handles it
                }

                var epoch = session.Epoch;
                var sample = await _d.LiveProbe.ProbeAsync(session.Backend.Mode, session.Start?.LocalProxyPort, settings.Probe, ct).ConfigureAwait(false);
                if (session.Epoch != epoch)
                {
                    continue;
                }

                // A probe that times out while the tunnel is busy delivering data (a big download, a 4K stream)
                // says nothing about the server: counting it would tear down a working connection.
                var downloaded = session.Traffic?.DownloadBytesTotal ?? 0;
                var flowing = downloaded - session.WatchdogDownloadMark >= AliveTrafficBytes;
                session.WatchdogDownloadMark = downloaded;
                if (!sample.Success && flowing)
                {
                    Log.LogDebug("Health probe failed while data is flowing ({Failure}); not counted.", sample.Failure);
                    continue;
                }

                await _d.Health.RecordLiveSampleAsync(current.Id, sample, ct).ConfigureAwait(false);
                session.ConsecutiveFailures = sample.Success ? 0 : session.ConsecutiveFailures + 1;
                if (sample.Success && downloaded >= WorkedBytes && _rememberedSession != session)
                {
                    // Real data passed (not just a handshake): remember this server for this network.
                    _rememberedSession = session;
                    var network = _d.NetworkKey();
                    var at = Now;
                    _d.Insights.Update(current.Fingerprint, i => i with
                    {
                        WorkedOn = new Dictionary<string, DateTimeOffset>(i.WorkedOn ?? new Dictionary<string, DateTimeOffset>(), StringComparer.Ordinal) { [network] = at },
                    });
                }

                // In a game or stream nothing optional runs: no pool tests (bursts of handshakes = lag spikes)
                // and no proactive server moves (a move drops every open connection).
                var quiet = QuietForOptionalWork;
                var proactive = session.Smart && settings.Smart.ProactiveSwitching && session.JackiSmart(current.Id) && !quiet;
                var busy = (session.Traffic?.DownloadBytesPerSecond ?? 0) > BusyBytesPerSecond;
                if (proactive && !busy && Now >= nextPoolRefresh)
                {
                    var owned = session.Pool.Where(p => session.OwnedIds.Contains(p.Id) && p.Id != current.Id).ToList();
                    await _d.Health.TestAsync(owned, new ProbeRequestOptions { Quick = true }, ct).ConfigureAwait(false);
                    nextPoolRefresh = Now + Max(settings.Probe.CacheTtl, PoolRefreshInterval);
                }

                var decision = Decide(session, current, settings, proactive);
                if (decision.Kind == FailoverDecisionKind.Stay)
                {
                    continue;
                }

                Log.LogInformation("Watchdog: {Decision} ({Reason}).", decision.Kind, decision.Reason);
                await _op.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_session != session || session.Epoch != epoch || _state.State != ConnectionState.Connected)
                    {
                        continue;
                    }

                    var target = decision.TargetProfileId is { } id ? session.Pool.FirstOrDefault(p => p.Id == id) : null;
                    switch (decision.Kind)
                    {
                        case FailoverDecisionKind.Reconnect:
                            _d.Disconnects.Record(current.Id);
                            await RecoverLockedAsync(session, decision.Reason, ConnectionOutcome.Failed, current).ConfigureAwait(false);
                            break;
                        case FailoverDecisionKind.SwitchForFailure when target is not null:
                            _d.Disconnects.Record(current.Id);
                            Cooldown(current.Id, settings.Smart.Failover.FailedNodeCooldown);
                            if (await SwitchLockedAsync(session, target, decision.Reason, ConnectionOutcome.FailedOver, ct).ConfigureAwait(false))
                            {
                                _d.Notifications.Notify(NotificationCategory.AutomaticFailover, "Switched server", $"{current.DisplayName} stopped responding; now using {target.DisplayName}.");
                            }

                            break;
                        case FailoverDecisionKind.SwitchProactive when target is not null:
                            if (await SwitchLockedAsync(session, target, decision.Reason, ConnectionOutcome.FailedOver, ct).ConfigureAwait(false))
                            {
                                _d.Notifications.Notify(NotificationCategory.AutomaticFailover, "Jacki Smart Connect", $"Moved to a faster server: {target.DisplayName}.");
                            }

                            break;
                    }
                }
                finally
                {
                    _op.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogWarning("Watchdog iteration failed: {Error}", SecretRedactor.Redact(ex.Message));
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // ------------------------------------------------------------------ gaming / streaming

    private ActivityProfile _detectedActivity;
    private volatile bool _background;

    private ActivityProfile EffectiveActivity => ActivityRules.Effective(_d.Settings().Performance.Activity, _detectedActivity);

    /// <summary>
    /// Mobile battery saving: with the app in the background (or the screen off) the live ping stops (nobody
    /// sees it), health checks run 4× less often and optional work (pool tests, tuning) waits.
    /// </summary>
    public void SetBackground(bool background)
    {
        if (_background != background)
        {
            _background = background;
            Log.LogDebug("App is now in the {Where}.", background ? "background" : "foreground");
        }
    }

    private bool QuietForOptionalWork => _background || ActivityRules.IsQuiet(EffectiveActivity);

    /// <summary>Platform detectors (foreground game, full-screen video, broadcasting app) report here.</summary>
    public void ReportActivity(ActivityProfile detected)
    {
        if (_detectedActivity == detected)
        {
            return;
        }

        _detectedActivity = detected;
        var effective = EffectiveActivity;
        Log.LogInformation("Activity: {Detected} (effective {Effective}).", detected, effective);
        Publish(s => s with { Activity = effective });
    }

    private FailoverDecision Decide(Session session, ProxyProfile current, AppSettings settings, bool proactive)
    {
        var policy = settings.Smart.Failover;
        if (!session.Smart)
        {
            return session.ConsecutiveFailures >= policy.ConsecutiveFailuresToFailover
                ? new FailoverDecision(FailoverDecisionKind.Reconnect, current.Id, $"{session.ConsecutiveFailures} consecutive failed checks")
                : new FailoverDecision(FailoverDecisionKind.Stay, null, "healthy or below threshold");
        }

        var failing = session.ConsecutiveFailures >= policy.ConsecutiveFailuresToFailover;
        var candidatePool = failing ? session.Pool : session.Pool.Where(p => session.OwnedIds.Contains(p.Id));
        Dictionary<string, DateTimeOffset> cooldowns;
        lock (_cooldownUntil)
        {
            cooldowns = new Dictionary<string, DateTimeOffset>(_cooldownUntil, StringComparer.Ordinal);
        }

        return FailoverEngine.Decide(new FailoverContext
        {
            CurrentProfileId = current.Id,
            Current = _d.Health.Snapshot(current.Id),
            ConsecutiveFailures = session.ConsecutiveFailures,
            ConnectedSinceUtc = session.ConnectedSince,
            Candidates = candidatePool.Select(p => _d.Health.Snapshot(p.Id)).ToList(),
            CooldownUntil = cooldowns,
            SwitchesInLastHour = SwitchesInLastHour(),
            ProactiveAllowed = proactive,
            NowUtc = Now,
        }, policy);
    }

    // ------------------------------------------------------------------ network changes

    /// <summary>Platform network monitors report changes here (Windows address/route monitor, Android ConnectivityManager).</summary>
    public void NotifyNetworkChanged(bool becameUnavailable, string reason)
    {
        _ = Task.Run(() => HandleNetworkChangeAsync(becameUnavailable, reason));
        if (!becameUnavailable)
        {
            NetworkChanged?.Invoke(this, reason);
        }
    }

    private async Task HandleNetworkChangeAsync(bool becameUnavailable, string reason)
    {
        var session = _session;
        if (session is null || _state.State != ConnectionState.Connected || session.Profile is not { } profile)
        {
            return;
        }

        if (becameUnavailable)
        {
            Publish(s => s with { LastError = "Network unavailable — waiting for it to come back." });
            return;
        }

        var ct = session.Watchdog.Token;
        try
        {
            // Give the new route a moment, then verify with a real request before tearing anything down.
            await Task.Delay(TimeSpan.FromSeconds(2), _d.Time, ct).ConfigureAwait(false);
            var settings = _d.Settings();
            var epoch = session.Epoch;
            var sample = await _d.LiveProbe.ProbeAsync(session.Backend.Mode, session.Start?.LocalProxyPort, settings.Probe, ct).ConfigureAwait(false);
            if (sample.Success)
            {
                Publish(s => s with { LastError = null });
                return;
            }

            await _op.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_session == session && session.Epoch == epoch && _state.State == ConnectionState.Connected)
                {
                    Log.LogInformation("Connection broken after network change ({Reason}); reconnecting.", reason);
                    await RecoverLockedAsync(session, "network changed: " + reason, ConnectionOutcome.NetworkLost, profile).ConfigureAwait(false);
                }
            }
            finally
            {
                _op.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ plumbing

    private void Publish(Func<ConnectionStatus, ConnectionStatus> update)
    {
        ConnectionStatus snapshot;
        lock (_statusGate)
        {
            _status = update(_status) with { State = _state.State };
            snapshot = _status;
        }

        StatusChanged?.Invoke(this, snapshot);
    }

    private async Task Swallow(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LogDebug("Non-critical persistence failed: {Error}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class Session(long generation, IConnectionBackend backend, IReadOnlyList<ProxyProfile> pool, bool smart, IReadOnlySet<string> ownedIds)
    {
        public long Generation { get; } = generation;
        public IConnectionBackend Backend { get; } = backend;
        public IReadOnlyList<ProxyProfile> Pool { get; } = pool;
        public bool Smart { get; } = smart;
        public IReadOnlySet<string> OwnedIds { get; } = ownedIds;
        public CancellationTokenSource Watchdog { get; } = new();
        public Task? WatchdogTask { get; set; }
        public Task? PingTask { get; set; }
        public TunedProfile? Tuned { get; set; }
        public int TestedCount { get; init; }
        public ProxyProfile? Profile { get; set; }
        public BackendStartResult? Start { get; set; }
        public PreparedRouting? Routing { get; set; }
        public ConnectionHistoryEntry? History { get; set; }
        public DateTimeOffset ConnectedSince { get; set; }
        public TrafficSnapshot? Traffic { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? LastError { get; set; }
        public EventHandler<BackendFault>? FaultHandler { get; set; }
        public EventHandler<TrafficSnapshot>? TrafficHandler { get; set; }

        /// <summary>Downloaded bytes at the previous watchdog check (data flowing = the tunnel is alive).</summary>
        public long WatchdogDownloadMark { get; set; }

        /// <summary>Last live latency samples (median and spread are shown, not single spikes).</summary>
        public Queue<double> RecentPings { get; } = new();

        /// <summary>Increments on every successful start so events from a replaced core are ignored.</summary>
        public int Epoch;

        /// <summary>Jacki Smart Connect applies only to nodes verified as the owner's infrastructure.</summary>
        public bool JackiSmart(string profileId) => Smart && OwnedIds.Contains(profileId);
    }
}
