using JackiVpn.Application.Notifications;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Ownership;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Providers;
using JackiVpn.Subscriptions.Sync;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Subscriptions;

public sealed record SubscriptionUpdateReport(
    string SubscriptionId,
    FetchOutcome Outcome,
    int Added,
    int Changed,
    int Removed,
    int Duplicates,
    int ParseErrors,
    string? Error)
{
    public bool Succeeded => Outcome != FetchOutcome.Failed;
}

public sealed class SubscriptionException(string message) : Exception(message);

/// <summary>
/// Adds, refreshes and auto-updates subscriptions. Refresh is transactional: nothing is written unless the
/// new payload parsed into at least one valid profile; then the whole merge is applied in one database
/// transaction. Any failure (HTTP error, timeout, HTML, broken body) leaves stored profiles untouched.
/// </summary>
public sealed class SubscriptionService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IProfileRepository _profiles;
    private readonly Func<IHttpFetcher> _fetcherFactory;
    private readonly IConfigParser _parser;
    private readonly Func<AppSettings> _settings;
    private readonly NotificationService? _notifications;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<DeviceIdentity?>? _device;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="device">This device's identity for device-limited providers (null: never identify).</param>
    public SubscriptionService(
        ISubscriptionRepository subscriptions,
        IProfileRepository profiles,
        Func<IHttpFetcher> fetcherFactory,
        IConfigParser parser,
        Func<AppSettings> settings,
        ILogger logger,
        NotificationService? notifications = null,
        TimeProvider? time = null,
        Func<DeviceIdentity?>? device = null)
    {
        _subscriptions = subscriptions;
        _profiles = profiles;
        _fetcherFactory = fetcherFactory;
        _parser = parser;
        _settings = settings;
        _logger = logger;
        _notifications = notifications;
        _time = time ?? TimeProvider.System;
        _device = device;
    }

    /// <summary>Whether a subscription identifies this device: its own choice, else official providers only.</summary>
    public static bool SendsDeviceId(Subscription subscription, string url)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return subscription.SendDeviceId ?? OfficialProviders.IsOfficialUrl(url);
    }

    private IReadOnlyDictionary<string, string>? DeviceHeaders(bool send) => send && _device?.Invoke() is { } d ? d.Headers() : null;

    public event EventHandler<SubscriptionUpdateReport>? Updated;

    public async Task<Subscription> AddAsync(string url, string? name, SubscriptionProviderKind? provider, CancellationToken ct)
    {
        var check = UrlSafetyPolicy.ValidateInitial(url, out var uri);
        if (!check.Allowed)
        {
            throw new SubscriptionException(check.Reason!);
        }

        var settings = _settings();
        var kind = provider;
        if (kind is null)
        {
            var fetcher = _fetcherFactory();
            kind = await new SubscriptionProviderDetector(fetcher).DetectAsync(uri!.ToString(), settings.Subscriptions, ct, DeviceHeaders(OfficialProviders.IsOfficialUrl(uri))).ConfigureAwait(false);
        }

        var subscription = new Subscription
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? uri!.Host : name.Trim(),
            Provider = kind.Value,
            MaskedUrl = SecretRedactor.MaskUrl(uri!.ToString()),
            Host = uri.IdnHost,
            AutoUpdate = settings.Subscriptions.DefaultAutoUpdate,
            CreatedUtc = _time.GetUtcNow(),
        };
        await _subscriptions.AddAsync(subscription, uri.ToString(), ct).ConfigureAwait(false);
        _logger.LogInformation("Subscription added: {Masked} ({Provider}).", subscription.MaskedUrl, subscription.Provider);
        await RefreshAsync(subscription.Id, ct).ConfigureAwait(false);
        return await _subscriptions.GetAsync(subscription.Id, ct).ConfigureAwait(false) ?? subscription;
    }

    public async Task<SubscriptionUpdateReport> RefreshAsync(string subscriptionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(subscriptionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SubscriptionUpdateReport> RefreshCoreAsync(string subscriptionId, CancellationToken ct)
    {
        var sub = await _subscriptions.GetAsync(subscriptionId, ct).ConfigureAwait(false)
            ?? throw new SubscriptionException("Subscription not found.");
        var url = await _subscriptions.GetSourceUrlAsync(subscriptionId, ct).ConfigureAwait(false)
            ?? throw new SubscriptionException("Subscription URL is missing.");
        var settings = _settings();
        var now = _time.GetUtcNow();
        var fetcher = _fetcherFactory();
        ISubscriptionProvider provider = sub.Provider == SubscriptionProviderKind.PasarGuard
            ? new PasarGuardSubscriptionProvider(fetcher, _parser, _time)
            : new GenericSubscriptionProvider(fetcher, _parser, _time);

        SecretRedactor.Register(url);
        var deviceHeaders = DeviceHeaders(SendsDeviceId(sub, url));

        // Leftovers from earlier versions (unmarked nodes flagged as removed, i.e. duplicates of rotated nodes) are
        // cleaned up by a full re-sync: ignore the cache validators once so the payload is parsed and merged again.
        var existingBefore = await _profiles.GetBySubscriptionAsync(sub.Id, ct).ConfigureAwait(false);
        var needsCleanup = existingBefore.Any(e => e.SyncState == ProfileSyncState.RemovedByProvider && !SubscriptionMerger.HasUserData(e));
        SubscriptionFetchRequest Request(string from) => new()
        {
            Url = from,
            ETag = needsCleanup ? null : sub.ETag,
            LastModified = needsCleanup ? null : sub.LastModified,
            PreviousContentHash = needsCleanup ? null : sub.ContentHash,
            PreferredEndpoint = from == url ? sub.ResolvedEndpoint : null,
            Settings = settings.Subscriptions,
            Ownership = settings.Ownership,
            ParseContext = new ParseContext { OriginKind = ProfileOriginKind.Subscription, SubscriptionId = sub.Id, NowUtc = now },
            DeviceHeaders = deviceHeaders,
        };

        // Official panels: the Iran entry first (it sees the real carrier and adds relay configs where direct paths
        // are throttled), the saved URL (Cloudflare) if the entry cannot be reached.
        var entry = OfficialProviders.IranEntryUrl(url);
        SubscriptionFetchResult result;
        try
        {
            result = entry is null ? await provider.FetchAsync(Request(url), ct).ConfigureAwait(false) : await FetchViaEntryAsync().ConfigureAwait(false);
        }
        finally
        {
            SecretRedactor.Unregister(url);
        }

        async Task<SubscriptionFetchResult> FetchViaEntryAsync()
        {
            SecretRedactor.Register(entry);
            try
            {
                using var entryTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                entryTimeout.CancelAfter(EntryTimeout);
                var viaEntry = await provider.FetchAsync(Request(entry), entryTimeout.Token).ConfigureAwait(false);
                if (viaEntry.Outcome != FetchOutcome.Failed)
                {
                    return viaEntry;
                }

                _logger.LogInformation("Iran entry unavailable for {Masked} ({Kind}); using the saved address.", sub.MaskedUrl, viaEntry.Failure);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Iran entry timed out for {Masked}; using the saved address.", sub.MaskedUrl);
            }
            finally
            {
                SecretRedactor.Unregister(entry);
            }

            return await provider.FetchAsync(Request(url), ct).ConfigureAwait(false);
        }

        var metadata = result.Metadata.RetrievedUtc is null ? sub.Metadata : JackiVpn.Subscriptions.Metadata.SubscriptionMetadataParser.Merge(sub.Metadata, result.Metadata);
        var ownership = result.Outcome == FetchOutcome.Failed
            ? sub.Ownership
            : ProfileOwnershipVerifier.VerifySubscription(sub.Host, result.WasHttps, result.ProviderHeaders, settings.Ownership, now);

        if (result.Outcome == FetchOutcome.Failed)
        {
            var failed = sub with
            {
                LastAttemptUtc = now,
                LastError = SecretRedactor.Redact(result.Error ?? "Update failed."),
                Metadata = metadata,
                DeviceLimit = result.DeviceLimit ?? sub.DeviceLimit,
            };
            await _subscriptions.UpdateAsync(failed, ct).ConfigureAwait(false);
            _logger.LogWarning("Subscription {Masked} update failed ({Kind}): {Error}. Existing configs kept.", sub.MaskedUrl, result.Failure, failed.LastError);
            var failReport = new SubscriptionUpdateReport(sub.Id, FetchOutcome.Failed, 0, 0, 0, 0, result.Parsed?.Failed ?? 0, failed.LastError);
            Updated?.Invoke(this, failReport);
            NotifyQuota(failed);
            return failReport;
        }

        var existing = existingBefore;
        SubscriptionSyncPlan plan;
        var duplicates = 0;
        if (result.Outcome == FetchOutcome.NotModified || result.Parsed is null)
        {
            plan = new SubscriptionSyncPlan([], [], [], []);
        }
        else
        {
            var report = SubscriptionMerger.Plan(existing, result.Parsed.Profiles, now);
            plan = report.Plan;
            duplicates = report.DuplicatesDropped;
        }

        var updated = sub with
        {
            Metadata = metadata,
            Name = sub.Name == sub.Host && metadata.Title is { Length: > 0 } title ? title : sub.Name,
            LastAttemptUtc = now,
            LastUpdateUtc = now,
            LastError = null,
            ETag = result.ETag ?? sub.ETag,
            LastModified = result.LastModified ?? sub.LastModified,
            ContentHash = result.ContentHash ?? sub.ContentHash,
            ResolvedEndpoint = result.ResolvedEndpoint ?? sub.ResolvedEndpoint,
            LastFormat = result.Parsed?.Format ?? sub.LastFormat,
            ProfileCount = result.Outcome == FetchOutcome.NotModified || result.Parsed is null
                ? existing.Count(p => p.SyncState != ProfileSyncState.RemovedByProvider)
                : plan.Added.Count + plan.Changed.Count + plan.Unchanged.Count,
            Ownership = ownership,

            // Accepted now: an earlier refusal is resolved; "Registered" only while the provider still confirms it.
            DeviceLimit = result.DeviceLimit ?? (deviceHeaders is not null && sub.DeviceLimit == DeviceLimitState.Registered ? DeviceLimitState.Registered : DeviceLimitState.Unknown),
        };

        await _subscriptions.ApplySyncAsync(updated, plan, ct).ConfigureAwait(false);
        var final = new SubscriptionUpdateReport(sub.Id, result.Outcome, plan.Added.Count, plan.Changed.Count, plan.RemovedIds.Count + plan.Deleted.Count, duplicates, result.Parsed?.Failed ?? 0, null);
        _logger.LogInformation("Subscription {Masked}: {Outcome} via {Endpoint} (+{Added} ~{Changed} -{Removed}, {Dup} duplicate(s), owned: {Owned}).",
            sub.MaskedUrl, result.Outcome, updated.ResolvedEndpoint, final.Added, final.Changed, final.Removed, duplicates, ownership.IsOwned);
        Updated?.Invoke(this, final);
        if (result.Outcome == FetchOutcome.Updated && plan.HasChanges)
        {
            _notifications?.Notify(NotificationCategory.SubscriptionUpdated, updated.Name, $"{final.Added} new, {final.Changed} changed, {final.Removed} removed.");
        }

        NotifyQuota(updated);
        return final;
    }

    private void NotifyQuota(Subscription s)
    {
        if (_notifications is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        switch (s.Metadata.ComputeStatus(now))
        {
            case SubscriptionStatus.Expired:
                _notifications.Notify(NotificationCategory.SubscriptionExpired, s.Name, "This subscription has expired.");
                return;
            case SubscriptionStatus.Limited:
                _notifications.Notify(NotificationCategory.TrafficAlmostFinished, s.Name, "The traffic quota is used up.");
                return;
        }

        if (s.Metadata.UsedPercent is { } pct && pct >= _settings().Notifications.TrafficWarningPercent)
        {
            _notifications.Notify(NotificationCategory.TrafficAlmostFinished, s.Name, $"{pct:0}% of the traffic quota is used.");
        }
    }

    /// <summary>Subscriptions whose auto-update interval has elapsed.</summary>
    public async Task<IReadOnlyList<Subscription>> DueForUpdateAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var all = await _subscriptions.GetAllAsync(ct).ConfigureAwait(false);
        return all.Where(s => s.Enabled && s.EffectiveUpdateInterval is { } iv
                && (s.LastAttemptUtc is null || now - s.LastAttemptUtc >= iv))
            .ToArray();
    }

    /// <summary>Background loop: refreshes due subscriptions (failures back off to the next interval).</summary>
    public async Task RunAutoUpdateLoopAsync(TimeSpan tick, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var s in await DueForUpdateAsync(ct).ConfigureAwait(false))
                {
                    await RefreshAsync(s.Id, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning("Auto-update cycle failed: {Error}", SecretRedactor.Redact(ex.Message));
            }

            await Task.Delay(tick, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>null = default (official providers only).</summary>
    public async Task SetSendDeviceIdAsync(string id, bool? send, CancellationToken ct)
    {
        var s = await _subscriptions.GetAsync(id, ct).ConfigureAwait(false) ?? throw new SubscriptionException("Subscription not found.");
        await _subscriptions.UpdateAsync(s with { SendDeviceId = send }, ct).ConfigureAwait(false);
    }

    /// <summary>Whether this subscription currently identifies the device (for display).</summary>
    public async Task<bool> SendsDeviceIdAsync(string id, CancellationToken ct)
    {
        var s = await _subscriptions.GetAsync(id, ct).ConfigureAwait(false);
        var url = await _subscriptions.GetSourceUrlAsync(id, ct).ConfigureAwait(false);
        return s is not null && url is not null && SendsDeviceId(s, url);
    }

    public async Task SetAutoUpdateAsync(string id, AutoUpdateInterval interval, CancellationToken ct)
    {
        var s = await _subscriptions.GetAsync(id, ct).ConfigureAwait(false) ?? throw new SubscriptionException("Subscription not found.");
        await _subscriptions.UpdateAsync(s with { AutoUpdate = interval }, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string id, string name, CancellationToken ct)
    {
        var s = await _subscriptions.GetAsync(id, ct).ConfigureAwait(false) ?? throw new SubscriptionException("Subscription not found.");
        var clean = ProfileFactory.NormalizeName(name);
        if (clean.Length == 0)
        {
            throw new SubscriptionException("Name must not be empty.");
        }

        await _subscriptions.UpdateAsync(s with { Name = clean }, ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(string id, CancellationToken ct) => _subscriptions.DeleteAsync(id, ct);

    public static TimeSpan AccountCheckInterval { get; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the Iran entry may take before the saved (Cloudflare) address is used instead.</summary>
    public static TimeSpan EntryTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Network changes closer together than this refresh only once.</summary>
    public static TimeSpan NetworkRefreshMinInterval { get; } = TimeSpan.FromMinutes(10);

    private DateTimeOffset _lastNetworkRefresh = DateTimeOffset.MinValue;

    /// <summary>
    /// After a move to another network (Wi-Fi ↔ mobile data, another SIM): providers that tailor the config set to
    /// the user's network (relay configs only where needed) are asked again, at most every few minutes.
    /// </summary>
    public async Task RefreshAfterNetworkChangeAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (now - _lastNetworkRefresh < NetworkRefreshMinInterval)
        {
            return;
        }

        _lastNetworkRefresh = now;
        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); // let the new network settle
        foreach (var sub in await _subscriptions.GetAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await RefreshAsync(sub.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SubscriptionException or HttpRequestException or IOException)
            {
                _logger.LogInformation("Refresh after network change failed for {Masked}: {Error}", sub.MaskedUrl, SecretRedactor.Redact(ex.Message));
            }
        }
    }

    /// <summary>Raised after an account check (device limit / online status) changed a subscription.</summary>
    public event EventHandler<string>? AccountChecked;

    /// <summary>
    /// Light account check (PasarGuard <c>/info</c> only, no server list): device limit, last time the account was
    /// online, status, usage and expiry. Also re-reads the device-limit answer for this device.
    /// </summary>
    public async Task<Subscription?> CheckAccountAsync(string id, CancellationToken ct)
    {
        var sub = await _subscriptions.GetAsync(id, ct).ConfigureAwait(false);
        var url = await _subscriptions.GetSourceUrlAsync(id, ct).ConfigureAwait(false);
        if (sub is null || url is null || sub.Provider != SubscriptionProviderKind.PasarGuard)
        {
            return sub;
        }

        SecretRedactor.Register(url);
        try
        {
            var headers = DeviceHeaders(SendsDeviceId(sub, url));
            var provider = new PasarGuardSubscriptionProvider(_fetcherFactory(), _parser, _time);
            var (account, device) = await provider.FetchAccountAsync(url, _settings().Subscriptions, headers, ct).ConfigureAwait(false);
            if (account is null && device is null)
            {
                return sub;
            }

            var updated = sub with
            {
                Metadata = JackiVpn.Subscriptions.Metadata.SubscriptionMetadataParser.Merge(sub.Metadata, account),
                DeviceLimit = device ?? sub.DeviceLimit,
            };
            await _subscriptions.UpdateAsync(updated, ct).ConfigureAwait(false);
            AccountChecked?.Invoke(this, id);
            NotifyQuota(updated);
            return updated;
        }
        catch (JackiVpn.Subscriptions.Fetching.FetchException ex) when (ex.Kind is not JackiVpn.Subscriptions.Fetching.FetchFailureKind.Cancelled)
        {
            _logger.LogDebug("Account check for {Masked} failed: {Kind}", sub.MaskedUrl, ex.Kind);
            return sub;
        }
        finally
        {
            SecretRedactor.Unregister(url);
        }
    }
    /// <summary>Every 10 minutes: account checks for all enabled PasarGuard subscriptions.</summary>
    public async Task RunAccountMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var s in (await _subscriptions.GetAllAsync(ct).ConfigureAwait(false)).Where(s => s.Enabled && s.Provider == SubscriptionProviderKind.PasarGuard))
                {
                    await CheckAccountAsync(s.Id, ct).ConfigureAwait(false);
                }

                await Task.Delay(AccountCheckInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Account monitor cycle failed: {Error}", SecretRedactor.Redact(ex.Message));
                await Task.Delay(AccountCheckInterval, _time, ct).ConfigureAwait(false);
            }
        }
    }
}
