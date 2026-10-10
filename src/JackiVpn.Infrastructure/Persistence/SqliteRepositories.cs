using System.Globalization;
using System.Text.Json;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;
using Microsoft.Data.Sqlite;

namespace JackiVpn.Infrastructure.Persistence;

public sealed class SqliteProfileRepository(JackiDatabase db, ISecretProtector protector) : IProfileRepository
{
    public async Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_protected FROM profiles ORDER BY sort_order, rowid";
        return await ReadProfilesAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<ProxyProfile?> GetAsync(string id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_protected FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (await ReadProfilesAsync(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ProxyProfile>> GetBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_protected FROM profiles WHERE subscription_id = $s ORDER BY sort_order, rowid";
        cmd.Parameters.AddWithValue("$s", subscriptionId);
        return await ReadProfilesAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(ProxyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Upsert(c, tx, profile, protector, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task Upsert(SqliteConnection c, SqliteTransaction tx, ProxyProfile p, ISecretProtector protector, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO profiles(id, subscription_id, name, protocol, server, port, fingerprint, sync_state, is_favorite, payload_protected, updated_utc)
            VALUES ($id, $sub, $name, $proto, $server, $port, $fp, $state, $fav, $payload, $now)
            ON CONFLICT(id) DO UPDATE SET subscription_id = excluded.subscription_id, name = excluded.name, protocol = excluded.protocol,
              server = excluded.server, port = excluded.port, fingerprint = excluded.fingerprint, sync_state = excluded.sync_state,
              is_favorite = excluded.is_favorite, payload_protected = excluded.payload_protected, updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$sub", (object?)p.Origin.SubscriptionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", p.DisplayName);
        cmd.Parameters.AddWithValue("$proto", p.Protocol.ToString());
        cmd.Parameters.AddWithValue("$server", p.Server);
        cmd.Parameters.AddWithValue("$port", p.Port);
        cmd.Parameters.AddWithValue("$fp", p.Fingerprint);
        cmd.Parameters.AddWithValue("$state", p.SyncState.ToString());
        cmd.Parameters.AddWithValue("$fav", p.User.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$payload", protector.Protect(JsonSerializer.Serialize(p, JsonDefaults.Storage), SecretPurposes.ProfilePayload));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ProxyProfile>> ReadProfilesAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<ProxyProfile>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var blob = (byte[])r[0];
            var json = protector.Unprotect(blob, SecretPurposes.ProfilePayload);
            if (JsonSerializer.Deserialize<ProxyProfile>(json, JsonDefaults.Storage) is { } p)
            {
                list.Add(p);
            }
        }

        return list;
    }
}

public sealed class SqliteSubscriptionRepository(JackiDatabase db, ISecretProtector protector) : ISubscriptionRepository
{
    public async Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM subscriptions ORDER BY created_utc";
        var list = new List<Subscription>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize<Subscription>(r.GetString(0), JsonDefaults.Storage) is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    public async Task<Subscription?> GetAsync(string id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM subscriptions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<Subscription>(json, JsonDefaults.Storage);
    }

    public async Task AddAsync(Subscription subscription, string sourceUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO subscriptions(id, json, url_protected, created_utc) VALUES ($id, $json, $url, $now)";
        cmd.Parameters.AddWithValue("$id", subscription.Id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(subscription, JsonDefaults.Storage));
        cmd.Parameters.AddWithValue("$url", protector.Protect(sourceUrl, SecretPurposes.SubscriptionUrl));
        cmd.Parameters.AddWithValue("$now", subscription.CreatedUtc.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Subscription subscription, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET json = $json WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", subscription.Id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(subscription, JsonDefaults.Storage));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM profiles WHERE subscription_id = $id; DELETE FROM subscriptions WHERE id = $id;";
            del.Parameters.AddWithValue("$id", id);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetSourceUrlAsync(string id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT url_protected FROM subscriptions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is byte[] blob
            ? protector.Unprotect(blob, SecretPurposes.SubscriptionUrl)
            : null;
    }

    public async Task SetSourceUrlAsync(string id, string sourceUrl, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET url_protected = $url WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$url", protector.Protect(sourceUrl, SecretPurposes.SubscriptionUrl));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>All-or-nothing: profiles added/changed, removed ones flagged, subscription record updated.</summary>
    public async Task ApplySyncAsync(Subscription updated, SubscriptionSyncPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(plan);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var p in plan.Added.Concat(plan.Changed).Concat(plan.Unchanged))
            {
                await SqliteProfileRepository.Upsert(c, tx, p, protector, ct).ConfigureAwait(false);
            }

            if (plan.RemovedIds.Count > 0)
            {
                await using var read = c.CreateCommand();
                read.Transaction = tx;
                foreach (var id in plan.RemovedIds)
                {
                    read.CommandText = "SELECT payload_protected FROM profiles WHERE id = $id";
                    read.Parameters.Clear();
                    read.Parameters.AddWithValue("$id", id);
                    if (await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] blob)
                    {
                        continue;
                    }

                    var p = JsonSerializer.Deserialize<ProxyProfile>(protector.Unprotect(blob, SecretPurposes.ProfilePayload), JsonDefaults.Storage);
                    if (p is null || p.SyncState == ProfileSyncState.RemovedByProvider)
                    {
                        continue;
                    }

                    await SqliteProfileRepository.Upsert(c, tx, p with { SyncState = ProfileSyncState.RemovedByProvider, SyncStateSinceUtc = DateTimeOffset.UtcNow }, protector, ct).ConfigureAwait(false);
                }
            }

            if (plan.Deleted.Count > 0)
            {
                await using var del = c.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM profiles WHERE id = $id AND subscription_id = $sub";
                foreach (var id in plan.Deleted)
                {
                    del.Parameters.Clear();
                    del.Parameters.AddWithValue("$id", id);
                    del.Parameters.AddWithValue("$sub", updated.Id);
                    await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await using var sub = c.CreateCommand();
            sub.Transaction = tx;
            sub.CommandText = "UPDATE subscriptions SET json = $json WHERE id = $id";
            sub.Parameters.AddWithValue("$id", updated.Id);
            sub.Parameters.AddWithValue("$json", JsonSerializer.Serialize(updated, JsonDefaults.Storage));
            if (await sub.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Subscription {updated.Id} does not exist.");
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class SqliteHealthHistoryRepository(JackiDatabase db) : IHealthHistoryRepository
{
    public Task AppendAsync(string profileId, HealthSample sample, CancellationToken ct) => AppendManyAsync([(profileId, sample)], ct);

    public async Task AppendManyAsync(IReadOnlyList<(string ProfileId, HealthSample Sample)> samples, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return;
        }

        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO health_samples(profile_id, ts, json) VALUES ($p, $ts, $json)";
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var json = cmd.Parameters.Add("$json", SqliteType.Text);
        foreach (var (profileId, sample) in samples)
        {
            p.Value = profileId;
            ts.Value = sample.TimestampUtc.ToUnixTimeMilliseconds();
            json.Value = JsonSerializer.Serialize(sample, JsonDefaults.Storage);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HealthSample>> GetRecentAsync(string profileId, int maxCount, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM health_samples WHERE profile_id = $p ORDER BY ts DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$n", maxCount);
        var list = new List<HealthSample>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize<HealthSample>(r.GetString(0), JsonDefaults.Storage) is { } s)
            {
                list.Add(s);
            }
        }

        list.Reverse();
        return list;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<HealthSample>>> GetRecentForAllAsync(int maxPerProfile, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT profile_id, json FROM (
              SELECT profile_id, json, ts, ROW_NUMBER() OVER (PARTITION BY profile_id ORDER BY ts DESC) AS rn FROM health_samples
            ) WHERE rn <= $n ORDER BY profile_id, ts
            """;
        cmd.Parameters.AddWithValue("$n", maxPerProfile);
        var map = new Dictionary<string, List<HealthSample>>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = r.GetString(0);
            if (JsonSerializer.Deserialize<HealthSample>(r.GetString(1), JsonDefaults.Storage) is { } s)
            {
                if (!map.TryGetValue(id, out var list))
                {
                    map[id] = list = [];
                }

                list.Add(s);
            }
        }

        return map.ToDictionary(k => k.Key, v => (IReadOnlyList<HealthSample>)v.Value, StringComparer.Ordinal);
    }

    public async Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM health_samples WHERE ts < $ts";
        cmd.Parameters.AddWithValue("$ts", olderThanUtc.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveMuxBenchmarkAsync(MuxBenchmarkResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO mux_benchmarks(fingerprint, json) VALUES ($f, $j) ON CONFLICT(fingerprint) DO UPDATE SET json = excluded.json";
        cmd.Parameters.AddWithValue("$f", result.ProfileFingerprint);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(result, JsonDefaults.Storage));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<MuxBenchmarkResult?> GetMuxBenchmarkAsync(string profileFingerprint, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM mux_benchmarks WHERE fingerprint = $f";
        cmd.Parameters.AddWithValue("$f", profileFingerprint);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<MuxBenchmarkResult>(json, JsonDefaults.Storage)
            : null;
    }
}

public sealed class SqliteConnectionHistoryRepository(JackiDatabase db) : IConnectionHistoryRepository
{
    public Task AppendAsync(ConnectionHistoryEntry entry, CancellationToken ct) => UpsertAsync(entry, ct);

    public Task UpdateAsync(ConnectionHistoryEntry entry, CancellationToken ct) => UpsertAsync(entry, ct);

    private async Task UpsertAsync(ConnectionHistoryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connection_history(id, profile_id, started_utc, outcome, json) VALUES ($id, $p, $s, $o, $j)
            ON CONFLICT(id) DO UPDATE SET outcome = excluded.outcome, json = excluded.json
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$p", entry.ProfileId);
        cmd.Parameters.AddWithValue("$s", entry.StartedUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$o", entry.Outcome.ToString());
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(entry with { Error = entry.Error is null ? null : SecretRedactor.Redact(entry.Error) }, JsonDefaults.Storage));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionHistoryEntry>> GetRecentAsync(int count, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM connection_history ORDER BY started_utc DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        var list = new List<ConnectionHistoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize<ConnectionHistoryEntry>(r.GetString(0), JsonDefaults.Storage) is { } e)
            {
                list.Add(e);
            }
        }

        return list;
    }

    public async Task<int> CountOutcomesSinceAsync(string profileId, ConnectionOutcome outcome, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM connection_history WHERE profile_id = $p AND outcome = $o AND started_utc >= $s";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$o", outcome.ToString());
        cmd.Parameters.AddWithValue("$s", sinceUtc.ToUnixTimeMilliseconds());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}

public sealed class SqliteSettingsRepository(JackiDatabase db) : ISettingsRepository
{
    private const string Key = "app";

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", Key);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json)
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Storage) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt settings never brick the app: secure defaults apply.
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, json) VALUES ($k, $j) ON CONFLICT(key) DO UPDATE SET json = excluded.json";
        cmd.Parameters.AddWithValue("$k", Key);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(settings, JsonDefaults.Storage));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
