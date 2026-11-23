using System.Security.Cryptography;
using JackiVpn.Security;

namespace JackiVpn.Application.Account;

/// <summary>App session tokens (IBOX panel, music) kept in the OS secure store.</summary>
public interface ISecureTokenStore
{
    string? Get(string name);

    void Set(string name, string token);

    void Remove(string name);
}

/// <summary>
/// One file per token, encrypted with the platform's <see cref="ISecretProtector"/>: DPAPI (Windows user), the Android
/// Keystore, or the 0600 key file on Linux. A copied file is useless on another account or device.
/// </summary>
public sealed class ProtectedFileTokenStore : ISecureTokenStore
{
    private const string Purpose = "ibox-session-token";
    private readonly string _dir;
    private readonly ISecretProtector _protector;

    public ProtectedFileTokenStore(string dir, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        ArgumentNullException.ThrowIfNull(protector);
        _dir = dir;
        _protector = protector;
    }

    public string? Get(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(File.ReadAllBytes(path), Purpose + ":" + name);
        }
        catch (Exception e) when (e is CryptographicException or IOException or FormatException)
        {
            return null;
        }
    }

    public void Set(string name, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        SecureFileSystem.CreatePrivateDirectory(_dir, PrivateScope.CurrentUser);
        var tmp = PathOf(name) + ".tmp";
        File.WriteAllBytes(tmp, _protector.Protect(token, Purpose + ":" + name));
        File.Move(tmp, PathOf(name), overwrite: true);
    }

    public void Remove(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathOf(string name)
    {
        if (name.Length is 0 or > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("Bad token name.", nameof(name));
        }

        return Path.Combine(_dir, name + ".tok");
    }
}

/// <summary>For tests and previews.</summary>
public sealed class MemoryTokenStore : ISecureTokenStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public string? Get(string name)
    {
        lock (_tokens)
        {
            return _tokens.GetValueOrDefault(name);
        }
    }

    public void Set(string name, string token)
    {
        lock (_tokens)
        {
            _tokens[name] = token;
        }
    }

    public void Remove(string name)
    {
        lock (_tokens)
        {
            _tokens.Remove(name);
        }
    }
}
