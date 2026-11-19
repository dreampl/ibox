using System.Security.Cryptography;
using System.Text;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Account;

/// <summary>One subscription of the Telegram account, and whether IBOX already has it.</summary>
public sealed record AccountLink(SubscriptionCard Card, bool Added);

/// <summary>
/// After a Telegram sign-in, adds the account's subscription links (the ones the bot knows for this user) to
/// IBOX's own subscriptions, so the user does not copy them by hand. Each link is imported once: one the user
/// later deletes is not brought back. Links already present are left alone.
/// </summary>
public sealed class AccountSubscriptionImport
{
    private readonly IboxServices _ibox;
    private readonly SubscriptionService _service;
    private readonly ISubscriptionRepository _repository;
    private readonly string _seenFile;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountSubscriptionImport(IboxServices ibox, SubscriptionService service, ISubscriptionRepository repository, string dataDir, ILogger? log = null)
    {
        _ibox = ibox;
        _service = service;
        _repository = repository;
        _log = log;
        _seenFile = Path.Combine(dataDir, "account", "imported-links");
    }

    /// <summary>Raised with the number of subscriptions just added (only when more than zero).</summary>
    public event EventHandler<int>? Imported;

    /// <summary>Imports on every sign-in and whenever the account view changes; once now if already signed in.</summary>
    public void Attach(CancellationToken lifetime)
    {
        _ibox.SignInChanged += (_, _) =>
        {
            if (_ibox.SignedIn)
            {
                _ = RunAsync(null, lifetime);
            }
        };
        _ibox.Watcher.Updated += (_, view) => _ = RunAsync(view, lifetime);
        if (_ibox.SignedIn)
        {
            _ = RunAsync(null, lifetime);
        }
    }

    private async Task RunAsync(AccountView? view, CancellationToken ct)
    {
        try
        {
            var added = await ImportAsync(view, ct).ConfigureAwait(false);
            if (added > 0)
            {
                Imported?.Invoke(this, added);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("Account subscription import failed: {Error}", ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Adds the account's links that are new to this device. Returns how many were added.</summary>
    public async Task<int> ImportAsync(AccountView? view, CancellationToken ct)
    {
        if (!_ibox.SignedIn)
        {
            return 0;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (view is null)
            {
                (_, view) = await _ibox.Account.GetAsync(null, 0, ct).ConfigureAwait(false);
                if (view is null)
                {
                    return 0;
                }
            }

            var links = view.Subscriptions
                .Where(c => c.Link is not null)
                .GroupBy(c => Normalize(c.Link!.AbsoluteUri))
                .Select(g => g.First())
                .ToList();
            if (links.Count == 0)
            {
                return 0;
            }

            var seen = ReadSeen();
            var existing = await ExistingAsync(ct).ConfigureAwait(false);

            var added = 0;
            foreach (var card in links)
            {
                var key = Normalize(card.Link!.AbsoluteUri);
                var hash = Hash(key);
                if (existing.Contains(key))
                {
                    seen.Add(hash);
                    continue;
                }

                if (seen.Contains(hash))
                {
                    continue; // imported before and deleted by the user since
                }

                try
                {
                    await _service.AddAsync(card.Link.AbsoluteUri, Name(card), null, ct).ConfigureAwait(false);
                    added++;
                    seen.Add(hash);
                    _log?.LogInformation("Imported the account's subscription {Name}.", Name(card));
                }
                catch (Exception ex) when (ex is SubscriptionException or IOException or HttpRequestException)
                {
                    // Not marked seen: tried again on the next account update.
                    _log?.LogWarning("Could not import the account's subscription {Name}: {Error}", Name(card), ex.Message);
                }
            }

            WriteSeen(seen);
            return added;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The account's subscriptions that have a link, each with whether IBOX already has it.</summary>
    public async Task<IReadOnlyList<AccountLink>> ListAsync(CancellationToken ct)
    {
        if (!_ibox.SignedIn)
        {
            return [];
        }

        var view = _ibox.Watcher.Current;
        if (view is null)
        {
            (_, view) = await _ibox.Account.GetAsync(null, 0, ct).ConfigureAwait(false);
        }

        if (view is null)
        {
            throw new InvalidOperationException("Your Telegram account did not answer; try again in a moment.");
        }

        var existing = await ExistingAsync(ct).ConfigureAwait(false);
        return view.Subscriptions
            .Where(c => c.Link is not null)
            .GroupBy(c => Normalize(c.Link!.AbsoluteUri))
            .Select(g => new AccountLink(g.First(), existing.Contains(g.Key)))
            .ToList();
    }

    /// <summary>Adds one the person picked (even one deleted earlier). Returns the new subscription.</summary>
    public async Task<Domain.Subscriptions.Subscription> AddChosenAsync(SubscriptionCard card, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);
        var link = card.Link ?? throw new SubscriptionException("This subscription has no link yet.");
        var sub = await _service.AddAsync(link.AbsoluteUri, Name(card), null, ct).ConfigureAwait(false);
        var seen = ReadSeen();
        seen.Add(Hash(Normalize(link.AbsoluteUri)));
        WriteSeen(seen);
        return sub;
    }

    private async Task<HashSet<string>> ExistingAsync(CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in await _repository.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (await _repository.GetSourceUrlAsync(sub.Id, ct).ConfigureAwait(false) is { } url)
            {
                existing.Add(Normalize(url));
            }
        }

        return existing;
    }

    private static string Name(SubscriptionCard card) =>
        string.IsNullOrWhiteSpace(card.Username) ? "IBOX" : $"IBOX · {card.Username.Trim()}";

    private static string Normalize(string url) => url.Trim().TrimEnd('/');

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private HashSet<string> ReadSeen()
    {
        try
        {
            return File.Exists(_seenFile)
                ? new HashSet<string>(File.ReadAllLines(_seenFile).Where(l => l.Length > 0), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void WriteSeen(HashSet<string> seen)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_seenFile)!);
            File.WriteAllLines(_seenFile, seen.Order(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // re-checked next time; already-present links are skipped anyway
        }
    }
}
