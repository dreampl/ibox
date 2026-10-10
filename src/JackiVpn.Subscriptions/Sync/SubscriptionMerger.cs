using System.Globalization;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.Subscriptions.Sync;

public sealed record MergeReport(SubscriptionSyncPlan Plan, int DuplicatesDropped);

/// <summary>
/// Computes how a fresh subscription payload maps onto stored profiles, so an update never grows the list.
/// <list type="bullet">
/// <item>Same fingerprint → same node: id, favorite, custom name and history are kept.</item>
/// <item>Same endpoint key (protocol/server/port/transport/path) → the node was updated in place (e.g. rotated
/// credentials): Changed, id and user data kept.</item>
/// <item>Same name and protocol → Changed. Panels such as PasarGuard pick a random address, SNI, path or REALITY
/// short-id from a list on <i>every</i> request, so fingerprints alone would turn each update into "new" nodes.</item>
/// <item>Same protocol, credential (UUID/password), transport and port → Changed (the remark may contain usage
/// or days left, which changes between updates).</item>
/// <item>No match → Added. Stored nodes not present any more are deleted, unless the user marked them (favorite,
/// custom name, notes, core override): those are kept and flagged RemovedByProvider. Earlier unmarked leftovers
/// are cleaned up the same way.</item>
/// <item>Duplicate entries inside the payload are dropped (first wins).</item>
/// </list>
/// The plan is pure; the repository applies it in one transaction.
/// </summary>
public static class SubscriptionMerger
{
    public static MergeReport Plan(IReadOnlyList<ProxyProfile> existing, IReadOnlyList<ProxyProfile> incoming, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ProxyProfile>(incoming.Count);
        var duplicates = 0;
        foreach (var p in incoming)
        {
            var fp = string.IsNullOrEmpty(p.Fingerprint) ? ProfileIdentity.ComputeFingerprint(p) : p.Fingerprint;
            if (seen.Add(fp))
            {
                unique.Add(p with { Fingerprint = fp });
            }
            else
            {
                duplicates++;
            }
        }

        // Live nodes are matched before nodes already flagged as removed, so old leftovers never win.
        var ordered = existing.OrderBy(e => e.SyncState == ProfileSyncState.RemovedByProvider ? 1 : 0).ToList();
        var byFingerprint = new Dictionary<string, ProxyProfile>(StringComparer.Ordinal);
        foreach (var e in ordered)
        {
            byFingerprint.TryAdd(string.IsNullOrEmpty(e.Fingerprint) ? ProfileIdentity.ComputeFingerprint(e) : e.Fingerprint, e);
        }

        var byEndpoint = Index(ordered, ProfileIdentity.ComputeEndpointKey);
        var byName = Index(ordered, NameKey);
        var byLoose = Index(ordered, LooseKey);

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<ProxyProfile>();
        var changed = new List<ProxyProfile>();
        var unchanged = new List<ProxyProfile>();

        // Pass 1: exact fingerprint matches (so later passes cannot steal an exactly matching node).
        var pending = new List<ProxyProfile>();
        foreach (var p in unique)
        {
            if (byFingerprint.TryGetValue(p.Fingerprint, out var e) && matched.Add(e.Id))
            {
                var wasRemoved = e.SyncState == ProfileSyncState.RemovedByProvider;
                var merged = Carry(p, e, wasRemoved ? ProfileSyncState.Changed : ProfileSyncState.Unchanged, nowUtc);
                if (wasRemoved || !SameContent(p, e))
                {
                    changed.Add(merged);
                }
                else
                {
                    unchanged.Add(merged);
                }
            }
            else
            {
                pending.Add(p);
            }
        }

        // Passes 2–4: progressively looser identities → the same logical node with updated parameters.
        foreach (var (index, key) in new (Dictionary<string, List<ProxyProfile>> Index, Func<ProxyProfile, string?> Key)[]
                 {
                     (byEndpoint, ProfileIdentity.ComputeEndpointKey),
                     (byName, NameKey),
                     (byLoose, LooseKey),
                 })
        {
            var next = new List<ProxyProfile>();
            foreach (var p in pending)
            {
                var k = key(p);
                var candidate = k is not null && index.TryGetValue(k, out var list) ? list.FirstOrDefault(x => !matched.Contains(x.Id)) : null;
                if (candidate is not null)
                {
                    matched.Add(candidate.Id);
                    changed.Add(Carry(p, candidate, ProfileSyncState.Changed, nowUtc));
                }
                else
                {
                    next.Add(p);
                }
            }

            pending = next;
        }

        foreach (var p in pending)
        {
            added.Add(p with
            {
                Id = string.IsNullOrEmpty(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                SyncState = ProfileSyncState.New,
                SyncStateSinceUtc = nowUtc,
            });
        }

        var leftovers = existing.Where(e => !matched.Contains(e.Id)).ToList();
        var removed = leftovers.Where(HasUserData).Select(e => e.Id).ToList();
        var deleted = leftovers.Where(e => !HasUserData(e)).Select(e => e.Id).ToList();
        return new MergeReport(new SubscriptionSyncPlan(added, changed, unchanged, removed, deleted), duplicates);
    }

    /// <summary>The user marked this node; it is kept (flagged) when the provider drops it.</summary>
    public static bool HasUserData(ProxyProfile p) =>
        p.User.IsFavorite || !string.IsNullOrWhiteSpace(p.User.CustomName) || !string.IsNullOrWhiteSpace(p.User.Notes) || p.User.CoreOverride is not null;

    private static Dictionary<string, List<ProxyProfile>> Index(IEnumerable<ProxyProfile> profiles, Func<ProxyProfile, string?> key)
    {
        var index = new Dictionary<string, List<ProxyProfile>>(StringComparer.Ordinal);
        foreach (var e in profiles)
        {
            if (key(e) is not { } k)
            {
                continue;
            }

            if (!index.TryGetValue(k, out var list))
            {
                index[k] = list = [];
            }

            list.Add(e);
        }

        return index;
    }

    private static string? NameKey(ProxyProfile p) =>
        string.IsNullOrWhiteSpace(p.Name) ? null : p.Protocol + "|" + p.Name.Trim().ToLowerInvariant();

    private static string? LooseKey(ProxyProfile p)
    {
        var credential = p.Credentials.Uuid ?? p.Credentials.Password ?? p.Credentials.Username ?? p.WireGuard?.PeerPublicKey;
        return string.IsNullOrEmpty(credential)
            ? null
            : string.Join('|', p.Protocol, credential.Trim().ToLowerInvariant(), p.Transport.Type, p.Port.ToString(CultureInfo.InvariantCulture));
    }

    private static ProxyProfile Carry(ProxyProfile incoming, ProxyProfile existing, ProfileSyncState state, DateTimeOffset now) =>
        incoming with
        {
            Id = existing.Id,
            User = existing.User,
            SyncState = state,
            SyncStateSinceUtc = state == existing.SyncState ? existing.SyncStateSinceUtc ?? now : now,
        };

    private static bool SameContent(ProxyProfile a, ProxyProfile b) =>
        a.Name == b.Name
        && string.Equals(a.Origin.RawSource, b.Origin.RawSource, StringComparison.Ordinal)
        && a.Tls.Fingerprint == b.Tls.Fingerprint
        && a.Tls.Alpn.SequenceEqual(b.Tls.Alpn)
        && Equals(a.Tls.Fragment, b.Tls.Fragment);
}
