using System.Security.Cryptography;
using System.Text;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Agent;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.News;

/// <summary>
/// The owner's news for this device's official subscriptions: asked every few minutes with the last ETag (a
/// "nothing new" answer is a few bytes), kept in memory only, handed to IBAR through eco.status without the
/// subscription. "Mark as read" goes back to the provider, so the message is gone on every device of that user.
/// </summary>
public sealed class NewsService(ISubscriptionRepository subscriptions, BroadcastClient client, ILogger logger)
{
    public static TimeSpan Interval { get; } = TimeSpan.FromMinutes(5);

    /// <summary>A subscription the panel does not know is asked again only after this long.</summary>
    public static TimeSpan UnknownBackoff { get; } = TimeSpan.FromHours(6);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private IReadOnlyList<AgentNews> _unread = [];

    private sealed class Source
    {
        public required Uri Endpoint { get; init; }
        public required string Token { get; init; }
        public required string Prefix { get; init; }
        public string? ETag { get; set; }
        public IReadOnlyList<BroadcastItem> Items { get; set; } = [];
        public DateTimeOffset QuietUntil { get; set; }
    }

    /// <summary>Unread news changed (raised on a background thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Unread messages, newest first.</summary>
    public IReadOnlyList<AgentNews> Unread => Volatile.Read(ref _unread);

    /// <summary>Asks now, then every <see cref="Interval"/> until <paramref name="ct"/> ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // closing
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sub in await subscriptions.GetAllAsync(ct).ConfigureAwait(false))
            {
                if (!sub.Enabled || BroadcastClient.Target(await subscriptions.GetSourceUrlAsync(sub.Id, ct).ConfigureAwait(false)) is not { } target)
                {
                    continue;
                }

                var key = target.Endpoint.Host + "|" + target.Token;
                if (!seen.Add(key))
                {
                    continue;
                }

                Source source;
                lock (_gate)
                {
                    if (!_sources.TryGetValue(key, out source!))
                    {
                        source = new Source { Endpoint = target.Endpoint, Token = target.Token, Prefix = Prefix(key) };
                        _sources[key] = source;
                    }
                }

                if (source.QuietUntil > DateTimeOffset.UtcNow)
                {
                    continue;
                }

                try
                {
                    var list = await client.ListAsync(source.Endpoint, source.Token, source.ETag, ct).ConfigureAwait(false);
                    if (list.UnknownSubscription)
                    {
                        source.QuietUntil = DateTimeOffset.UtcNow + UnknownBackoff;
                        source.Items = [];
                        source.ETag = null;
                    }
                    else if (!list.NotModified)
                    {
                        source.Items = list.Items;
                        source.ETag = list.ETag;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
                {
                    // Offline or the panel is busy: keep what we had, ask again next time.
                    logger.LogDebug("News: {Error}", ex.GetType().Name);
                }
            }

            lock (_gate)
            {
                foreach (var gone in _sources.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _sources.Remove(gone);
                }
            }

            Publish();
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>news.read from IBAR: marks the ids read at their provider, and hides them here at once.</summary>
    public async Task ReadAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        List<(Source Source, List<string> Ids)> work;
        lock (_gate)
        {
            work = _sources.Values
                .Select(s => (s, ids.Where(i => i.StartsWith(s.Prefix + ".", StringComparison.Ordinal)).Select(i => i[(s.Prefix.Length + 1)..]).ToList()))
                .Where(w => w.Item2.Count > 0)
                .ToList();
            foreach (var (s, local) in work)
            {
                s.Items = s.Items.Select(i => local.Contains(i.Id) ? i with { Read = true } : i).ToList();
            }
        }

        Publish();
        foreach (var (s, local) in work)
        {
            try
            {
                await client.ReadAsync(s.Endpoint, s.Token, local, ct).ConfigureAwait(false);
                s.ETag = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // Hidden here already; the provider hears it on the next "mark as read" or shows it again next poll.
                logger.LogDebug("News read: {Error}", ex.GetType().Name);
            }
        }
    }

    /// <summary>A short id for a subscription that reveals nothing about it.</summary>
    private static string Prefix(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..6].ToLowerInvariant();

    private void Publish()
    {
        List<AgentNews> unread;
        lock (_gate)
        {
            unread = _sources.Values
                .SelectMany(s => s.Items.Where(i => !i.Read).Select(i => new AgentNews(s.Prefix + "." + i.Id, i.Title, i.Text, DateTimeOffset.FromUnixTimeSeconds(i.T), i.Lvl)))
                .OrderByDescending(n => n.AtUtc)
                .Take(20)
                .ToList();
        }

        var old = Volatile.Read(ref _unread);
        if (old.SequenceEqual(unread))
        {
            return;
        }

        Volatile.Write(ref _unread, unread);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
