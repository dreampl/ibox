using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Parsing;

namespace JackiVpn.Application.Profiles;

public sealed record ImportResult(IReadOnlyList<ProxyProfile> Imported, IReadOnlyList<ParseIssue> Issues, ConfigFormat Format);

public sealed class ProfileException(string message) : Exception(message);

/// <summary>Single configs (independent of subscriptions): import, rename, duplicate, edit, delete, favorites.</summary>
public sealed class ProfileService(IProfileRepository profiles, IConfigParser parser, TimeProvider? time = null)
{
    public const long MaxImportFileBytes = 4 * 1024 * 1024;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Imports one or more configs from pasted text (URI, URI list, Base64, sing-box/Xray JSON, Clash YAML).</summary>
    public async Task<ImportResult> ImportTextAsync(string text, CancellationToken ct)
    {
        var result = parser.Parse(text, new ParseContext { OriginKind = ProfileOriginKind.Manual, NowUtc = _time.GetUtcNow() });
        if (result.IsHtml)
        {
            throw new ProfileException("This looks like a web page, not a config. Paste the subscription URL in Subscriptions instead.");
        }

        var imported = new List<ProxyProfile>();
        var existing = (await profiles.GetAllAsync(ct).ConfigureAwait(false)).Where(p => p.Origin.Kind != ProfileOriginKind.Subscription).Select(p => p.Fingerprint).ToHashSet(StringComparer.Ordinal);
        foreach (var p in result.Profiles)
        {
            if (!existing.Add(p.Fingerprint))
            {
                continue; // exact duplicate of a stored single config
            }

            await profiles.UpsertAsync(p, ct).ConfigureAwait(false);
            imported.Add(p);
        }

        return new ImportResult(imported, result.Issues, result.Format);
    }

    /// <summary>Imports from a file chosen by the user (size-limited; file name never used as a path).</summary>
    public async Task<ImportResult> ImportFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ProfileException("File not found.");
        }

        if (info.Length > MaxImportFileBytes)
        {
            throw new ProfileException($"File is too large (max {MaxImportFileBytes / 1024 / 1024} MB).");
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var r = await ImportTextAsync(text, ct).ConfigureAwait(false);
        return r with { Imported = r.Imported.Select(p => p with { Origin = p.Origin with { Kind = ProfileOriginKind.File } }).ToArray() };
    }

    public async Task<ProxyProfile> RenameAsync(string id, string name, CancellationToken ct)
    {
        var p = await Require(id, ct).ConfigureAwait(false);
        var clean = ProfileFactory.NormalizeName(name);
        if (clean.Length == 0)
        {
            throw new ProfileException("Name must not be empty.");
        }

        var updated = p with { User = p.User with { CustomName = clean } };
        await profiles.UpsertAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<ProxyProfile> SetFavoriteAsync(string id, bool favorite, CancellationToken ct)
    {
        var p = await Require(id, ct).ConfigureAwait(false);
        var updated = p with { User = p.User with { IsFavorite = favorite } };
        await profiles.UpsertAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Duplicates any profile (including subscription ones) as an independent manual profile.</summary>
    public async Task<ProxyProfile> DuplicateAsync(string id, CancellationToken ct)
    {
        var p = await Require(id, ct).ConfigureAwait(false);
        var copy = p with
        {
            Id = ProfileFactory.NewId(),
            Name = p.DisplayName + " (copy)",
            User = new ProfileUserData(),
            Origin = p.Origin with { Kind = ProfileOriginKind.Manual, SubscriptionId = null },
            SyncState = ProfileSyncState.Unchanged,
        };
        await profiles.UpsertAsync(copy, ct).ConfigureAwait(false);
        return copy;
    }

    /// <summary>Saves an edited profile after full validation. Subscription profiles are edited as copies.</summary>
    public async Task<ProxyProfile> SaveEditedAsync(ProxyProfile edited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var validation = ProfileValidator.Validate(edited);
        if (!validation.IsValid)
        {
            throw new ProfileException(string.Join(" ", validation.Errors.Select(e => $"{e.Field}: {e.Message}")));
        }

        var stored = await profiles.GetAsync(edited.Id, ct).ConfigureAwait(false);
        var target = stored is { Origin.Kind: ProfileOriginKind.Subscription }
            ? edited with { Id = ProfileFactory.NewId(), Origin = edited.Origin with { Kind = ProfileOriginKind.Manual, SubscriptionId = null } }
            : edited;
        target = ProfileIdentity.WithFingerprint(target);
        await profiles.UpsertAsync(target, ct).ConfigureAwait(false);
        return target;
    }

    public Task DeleteAsync(string id, CancellationToken ct) => profiles.DeleteAsync(id, ct);

    /// <summary>Validation report for the UI (errors, warnings such as insecure TLS).</summary>
    public static ValidationResult Validate(ProxyProfile profile) => ProfileValidator.Validate(profile);

    private async Task<ProxyProfile> Require(string id, CancellationToken ct) =>
        await profiles.GetAsync(id, ct).ConfigureAwait(false) ?? throw new ProfileException("Profile not found.");
}
