using System.Security.Cryptography;
using System.Text;
using JackiVpn.Security;

namespace JackiVpn.Linux;

/// <summary>
/// AES-256-GCM with a random key in a file only the user can read (0600), like the Android Keystore protector:
/// a copied database is useless without the key file. The purpose is bound as associated data.
/// </summary>
public sealed class KeyFileSecretProtector : ISecretProtector
{
    private const byte FormatVersion = 1;
    private readonly byte[] _key;

    public KeyFileSecretProtector(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var fs = new FileStream(keyPath, options);
            fs.Write(_key);
        }

        if (_key.Length != 32)
        {
            throw new CryptographicException("The secret key file is damaged.");
        }
    }

    public byte[] Protect(string plaintext, string purpose)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var result = new byte[1 + 12 + 16 + data.Length];
        result[0] = FormatVersion;
        var nonce = result.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, result.AsSpan(29), result.AsSpan(13, 16), Encoding.UTF8.GetBytes(purpose));
        CryptographicOperations.ZeroMemory(data);
        return result;
    }

    public string Unprotect(byte[] protectedData, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        if (protectedData.Length < 29 || protectedData[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported protected data format.");
        }

        var plain = new byte[protectedData.Length - 29];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(protectedData.AsSpan(1, 12), protectedData.AsSpan(29), protectedData.AsSpan(13, 16), plain, Encoding.UTF8.GetBytes(purpose));
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
