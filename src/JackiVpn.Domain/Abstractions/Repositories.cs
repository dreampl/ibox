using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.Domain.Abstractions;

public interface IProfileRepository
{
    Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken ct);

    Task<ProxyProfile?> GetAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<ProxyProfile>> GetBySubscriptionAsync(string subscriptionId, CancellationToken ct);

    Task UpsertAsync(ProxyProfile profile, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);
}

public interface ISubscriptionRepository
{
    Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken ct);

    Task<Subscription?> GetAsync(string id, CancellationToken ct);

    /// <summary>Creates a subscription; the source URL is stored encrypted.</summary>
    Task AddAsync(Subscription subscription, string sourceUrl, CancellationToken ct);

    Task UpdateAsync(Subscription subscription, CancellationToken ct);

    /// <summary>Deletes the subscription and every profile it owns.</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>Decrypts and returns the real subscription URL. Callers must never log it.</summary>
    Task<string?> GetSourceUrlAsync(string id, CancellationToken ct);

    Task SetSourceUrlAsync(string id, string sourceUrl, CancellationToken ct);

    /// <summary>
    /// Atomically applies a sync plan and the updated subscription record. Either everything is committed or
    /// nothing is — a failed update never leaves a half-written node list.
    /// </summary>
    Task ApplySyncAsync(Subscription updated, SubscriptionSyncPlan plan, CancellationToken ct);
}

public interface IHealthHistoryRepository
{
    Task AppendAsync(string profileId, HealthSample sample, CancellationToken ct);

    Task AppendManyAsync(IReadOnlyList<(string ProfileId, HealthSample Sample)> samples, CancellationToken ct);

    Task<IReadOnlyList<HealthSample>> GetRecentAsync(string profileId, int maxCount, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<HealthSample>>> GetRecentForAllAsync(int maxPerProfile, CancellationToken ct);

    Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken ct);

    Task SaveMuxBenchmarkAsync(MuxBenchmarkResult result, CancellationToken ct);

    Task<MuxBenchmarkResult?> GetMuxBenchmarkAsync(string profileFingerprint, CancellationToken ct);
}

public interface IConnectionHistoryRepository
{
    Task AppendAsync(ConnectionHistoryEntry entry, CancellationToken ct);

    Task UpdateAsync(ConnectionHistoryEntry entry, CancellationToken ct);

    Task<IReadOnlyList<ConnectionHistoryEntry>> GetRecentAsync(int count, CancellationToken ct);

    Task<int> CountOutcomesSinceAsync(string profileId, ConnectionOutcome outcome, DateTimeOffset sinceUtc, CancellationToken ct);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken ct);

    Task SaveAsync(AppSettings settings, CancellationToken ct);
}
