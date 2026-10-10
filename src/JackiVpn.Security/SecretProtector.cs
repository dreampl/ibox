using System.Security.Cryptography;
using System.Text;

namespace JackiVpn.Security;

/// <summary>Encrypts secrets at rest (subscription URLs, profile credentials).</summary>
public interface ISecretProtector
{
    byte[] Protect(string plaintext, string purpose);

    string Unprotect(byte[] protectedData, string purpose);
}

#if WINDOWS
/// <summary>
/// Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>): ciphertext is bound to the Windows user
/// account, so a copied database is useless on another account/machine. A versioned header and a
/// purpose-specific entropy prevent ciphertext from one column being replayed into another.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const byte FormatVersion = 1;
    private static readonly byte[] EntropyPrefix = "JackiVpn.v1:"u8.ToArray();
    private readonly DataProtectionScope _scope;

    public DpapiSecretProtector(DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _scope = scope;
    }

    public byte[] Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var cipher = ProtectedData.Protect(data, Entropy(purpose), _scope);
            var result = new byte[cipher.Length + 1];
            result[0] = FormatVersion;
            cipher.CopyTo(result, 1);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string Unprotect(byte[] protectedData, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (protectedData.Length < 2 || protectedData[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported protected data format.");
        }

        var plain = ProtectedData.Unprotect(protectedData.AsSpan(1).ToArray(), Entropy(purpose), _scope);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Entropy(string purpose)
    {
        var p = Encoding.UTF8.GetBytes(purpose);
        var e = new byte[EntropyPrefix.Length + p.Length];
        EntropyPrefix.CopyTo(e, 0);
        p.CopyTo(e, EntropyPrefix.Length);
        return e;
    }
}
#endif

public static class SecretPurposes
{
    public const string SubscriptionUrl = "subscription-url";
    public const string ProfilePayload = "profile-payload";
    public const string SettingsSecret = "settings-secret";
}
