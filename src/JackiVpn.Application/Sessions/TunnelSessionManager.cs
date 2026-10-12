using System.Security.Cryptography;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Routing;
using JackiVpn.Ipc;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.KillSwitch;
using JackiVpn.Networking.Recovery;
using JackiVpn.Routing;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Sessions;

public sealed class TunnelRequestException(string message) : Exception(message);

/// <summary>
/// Service-side TUN session. Order of operations is chosen so traffic can never leak:
/// start = kill switch (if enabled) → core; stop = core → kill switch → DNS flush. If the core dies while
/// the kill switch is engaged, the filters stay up (fail closed) while the core is restarted with backoff;
/// if restarting fails the tunnel is Failed and traffic stays blocked until the user disconnects.
/// <para>
/// Stop always wins: it pre-empts any start or crash-restart in progress (they observe a pre-emption token), and
/// if the session still cannot be taken over within <see cref="StopGateTimeout"/> it tears down without it —
/// cores are killed and the kill switch removed — so a hung or crash-looping core can never leave the user with
/// a blocked network that "Disconnect" cannot release.
/// </para>
/// </summary>
public sealed class TunnelSessionManager : IAsyncDisposable
{
    public const int MaxRestarts = 3;

    private readonly Func<SessionRequest, CancellationToken, Task<SessionPlan>> _planFactory;
    private readonly Func<ISessionRunner> _runnerFactory;
    private readonly IKillSwitch _killSwitch;
    private readonly NetworkStateJournal _journal;
    private readonly string _ruleSetDir;
    private readonly IReadOnlyList<string> _coreExecutables;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISessionRunner? _runner;
    private TunnelStartRequest? _request;
    private SessionPlan? _plan;
    private string? _killSwitchJournalId;
    private string? _adapterJournalId;
    private CancellationTokenSource? _sessionCts;

    // Cancelled (and replaced) by Stop and by a newer Start; every long operation links to the token it captured.
    // Old sources are never disposed: in-flight operations may still hold their tokens.
    private CancellationTokenSource _preempt = new();
    private ServiceStatus _status = new();
    private long _lastUp;
    private long _lastDown;

    public TunnelSessionManager(
        Func<SessionRequest, CancellationToken, Task<SessionPlan>> planFactory,
        Func<ISessionRunner> runnerFactory,
        IKillSwitch killSwitch,
        NetworkStateJournal journal,
        string ruleSetDirectory,
        IReadOnlyList<string> coreExecutables,
        ILogger logger,
        TimeProvider? time = null)
    {
        _planFactory = planFactory;
        _runnerFactory = runnerFactory;
        _killSwitch = killSwitch;
        _journal = journal;
        _ruleSetDir = ruleSetDirectory;
        _coreExecutables = coreExecutables;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<ServiceStatus>? StatusChanged;

    public event EventHandler<CoreExitedEvent>? CoreExited;

    public event EventHandler<TrafficEvent>? Traffic;

    public ServiceStatus Status => _status;

    public TimeSpan RestartBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long Stop waits to take over the session before tearing down without it.</summary>
    public TimeSpan StopGateTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Flushes the Windows resolver cache after a session (replaceable in tests).</summary>
    public Action FlushDns { get; init; } = static () => Networking.Adapters.NetworkInspector.FlushDnsCache();

    /// <summary>Cancels whatever start/restart is running and returns the token for the next operation.</summary>
    private CancellationToken Preempt()
    {
        var fresh = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _preempt, fresh);
        try
        {
            old.Cancel();
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug("Pre-empted operation threw while cancelling: {Error}", ex.InnerException?.Message);
        }

        return fresh.Token;
    }

