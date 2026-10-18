using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Settings;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Accounts;

/// <summary>A person using IBOX on this device: their own settings (routing, DNS, theme, modes, caps…).</summary>
public sealed record LocalAccount
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Index into <see cref="LocalAccountService.AvatarColors"/> (initials avatar).</summary>
    public int Color { get; init; }

    /// <summary>File name of a picture in the profiles folder (wins over <see cref="Avatar"/>), or null.</summary>
    public string? Picture { get; init; }

    /// <summary>The cartoon avatar chosen (<see cref="Domain.Visuals.Avatars"/> id); null = initials on <see cref="Color"/>.</summary>
    public string? Avatar { get; init; }

    /// <summary>PBKDF2 salt (base64) when the profile is locked with a PIN; its settings then need the PIN.</summary>
    public string? PinSalt { get; init; }

    /// <summary>Check value derived from the PIN (never the PIN or the key itself).</summary>
    public string? PinCheck { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLocked => PinSalt is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>
/// Local profiles: several people (or roles: home, work, kids) on one device, each with their own settings. The
/// active profile's settings are the app's settings; every profile's copy is stored encrypted in the profiles
/// folder: with the OS-bound protector (DPAPI / Android Keystore / Linux key file), and, when the profile has a PIN,
/// also with AES-GCM under a key derived from the PIN (PBKDF2-SHA256), so another profile on the same account cannot
/// read or switch to it without the PIN. Servers and subscriptions are shared.
/// </summary>
public sealed class LocalAccountService
{
    public const int MaxAccounts = 12;
    public const int MaxPictureBytes = 2 * 1024 * 1024;
    private const int Iterations = 210_000;
    private static readonly byte[] PinMagic = "IBOXPIN1"u8.ToArray();

    /// <summary>Avatar colours (ARGB) for initials avatars; the same on every platform.</summary>
    public static IReadOnlyList<uint> AvatarColors { get; } = [0xFF3B82F6, 0xFF22C55E, 0xFF8B5CF6, 0xFFF59E0B, 0xFFEC4899, 0xFF14B8A6, 0xFFEF4444, 0xFF64748B];

    private readonly string _dir;
    private readonly ISecretProtector _protector;
    private readonly SettingsStore _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, byte[]> _unlocked = new(StringComparer.Ordinal);
    private State _state = new([], string.Empty);
    private int _pendingSave;

    private sealed record State(IReadOnlyList<LocalAccount> Accounts, string ActiveId);

    public LocalAccountService(string directory, ISecretProtector protector, SettingsStore settings, ILogger logger)
    {
        _dir = directory;
        _protector = protector;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Raised when the list or the active profile changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<LocalAccount> Accounts => _state.Accounts;

    public LocalAccount? Active => _state.Accounts.FirstOrDefault(a => a.Id == _state.ActiveId);

    public string? PicturePath(LocalAccount account) =>
        account?.Picture is { } file ? Path.Combine(_dir, Path.GetFileName(file)) : null;

    /// <summary>Reads the list (call after the settings are loaded). The first start makes one profile from the current settings.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "profiles.json");
        if (File.Exists(file))
        {
            try
            {
                _state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), JsonDefaults.Storage) ?? _state;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Profiles list unreadable ({Error}); starting a new one.", ex.Message);
            }
        }

        // Profiles from before the cartoon avatars (initials on a colour) get one each; a picture still wins.
        if (_state.Accounts.Any(a => a.Avatar is null))
        {
            var used = new List<string?>();
            _state = _state with
            {
                Accounts = _state.Accounts.Select(a =>
                {
                    var next = a.Avatar is null ? a with { Avatar = Domain.Visuals.Avatars.NextFree(used.Concat(_state.Accounts.Select(x => x.Avatar))) } : a;
                    used.Add(next.Avatar);
                    return next;
                }).ToList(),
            };
            await SaveListAsync(ct).ConfigureAwait(false);
        }

        if (_state.Accounts.Count == 0 || Active is null)
        {
            var me = new LocalAccount { Id = NewId(), Name = Environment.UserName is { Length: > 0 } u ? Capitalize(u) : "Me", CreatedUtc = DateTimeOffset.UtcNow, Avatar = Domain.Visuals.Avatars.All[0].Id };
            _state = new State([.. _state.Accounts, me], me.Id);
            await WriteSettingsAsync(me, _settings.Current, ct).ConfigureAwait(false);
            await SaveListAsync(ct).ConfigureAwait(false);
        }

        // Keep the active profile's encrypted copy current (coalesced, off the UI thread).
        _settings.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref _pendingSave, 1) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800).ConfigureAwait(false);
                    Interlocked.Exchange(ref _pendingSave, 0);
                    await SaveActiveAsync(CancellationToken.None).ConfigureAwait(false);
                });
            }
        };
    }

    /// <summary>Adds a profile, starting from the default settings or a copy of the current ones.</summary>
    public async Task<(LocalAccount? Account, string? Problem)> CreateAsync(string name, int color, bool copyCurrent, string? pin, CancellationToken ct, string? avatar = null)
    {
        if (Problem(name) is { } problem)
        {
            return (null, problem);
        }

        if (_state.Accounts.Count >= MaxAccounts)
        {
            return (null, $"Up to {MaxAccounts} profiles.");
        }

        if (pin is not null && PinProblem(pin) is { } pinProblem)
        {
            return (null, pinProblem);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = new LocalAccount
            {
                Id = NewId(),
                Name = name.Trim(),
                Color = Math.Clamp(color, 0, AvatarColors.Count - 1),
                CreatedUtc = DateTimeOffset.UtcNow,
                Avatar = Domain.Visuals.Avatars.Find(avatar)?.Id ?? Domain.Visuals.Avatars.NextFree(_state.Accounts.Select(a => a.Avatar)),
            };
            if (pin is not null)
            {
                account = WithPin(account, pin, out var key);
                _unlocked[account.Id] = key;
            }

            var start = copyCurrent ? _settings.Current : SettingsMigrations.Upgrade(new AppSettings()) with { Ui = _settings.Current.Ui };
            await WriteSettingsAsync(account, start, ct).ConfigureAwait(false);
            _state = _state with { Accounts = [.. _state.Accounts, account] };
            await SaveListAsync(ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
            return (account, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes another profile active: the current settings are saved to the current profile, the other profile's
    /// settings become the app's. A locked profile needs its PIN. Disconnect first (the caller does).
    /// </summary>
    public async Task<string?> SwitchAsync(string id, string? pin, CancellationToken ct)
    {
        var target = _state.Accounts.FirstOrDefault(a => a.Id == id);
        if (target is null)
        {
            return "Unknown profile.";
        }

        if (target.Id == _state.ActiveId)
        {
            return null;
        }

        if (target.IsLocked && !_unlocked.ContainsKey(target.Id))
        {
            if (pin is null || DeriveKey(target, pin) is not { } key)
            {
                return "Wrong PIN.";
            }

            _unlocked[target.Id] = key;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Active is { } current)
            {
                await WriteSettingsAsync(current, _settings.Current, ct).ConfigureAwait(false);
                if (current.IsLocked)
                {
                    // Leaving a locked profile locks it again.
                    _unlocked.Remove(current.Id);
                }
            }

            var settings = await ReadSettingsAsync(target, ct).ConfigureAwait(false) ?? SettingsMigrations.Upgrade(new AppSettings());
            _state = _state with { ActiveId = target.Id };
            await SaveListAsync(ct).ConfigureAwait(false);
            _settings.Update(_ => SettingsMigrations.Upgrade(settings));
            await _settings.FlushAsync().ConfigureAwait(false);
            _logger.LogInformation("Profile {Name} is active.", target.Name);
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Name and colour; with <paramref name="avatar"/> also the cartoon avatar (which replaces a picture).</summary>
    public async Task<string?> RenameAsync(string id, string name, int color, CancellationToken ct, string? avatar = null)
    {
        if (Problem(name) is { } problem)
        {
            return problem;
        }

        var art = Domain.Visuals.Avatars.Find(avatar)?.Id;
        await UpdateAsync(id, a => a with
        {
            Name = name.Trim(),
            Color = Math.Clamp(color, 0, AvatarColors.Count - 1),
            Avatar = art ?? a.Avatar,
            Picture = art is not null && art != a.Avatar ? null : a.Picture,
        }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Sets (or with null, removes) the picture: PNG or JPEG up to 2 MB, copied into the profiles folder.</summary>
    public async Task<string?> SetPictureAsync(string id, string? sourceFile, CancellationToken ct)
    {
        if (sourceFile is null)
        {
            await UpdateAsync(id, a => a with { Picture = null }, ct).ConfigureAwait(false);
            return null;
        }

        var info = new FileInfo(sourceFile);
        if (!info.Exists || info.Length > MaxPictureBytes)
        {
            return "Choose a PNG or JPEG picture up to 2 MB.";
        }

        var header = new byte[8];
        await using (var f = File.OpenRead(sourceFile))
        {
            _ = await f.ReadAsync(header, ct).ConfigureAwait(false);
        }

        var ext = header.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) ? ".png" : header.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg" : null;
        if (ext is null)
        {
            return "Choose a PNG or JPEG picture up to 2 MB.";
        }

        var name = id + ext;
        File.Copy(sourceFile, Path.Combine(_dir, name), overwrite: true);
        await UpdateAsync(id, a => a with { Picture = name }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Sets, changes (needs the old PIN when locked) or with null removes the PIN of a profile.</summary>
    public async Task<string?> SetPinAsync(string id, string? oldPin, string? newPin, CancellationToken ct)
    {
        var account = _state.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null)
        {
            return "Unknown profile.";
        }

        if (newPin is not null && PinProblem(newPin) is { } problem)
        {
            return problem;
        }

        if (account.IsLocked && !_unlocked.ContainsKey(id) && (oldPin is null || DeriveKey(account, oldPin) is not { } k || !Remember(id, k)))
        {
            return "Wrong PIN.";
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = id == _state.ActiveId ? _settings.Current : await ReadSettingsAsync(account, ct).ConfigureAwait(false) ?? new AppSettings();
            LocalAccount updated;
            if (newPin is null)
            {
                updated = account with { PinSalt = null, PinCheck = null };
                _unlocked.Remove(id);
            }
            else
            {
                updated = WithPin(account, newPin, out var key);
                _unlocked[id] = key;
            }

            _state = _state with { Accounts = _state.Accounts.Select(a => a.Id == id ? updated : a).ToList() };
            await WriteSettingsAsync(updated, settings, ct).ConfigureAwait(false);
            await SaveListAsync(ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a profile and its settings (never the active one or the last one).</summary>
    public async Task<string?> DeleteAsync(string id, CancellationToken ct)
    {
        if (id == _state.ActiveId)
        {
            return "Switch to another profile first.";
        }

        var account = _state.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state = _state with { Accounts = _state.Accounts.Where(a => a.Id != id).ToList() };
            _unlocked.Remove(id);
            TryDelete(SettingsFile(id));
            if (account.Picture is { } picture)
            {
                TryDelete(Path.Combine(_dir, Path.GetFileName(picture)));
            }

            await SaveListAsync(ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// An account brought from another device (see <see cref="AccountTransfer"/>): a new profile with those settings and
    /// picture, made active (the current settings are saved to the current profile first).
    /// </summary>
    public async Task<string?> ImportAsync(string name, int color, byte[]? picture, string? pictureExt, AppSettings settings, CancellationToken ct, string? avatar = null)
    {
        if (Problem(name) is { } problem)
        {
            return problem;
        }

        if (_state.Accounts.Count >= MaxAccounts)
        {
            return $"Up to {MaxAccounts} profiles: remove one first.";
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = new LocalAccount
            {
                Id = NewId(),
                Name = name.Trim(),
                Color = Math.Clamp(color, 0, AvatarColors.Count - 1),
                CreatedUtc = DateTimeOffset.UtcNow,
                Avatar = Domain.Visuals.Avatars.Find(avatar)?.Id ?? Domain.Visuals.Avatars.NextFree(_state.Accounts.Select(a => a.Avatar)),
            };
            if (picture is { Length: > 0 and <= MaxPictureBytes } && pictureExt is ".png" or ".jpg")
            {
                await File.WriteAllBytesAsync(Path.Combine(_dir, account.Id + pictureExt), picture, ct).ConfigureAwait(false);
                account = account with { Picture = account.Id + pictureExt };
            }

            if (Active is { } current)
            {
                await WriteSettingsAsync(current, _settings.Current, ct).ConfigureAwait(false);
                if (current.IsLocked)
                {
                    _unlocked.Remove(current.Id);
                }
            }

            await WriteSettingsAsync(account, settings, ct).ConfigureAwait(false);
            _state = new State([.. _state.Accounts, account], account.Id);
            await SaveListAsync(ct).ConfigureAwait(false);
            _settings.Update(_ => SettingsMigrations.Upgrade(settings));
            await _settings.FlushAsync().ConfigureAwait(false);
            _logger.LogInformation("Profile {Name} imported and active.", account.Name);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Writes the active profile's encrypted copy now (also called on exit).</summary>
    public async Task SaveActiveAsync(CancellationToken ct)
    {
        if (Active is not { } active)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteSettingsAsync(active, _settings.Current, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning("Profile settings not saved: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string? Problem(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 32 || name.Any(char.IsControl) ? "Give the profile a name (up to 32 characters)." : null;

    public static string? PinProblem(string pin) =>
        pin.Length is < 4 or > 12 || !pin.All(char.IsAsciiDigit) ? "The PIN must be 4 to 12 digits." : null;

    // ------------------------------------------------------------------ storage

    private bool Remember(string id, byte[] key)
    {
        _unlocked[id] = key;
        return true;
    }

    private async Task UpdateAsync(string id, Func<LocalAccount, LocalAccount> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state = _state with { Accounts = _state.Accounts.Select(a => a.Id == id ? change(a) : a).ToList() };
            await SaveListAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string SettingsFile(string id) => Path.Combine(_dir, id + ".settings");

    private async Task WriteSettingsAsync(LocalAccount account, AppSettings settings, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(settings, JsonDefaults.Storage);
        if (account.IsLocked)
        {
            if (!_unlocked.TryGetValue(account.Id, out var key))
            {
                return; // locked and not opened in this session: its stored copy stays as it is
            }

            json = Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(json), account.Id));
        }

        var bytes = _protector.Protect(json, "ibox-profile:" + account.Id);
        var temp = SettingsFile(account.Id) + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, SettingsFile(account.Id), overwrite: true);
    }

    private async Task<AppSettings?> ReadSettingsAsync(LocalAccount account, CancellationToken ct)
    {
        var file = SettingsFile(account.Id);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = _protector.Unprotect(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), "ibox-profile:" + account.Id);
            if (account.IsLocked)
            {
                json = Encoding.UTF8.GetString(Open(_unlocked[account.Id], Convert.FromBase64String(json), account.Id));
            }

            return JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Storage);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or IOException or KeyNotFoundException)
        {
            _logger.LogWarning("Settings of profile {Name} unreadable ({Error}); defaults used.", account.Name, ex.Message);
            return null;
        }
    }

    private async Task SaveListAsync(CancellationToken ct)
    {
        var file = Path.Combine(_dir, "profiles.json");
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_state, JsonDefaults.Storage), ct).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
    }

    // ------------------------------------------------------------------ PIN

    private static LocalAccount WithPin(LocalAccount account, string pin, out byte[] key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 64);
        key = derived[..32];
        return account with { PinSalt = Convert.ToBase64String(salt), PinCheck = Convert.ToBase64String(derived[32..]) };
    }

    /// <summary>The settings key when the PIN is right, else null (constant-time compare of the check half).</summary>
    internal static byte[]? DeriveKey(LocalAccount account, string pin)
    {
        if (account.PinSalt is null || account.PinCheck is null)
        {
            return null;
        }

        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), Convert.FromBase64String(account.PinSalt), Iterations, HashAlgorithmName.SHA256, 64);
        return CryptographicOperations.FixedTimeEquals(derived.AsSpan(32), Convert.FromBase64String(account.PinCheck)) ? derived[..32] : null;
    }

    private static byte[] Seal(byte[] key, byte[] plain, string id)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(id));
        return [.. PinMagic, .. nonce, .. tag, .. cipher];
    }

    private static byte[] Open(byte[] key, byte[] sealedData, string id)
    {
        if (sealedData.Length < PinMagic.Length + 28 || !sealedData.AsSpan(0, PinMagic.Length).SequenceEqual(PinMagic))
        {
            throw new CryptographicException("Not a PIN-protected profile.");
        }

        var nonce = sealedData.AsSpan(PinMagic.Length, 12);
        var tag = sealedData.AsSpan(PinMagic.Length + 12, 16);
        var cipher = sealedData.AsSpan(PinMagic.Length + 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(id));
        return plain;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
