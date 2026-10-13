using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JackiVpn.Application.Settings;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Accounts;

/// <summary>What an import brought in.</summary>
public sealed record AccountImportResult(string Name, int Subscriptions, int Servers, string From, IReadOnlyList<string> Notes);

/// <summary>
/// Moves an account between devices — Windows, Linux and Android in any direction — as one ".ibox" file: the
/// profile (name, colour, picture), its settings, the subscriptions (links), manually added servers and the Dual
/// VPN. Everything is encrypted with a password the user chooses (AES-256-GCM, key from PBKDF2-SHA256), because the
/// file holds subscription links and server keys. On import the device keeps what belongs to it (start-up, logging,
/// the notch's place, AI-agent permission, update channel), subscriptions refresh by themselves, and anything that
/// needs this device (IPsec connection, Kerio adapter) is set up again here.
/// </summary>
public sealed class AccountTransfer(
    SettingsStore settings,
    IProfileRepository profiles,
    ISubscriptionRepository subscriptions,
    SubscriptionService subscriptionService,
    SecondVpnService? dualVpn,
    LocalAccountService? accounts,
    string platform,
    ILogger logger)
{
    public const string Extension = ".ibox";
    public const int MinPasswordLength = 6;
    public const long MaxFileBytes = 16 * 1024 * 1024;
    private const int Iterations = 310_000;
    private static readonly byte[] Magic = "IBOXACCT1\n"u8.ToArray();

    private sealed record Payload
    {
        public int Version { get; init; } = 1;
        public string From { get; init; } = string.Empty;
        public DateTimeOffset ExportedUtc { get; init; }
        public string Name { get; init; } = "Me";
        public int Color { get; init; }
        public string? Picture { get; init; }
        public string? PictureExt { get; init; }
        public AppSettings Settings { get; init; } = new();
        public string? DualPassword { get; init; }
        public string? DualPsk { get; init; }
        public IReadOnlyList<SubscriptionEntry> Subscriptions { get; init; } = [];
        public IReadOnlyList<ProxyProfile> Servers { get; init; } = [];
    }

    private sealed record SubscriptionEntry(string Name, string Url, SubscriptionProviderKind Provider, AutoUpdateInterval AutoUpdate, bool? SendDeviceId);

    public static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? $"Choose a password of at least {MinPasswordLength} characters (you need it to open the file on the other device)." : null;

    /// <summary>A suggested file name: "IBOX-Name-2026-10-12.ibox".</summary>
    public string SuggestedFileName()
    {
        var name = accounts?.Active?.Name ?? "account";
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return $"IBOX-{(safe.Length > 0 ? safe : "account")}-{DateTime.Now:yyyy-MM-dd}{Extension}";
    }

    /// <summary>The active account as an encrypted file.</summary>
    public async Task<byte[]> ExportAsync(string password, CancellationToken ct)
    {
        if (PasswordProblem(password) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var subs = new List<SubscriptionEntry>();
        foreach (var s in await subscriptions.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (await subscriptions.GetSourceUrlAsync(s.Id, ct).ConfigureAwait(false) is { Length: > 0 } url)
            {
                subs.Add(new SubscriptionEntry(s.Name, url, s.Provider, s.AutoUpdate, s.SendDeviceId));
            }
        }

        // Subscription servers come back with the subscription; manual ones (and the Dual VPN's config) travel.
        var manual = (await profiles.GetAllAsync(ct).ConfigureAwait(false)).Where(p => p.Origin.SubscriptionId is null).ToList();
        var account = accounts?.Active;
        string? picture = null;
        string? ext = null;
        if (account is not null && accounts!.PicturePath(account) is { } path && File.Exists(path) && new FileInfo(path).Length <= LocalAccountService.MaxPictureBytes)
        {
            picture = Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            ext = Path.GetExtension(path);
        }

        var (dualPassword, dualPsk) = dualVpn?.ReadSecrets() ?? (null, null);
        var current = settings.Current;
        var payload = new Payload
        {
            From = platform,
            ExportedUtc = DateTimeOffset.UtcNow,
            Name = account?.Name ?? "Me",
            Color = account?.Color ?? 0,
            Picture = picture,
            PictureExt = ext,
            Settings = current with { DualVpn = current.DualVpn with { Secret = null } },
            DualPassword = dualPassword,
            DualPsk = dualPsk,
            Subscriptions = subs,
            Servers = manual,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonDefaults.Storage);
        logger.LogInformation("Account exported: {Subs} subscriptions, {Servers} servers.", subs.Count, manual.Count);
        return Seal(json, password);
    }

    /// <summary>Opens a file from any device and brings the account in (as a new profile where profiles exist).</summary>
    public async Task<(AccountImportResult? Result, string? Problem)> ImportAsync(byte[] file, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Open(file, password), JsonDefaults.Storage);
        }
        catch (InvalidDataException)
        {
            return (null, "This is not an IBOX account file.");
        }
        catch (CryptographicException)
        {
            return (null, "Wrong password (or the file is damaged).");
        }
        catch (JsonException)
        {
            return (null, "The account file is damaged.");
        }

        if (payload is null || payload.Version > 1)
        {
            return (null, "This account file comes from a newer IBOX. Update IBOX here first.");
        }

        var notes = new List<string>();

        // Servers first: the settings may point at them (the chosen server, the Dual VPN's WireGuard config).
        var known = (await profiles.GetAllAsync(ct).ConfigureAwait(false)).ToList();
        var servers = 0;
        foreach (var p in payload.Servers.Take(2000))
        {
            if (!ProfileValidator.Validate(p).IsValid)
            {
                continue;
            }

            if (known.FirstOrDefault(k => k.Id == p.Id || k.Fingerprint == p.Fingerprint) is { } same && same.Id != p.Id && p.Origin.ProviderId != SecondVpnService.OwnedTag)
            {
                continue; // already here under another id
            }

            await profiles.UpsertAsync(p, ct).ConfigureAwait(false);
            servers++;
        }

        // Subscriptions: added as they were (no detection round trip), refreshed in the background.
        var existingUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in await subscriptions.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (await subscriptions.GetSourceUrlAsync(s.Id, ct).ConfigureAwait(false) is { } url)
            {
                existingUrls.Add(url);
            }
        }

        var added = new List<string>();
        foreach (var s in payload.Subscriptions.Take(100))
        {
            if (existingUrls.Contains(s.Url) || !Security.UrlSafetyPolicy.ValidateInitial(s.Url, out var uri).Allowed)
            {
                continue;
            }

            var sub = new Subscription
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(s.Name) ? uri!.Host : s.Name.Trim(),
                Provider = s.Provider,
                MaskedUrl = Security.SecretRedactor.MaskUrl(uri!.ToString()),
                Host = uri.IdnHost,
                AutoUpdate = s.AutoUpdate,
                SendDeviceId = s.SendDeviceId,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            await subscriptions.AddAsync(sub, uri.ToString(), ct).ConfigureAwait(false);
            added.Add(sub.Id);
        }

        // Settings: the account's own, with what belongs to this device kept from this device.
        var here = settings.Current;
        var imported = SettingsMigrations.Upgrade(payload.Settings);
        var serverIds = (await profiles.GetAllAsync(ct).ConfigureAwait(false)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var merged = Merge(imported, here, serverIds);
        if (accounts is not null)
        {
            var name = UniqueName(payload.Name);
            if (await accounts.ImportAsync(name, payload.Color, payload.Picture is { } pic ? Convert.FromBase64String(pic) : null, payload.PictureExt, merged, ct).ConfigureAwait(false) is { } problem)
            {
                return (null, problem);
            }
        }
        else
        {
            settings.Update(_ => merged);
            await settings.FlushAsync().ConfigureAwait(false);
        }

        // Dual VPN parts that live in this device's system.
        if (dualVpn is not null && merged.DualVpn is { IsConfigured: true } dual)
        {
            if (dual.Kind == SecondVpnKind.Ipsec && dualVpn.SupportsIpsec && payload.DualPassword is not null)
            {
                var draft = new SecondVpnDraft
                {
                    Name = dual.Name, Kind = dual.Kind, Host = dual.Host, IpsecType = dual.IpsecType, Username = dual.Username,
                    Password = payload.DualPassword, PreSharedKey = payload.DualPsk,
                    Domains = string.Join(", ", dual.Domains), Cidrs = string.Join(", ", dual.Cidrs), Dns = string.Join(", ", dual.Dns),
                };
                if (await dualVpn.SaveAsync(draft, ct).ConfigureAwait(false) is { } problem)
                {
                    notes.Add("Dual VPN (IPsec) needs attention: " + problem);
                }
            }
            else if (dual.Kind == SecondVpnKind.Kerio && SecondVpnService.FindKerioAdapter() is { } kerio)
            {
                settings.Update(s => s with { DualVpn = s.DualVpn with { Interface = kerio } });
            }
            else if (dual.Kind is SecondVpnKind.Ipsec or SecondVpnKind.Kerio or SecondVpnKind.App)
            {
                notes.Add("Dual VPN: open it on the Servers page once to finish its setup on this device.");
            }
        }

        foreach (var id in added)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await subscriptionService.RefreshAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogWarning("Imported subscription not refreshed yet: {Error}", Security.SecretRedactor.Redact(ex.Message));
                }
            }, CancellationToken.None);
        }

        logger.LogInformation("Account imported from {From}: {Subs} subscriptions, {Servers} servers.", payload.From, added.Count, servers);
        return (new AccountImportResult(payload.Name, added.Count, servers, payload.From, notes), null);
    }

    /// <summary>The imported settings, keeping this device's own parts (and dropping links to servers it lacks).</summary>
    public static AppSettings Merge(AppSettings imported, AppSettings here, IReadOnlySet<string> serverIds)
    {
        var selected = imported.Connection.SelectedProfileId is { } id && serverIds.Contains(id) ? id : null;
        var dual = imported.DualVpn with { Secret = null };
        if (dual.Route == WorkRoute.Server && (dual.ServerId is null || !serverIds.Contains(dual.ServerId)))
        {
            dual = dual with { Enabled = false };
        }

        return imported with
        {
            Startup = here.Startup,
            Device = here.Device,
            Logging = here.Logging,
            MobileProxy = here.MobileProxy,
            Agent = here.Agent,
            Updates = here.Updates,
            Ui = imported.Ui with { Notch = imported.Ui.Notch with { Position = here.Ui.Notch.Position, Display = here.Ui.Notch.Display } },
            Connection = imported.Connection with { SelectedProfileId = selected, SmartMode = imported.Connection.SmartMode || selected is null },
            DualVpn = dual,
        };
    }

    private string UniqueName(string name)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "Imported" : name.Trim();
        n = n.Length > 28 ? n[..28] : n;
        if (accounts is null || accounts.Accounts.All(a => !string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            return n;
        }

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{n} ({i})";
            if (accounts.Accounts.All(a => !string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return n;
    }

    // ------------------------------------------------------------------ file format
    // "IBOXACCT1\n" | salt (16) | nonce (12) | tag (16) | AES-256-GCM(json); key = PBKDF2-SHA256(password, salt).

    public static byte[] Seal(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }

        CryptographicOperations.ZeroMemory(key);
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    public static byte[] Open(byte[] file, string password)
    {
        if (file.Length < Magic.Length + 44 || file.Length > MaxFileBytes || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an IBOX account file.");
        }

        var at = Magic.Length;
        var salt = file.AsSpan(at, 16);
        var nonce = file.AsSpan(at + 16, 12);
        var tag = file.AsSpan(at + 28, 16);
        var cipher = file.AsSpan(at + 44);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? string.Empty), salt.ToArray(), Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