    public async Task<TunnelStartResponse> StartAsync(TunnelStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A new start (server switch) supersedes a crash-restart still in progress.
        using var op = CancellationTokenSource.CreateLinkedTokenSource(ct, Preempt());
        ct = op.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Validate(request);

            // Server switch / restart while the kill switch holds: keep the filters up across the swap
            // (Enable below replaces them atomically), so there is no unprotected window between sessions.
            var holding = _killSwitchJournalId is not null && request.KillSwitch.Enabled;
            if (_runner is not null)
            {
                await StopCoreAsync(keepKillSwitch: holding).ConfigureAwait(false);
            }
            else if (_killSwitchJournalId is not null && !request.KillSwitch.Enabled)
            {
                SafeDisableKillSwitch();
            }

            var ruleSetPaths = PersistRuleSets(request.RuleSets);
            SetStatus(_status with { State = TunnelState.Starting, ProfileId = request.Profile.Id, LastError = null, RestartCount = 0, UploadTotal = 0, DownloadTotal = 0 });

            if (request.KillSwitch.Enabled)
            {
                _killSwitchJournalId ??= _journal.Record(JournalEntryKind.KillSwitch, new { request.KillSwitch.AllowLan }, "kill-switch");
                try
                {
                    _killSwitch.Enable(new KillSwitchOptions { AllowLan = request.KillSwitch.AllowLan, AllowedApplications = _coreExecutables });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Never connect without the kill switch the user asked for. If filters were already holding
                    // from the previous session they stay (fail closed); otherwise nothing was installed.
                    if (!holding)
                    {
                        SafeDisableKillSwitch();
                    }

                    SetStatus(_status with { State = TunnelState.Failed, LastError = "Kill switch could not be enabled: " + ex.Message, KillSwitchEngaged = holding });
                    return new TunnelStartResponse(false, CoreKind.SingBox, false, _status.LastError, [], null);
                }
            }

            _adapterJournalId = _journal.Record(JournalEntryKind.TunAdapter, new TunAdapterJournalData(Domain.ProductInfo.TunInterfaceName), "tun-adapter");
            var sessionRequest = new SessionRequest
            {
                Profile = request.Profile,
                Mode = ConnectionMode.Tun,
                Core = request.Core,
                Dns = request.Dns,
                Ipv6 = request.Ipv6,
                TunnelSupportsIpv6 = request.TunnelSupportsIpv6,
                Routing = request.Routing,
                RuleSetPaths = ruleSetPaths,
                Mtu = request.Mtu,
                MuxEnabled = request.MuxEnabled,
                DebugLogging = request.DebugLogging,
                Performance = request.Performance,
                Traffic = request.Traffic,
                ListenPort = request.SystemProxyPort,
                TunSystemProxy = request.SystemProxyPort > 0,
            };
            _limits = request.Limits;

            try
            {
                _plan = await _planFactory(sessionRequest, ct).ConfigureAwait(false);
                _request = request;
                var notes = await LaunchAsync(ct).ConfigureAwait(false);

                // Pre-empted while a step ignored the token: never report Running; the stop waiting for the gate cleans up.
                ct.ThrowIfCancellationRequested();
                SetStatus(_status with
                {
                    State = TunnelState.Running,
                    Core = _plan.Selection.Core,
                    Chained = _plan.Chained,
                    SinceUtc = _time.GetUtcNow(),
                    KillSwitchEngaged = request.KillSwitch.Enabled,
                    ProxyPort = _plan.Frontend.TunProxyPort,
                });
                return new TunnelStartResponse(true, _plan.Selection.Core, _plan.Chained, null, _plan.Warnings.Concat(notes).ToArray(), _plan.Selection.Report) { ProxyPort = _plan.Frontend.TunProxyPort };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                var error = SecretRedactor.Redact(ex.Message);
                _logger.LogError("TUN session failed to start: {Error}", error);
                // A failed switch while protected keeps blocking (the user expected to stay protected);
                // a failed first connect releases, since the user was never told they were protected.
                await StopCoreAsync(keepKillSwitch: holding).ConfigureAwait(false);
                SetStatus(_status with { State = TunnelState.Failed, LastError = error, KillSwitchEngaged = holding });
                return new TunnelStartResponse(false, _plan?.Selection.Core ?? CoreKind.SingBox, _plan?.Chained ?? false, error, [], _plan?.Selection.Report);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> LaunchAsync(CancellationToken ct)
    {
        _sessionCts?.Cancel();
        _sessionCts = new CancellationTokenSource();
        var runner = _runnerFactory();
        runner.CoreExited += OnCoreExited;
        runner.SetTrafficLimits(_limits);
        _runner = runner;
        var notes = await runner.StartAsync(_plan!, ct).ConfigureAwait(false);
        _ = Task.Run(() => TrafficLoopAsync(runner, _sessionCts.Token), CancellationToken.None);
        return notes;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Preempt();
        SetStatus(_status with { State = TunnelState.Stopping });
        bool acquired;
        try
        {
            acquired = await _gate.WaitAsync(StopGateTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            acquired = false;
        }

        if (!acquired)
        {
            // Something holds the session and did not yield to the pre-emption: tear down without it.
            _logger.LogError("Stop could not take over the session within {Timeout}s; forcing the tunnel down.", StopGateTimeout.TotalSeconds);
            EmergencyTeardown();
            SetStatus(new ServiceStatus { State = TunnelState.Stopped, ServiceVersion = _status.ServiceVersion, LastError = "The tunnel was force-stopped." });
            return;
        }

        try
        {
            await StopCoreAsync(keepKillSwitch: false).ConfigureAwait(false);
            SetStatus(new ServiceStatus { State = TunnelState.Stopped, ServiceVersion = _status.ServiceVersion });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hard stop of a cancelled connect whose graceful stop hangs: the cores die and the kill-switch filters go at
    /// once (the network works again), then the normal stop runs once whatever holds the session yields — it also
    /// removes filters a start that was still in flight may have installed after this.
    /// </summary>
    public void Kill()
    {
        Preempt();
        _logger.LogWarning("Tunnel kill requested (cancelled connect).");
        EmergencyTeardown();
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError("Teardown after kill failed: {Error}", ex.Message);
            }
        });
    }

    /// <summary>
    /// Last resort, without the session gate: kills this session's core processes (only if they are still our
    /// own core executables — never a recycled PID) and removes the kill-switch filters, so the network works again.
    /// </summary>
    private void EmergencyTeardown()
    {
        _sessionCts?.Cancel();
        foreach (var pid in _runner?.ProcessIds ?? [])
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                var path = process.MainModule?.FileName;
                if (path is not null && _coreExecutables.Any(c => string.Equals(Path.GetFullPath(c), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                {
                    process.Kill(entireProcessTree: true);
                    _logger.LogWarning("Force-killed core process {Pid}.", pid);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                _logger.LogDebug("Core {Pid} was already gone or not accessible: {Error}", pid, ex.Message);
            }
        }

        SafeDisableKillSwitch();
        try
        {
            FlushDns();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("DNS flush failed: {Error}", ex.Message);
        }
    }

    /// <summary>User-initiated release of a fail-closed kill switch after a failed session.</summary>
    public async Task ReleaseKillSwitchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runner is null)
            {
                SafeDisableKillSwitch();
                SetStatus(_status with { KillSwitchEngaged = false });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(bool keepKillSwitch)
    {
        _sessionCts?.Cancel();
        if (_runner is not null)
        {
            _runner.CoreExited -= OnCoreExited;
            await _runner.StopAsync().ConfigureAwait(false);
            await _runner.DisposeAsync().ConfigureAwait(false);
            _runner = null;
        }

        if (_adapterJournalId is not null)
        {
            _journal.Complete(_adapterJournalId);
            _adapterJournalId = null;
        }

        if (!keepKillSwitch)
        {
            SafeDisableKillSwitch();
        }

        FlushDns();
    }

    private void SafeDisableKillSwitch()
    {
        try
        {
            _killSwitch.Disable();
            if (_killSwitchJournalId is not null)
            {
                _journal.Complete(_killSwitchJournalId);
                _killSwitchJournalId = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Journal entry stays; recovery/repair will retry.
            _logger.LogError("Failed to remove kill-switch filters: {Error}", ex.Message);
        }
    }

    private void OnCoreExited(object? sender, CoreExitInfo info)
    {
        _ = Task.Run(() => HandleCoreExitAsync(info));
    }

    private async Task HandleCoreExitAsync(CoreExitInfo info)
    {
        // Captured before waiting: a Stop (or new Start) issued from now on cancels this whole restart sequence.
        var preempted = Volatile.Read(ref _preempt).Token;
        try
        {
            await _gate.WaitAsync(preempted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_runner is null || _plan is null || preempted.IsCancellationRequested)
            {
                return;
            }

            var killSwitch = _request?.KillSwitch.Enabled == true;
            var attempt = _status.RestartCount + 1;
            var willRestart = attempt <= MaxRestarts;
            _logger.LogError("{Core} crashed (code {Code}); kill switch {Ks}; restart {Attempt}/{Max}.", info.Core, info.ExitCode, killSwitch ? "holding traffic" : "off", attempt, MaxRestarts);
            CoreExited?.Invoke(this, new CoreExitedEvent(info.Core, info.ExitCode, false, info.LastOutput, willRestart));
            await StopCoreAsync(keepKillSwitch: killSwitch).ConfigureAwait(false);
            if (!willRestart)
            {
                SetStatus(_status with { State = TunnelState.Failed, LastError = $"{info.Core} keeps crashing: {info.LastOutput}", KillSwitchEngaged = killSwitch });
                return;
            }

            SetStatus(_status with { State = TunnelState.Restarting, RestartCount = attempt });
            try
            {
                await Task.Delay(TimeSpan.FromTicks(RestartBaseDelay.Ticks * (1L << (attempt - 1))), _time, preempted).ConfigureAwait(false);
                _adapterJournalId = _journal.Record(JournalEntryKind.TunAdapter, new TunAdapterJournalData(Domain.ProductInfo.TunInterfaceName), "tun-adapter");
                await LaunchAsync(preempted).ConfigureAwait(false);
                SetStatus(_status with { State = TunnelState.Running, LastError = null });
                _logger.LogWarning("Core restarted after crash (attempt {Attempt}).", attempt);
            }
            catch (OperationCanceledException) when (preempted.IsCancellationRequested)
            {
                // Stop/Start took over: leave the teardown to it (it runs right after we release the gate).
                _logger.LogInformation("Core restart abandoned: a stop or new start took over.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await StopCoreAsync(keepKillSwitch: killSwitch).ConfigureAwait(false);
                SetStatus(_status with { State = TunnelState.Failed, LastError = SecretRedactor.Redact(ex.Message), KillSwitchEngaged = killSwitch });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TrafficLoopAsync(ISessionRunner runner, CancellationToken ct)
    {
        _lastUp = 0;
        _lastDown = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false);
                if (await runner.GetTrafficAsync(ct).ConfigureAwait(false) is { } t)
                {
                    var up = Math.Max(0, t.UploadTotal - _lastUp);
                    var down = Math.Max(0, t.DownloadTotal - _lastDown);
                    _lastUp = t.UploadTotal;
                    _lastDown = t.DownloadTotal;
                    _status = _status with { UploadTotal = t.UploadTotal, DownloadTotal = t.DownloadTotal };
                    Traffic?.Invoke(this, new TrafficEvent(t.UploadTotal, t.DownloadTotal, up, down));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Domain.Usage.TrafficLimits _limits = Domain.Usage.TrafficLimits.None;

    public Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct) =>
        _runner is { } r ? r.GetConnectionsAsync(ct) : Task.FromResult<Domain.Usage.CoreConnections?>(null);

    /// <summary>Kept for core restarts; applied to the running relay at once.</summary>
    public void SetTrafficLimits(Domain.Usage.TrafficLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _runner?.SetTrafficLimits(limits);
    }

    public const int MaxTrafficEntries = 4000;

    public static void ValidateLimits(Domain.Usage.TrafficLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Classes.Count > 256 || limits.PriorityProcesses.Count > MaxTrafficEntries || limits.PriorityFolders.Count > 256)
        {
            throw new TunnelRequestException("Too many traffic limit entries.");
        }

        if (limits.Classes.Keys.Concat(limits.PriorityProcesses).Concat(limits.PriorityFolders).Any(v => v is null || v.Length > 300 || NetText.HasControlChars(v)))
        {
            throw new TunnelRequestException("Invalid traffic limit entry.");
        }
    }

    /// <summary>Lists in the traffic policy are bounded and free of control characters (they end up in the core config).</summary>
    public static void ValidateTraffic(Domain.Usage.TrafficPolicy? traffic)
    {
        if (traffic is null)
        {
            return;
        }

        var lists = new[]
        {
            traffic.BlockedApps, traffic.BlockedDomains, traffic.BlockedCidrs, traffic.InternalDomains, traffic.InternalCidrs, traffic.InternalDns,
            traffic.PriorityApps, traffic.LimitedApps, traffic.MediaApps, traffic.DownloadApps,
        };
        lists = [.. lists, .. traffic.DomainClasses.Select(d => d.Domains)];
        if (traffic.DomainClasses.Count > 64 || traffic.DomainClasses.Any(d => !Cores.Generation.SingBoxConfigBuilder.IsClassName(d.Class)))
        {
            throw new TunnelRequestException("Invalid traffic class.");
        }

        if (lists.Any(l => l.Count > MaxTrafficEntries) || lists.SelectMany(l => l).Any(v => v is null || v.Length > 300 || NetText.HasControlChars(v)))
        {
            throw new TunnelRequestException("Invalid traffic policy.");
        }

        if (traffic.InternalDns.Any(d => !NetText.IsIpLiteral(d)) || traffic.InternalCidrs.Concat(traffic.BlockedCidrs).Any(c => !NetText.IsCidr(c)))
        {
            throw new TunnelRequestException("Invalid workplace address in the traffic policy.");
        }

        switch (traffic.WorkRoute)
        {
            case Domain.Usage.WorkRoute.Off:
                break;
            case Domain.Usage.WorkRoute.Server:
                if (traffic.WorkServer is not { } work || !ProfileValidator.Validate(work).IsValid)
                {
                    throw new TunnelRequestException("Invalid work server in the traffic policy.");
                }

                break;
            case Domain.Usage.WorkRoute.Adapter:
                if (!Domain.Network.AdapterNames.IsValid(traffic.WorkInterface))
                {
                    throw new TunnelRequestException("Invalid work adapter in the traffic policy.");
                }

                break;
            default:
                throw new TunnelRequestException("Invalid work route in the traffic policy.");
        }
    }

    /// <summary>The privileged boundary: reject anything not well-formed before it reaches a core running as SYSTEM.</summary>
    public static void Validate(TunnelStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = ProfileValidator.Validate(request.Profile);
        if (!profile.IsValid)
        {
            throw new TunnelRequestException("Invalid profile: " + string.Join(" ", profile.Errors.Select(e => e.Message)));
        }

        if (request.Mtu is < 1280 or > 9000)
        {
            throw new TunnelRequestException("MTU must be between 1280 and 9000.");
        }

        foreach (var ep in new[] { request.Dns.Remote, request.Dns.Direct, request.Dns.Bootstrap })
        {
            if (ep.Transport != Domain.Settings.DnsTransport.Local && !NetText.IsValidServerAddress(ep.Server))
            {
                throw new TunnelRequestException("Invalid DNS server address.");
            }

            if (ep.Path is { } p && (!p.StartsWith('/') || NetText.HasControlChars(p) || p.Length > 256))
            {
                throw new TunnelRequestException("Invalid DoH path.");
            }
        }

        var tags = request.RuleSets.Select(r => r.Tag).ToArray();
        var rules = RuleValidator.ValidateAll(request.Routing.Rules, tags.Concat(request.Routing.RuleSets.Select(r => r.Tag)).ToArray());
        if (!rules.IsValid)
        {
            throw new TunnelRequestException("Invalid routing rules: " + string.Join(" ", rules.Errors.Select(e => e.Message)));
        }

        if (request.RuleSets.Count > 64)
        {
            throw new TunnelRequestException("Too many rule-sets.");
        }

        ValidateTraffic(request.Traffic);
        ValidateLimits(request.Limits);
        if (request.SystemProxyPort is < 0 or > 65535 || request.SystemProxyPort is > 0 and < 1024)
        {
            throw new TunnelRequestException("Invalid proxy port.");
        }
    }

    /// <summary>Stores rule-set payloads under the service-owned directory after hash + format checks.</summary>
    public IReadOnlyDictionary<string, string> PersistRuleSets(IReadOnlyList<RuleSetPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        Directory.CreateDirectory(_ruleSetDir);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rs in payloads)
        {
            if (rs.ContentBase64.Length > IpcProtocol.MaxRuleSetBytes * 4 / 3 + 16)
            {
                throw new TunnelRequestException($"Rule-set {rs.Tag} is too large.");
            }

            var bytes = Convert.FromBase64String(rs.ContentBase64);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!FileIntegrity.DigestEquals(sha, rs.Sha256))
            {
                throw new TunnelRequestException($"Rule-set {rs.Tag} hash mismatch.");
            }

            var (valid, _, error) = RuleSetManager.ValidateContent(bytes, rs.Format);
            if (!valid)
            {
                throw new TunnelRequestException($"Rule-set {rs.Tag} is invalid: {error}");
            }

            var path = SafePath.CombineUnderRoot(_ruleSetDir, $"{RuleSetManager.SafeTag(rs.Tag)}-{sha[..16]}{(rs.Format == RuleSetFormat.Binary ? ".srs" : ".json")}");
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, bytes);
            }

            map[rs.Tag] = path;
        }

        return map;
    }

    private void SetStatus(ServiceStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(keepKillSwitch: false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
