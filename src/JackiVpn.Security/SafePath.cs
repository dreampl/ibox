namespace JackiVpn.Security;

/// <summary>Path hygiene for anything derived from untrusted input (imported file names, rule-set tags…).</summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Converts arbitrary text into a safe single file name component (no separators, no traversal, no
    /// reserved device names, no ADS colon, bounded length).
    /// </summary>
    public static string ToSafeFileName(string? name, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => c < 32 || invalid.Contains(c) || c is ':' or '/' or '\\' ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim(' ', '.');
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
        }

        if (cleaned.Length == 0)
        {
            return fallback;
        }

        var stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    /// <summary>Combines <paramref name="root"/> with a relative path and guarantees the result stays under root.</summary>
    public static string CombineUnderRoot(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(relative);
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Absolute paths are not allowed here.");
        }

        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path escapes its root directory.");
        }

        return combined;
    }

    public static bool IsUnder(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
