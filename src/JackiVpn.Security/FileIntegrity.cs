using System.Security.Cryptography;

namespace JackiVpn.Security;

public static class FileIntegrity
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public static string ComputeSha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Constant-time comparison of two hex digests (case-insensitive).</summary>
    public static bool DigestEquals(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        Span<byte> x = stackalloc byte[a.Length];
        Span<byte> y = stackalloc byte[b.Length];
        for (var i = 0; i < a.Length; i++)
        {
            x[i] = (byte)char.ToLowerInvariant(a[i]);
            y[i] = (byte)char.ToLowerInvariant(b[i]);
        }

        return CryptographicOperations.FixedTimeEquals(x, y);
    }
}
