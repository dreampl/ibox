using System.Buffers;
using System.Text;

namespace JackiVpn.Subscriptions.Parsing;

/// <summary>Tolerant Base64 (standard or URL-safe, padded or not, with embedded whitespace) → strict UTF-8.</summary>
public static class Base64Text
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryDecodeToString(string? input, int maxChars, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrEmpty(input) || input.Length > maxChars)
        {
            return false;
        }

        if (!TryDecode(input, out var bytes))
        {
            return false;
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // Reject binary garbage that happens to be valid UTF-8.
        var control = 0;
        foreach (var c in decoded)
        {
            if (char.IsControl(c) && c is not '\r' and not '\n' and not '\t')
            {
                control++;
            }
        }

        return control == 0 || control < decoded.Length / 100;
    }

    public static bool TryDecode(string input, out byte[] bytes)
    {
        bytes = [];
        var sb = new StringBuilder(input.Length + 3);
        foreach (var c in input)
        {
            switch (c)
            {
                case '-':
                    sb.Append('+');
                    break;
                case '_':
                    sb.Append('/');
                    break;
                case ' ' or '\r' or '\n' or '\t':
                    break;
                default:
                    if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
                    {
                        return false;
                    }

                    sb.Append(c);
                    break;
            }
        }

        // Strip existing padding, then re-pad correctly (some providers emit wrong padding).
        var core = sb.ToString().TrimEnd('=');
        if (core.Length == 0 || core.Length % 4 == 1)
        {
            return false;
        }

        var padded = core.PadRight(core.Length + ((4 - (core.Length % 4)) % 4), '=');
        var buffer = ArrayPool<byte>.Shared.Rent(padded.Length);
        try
        {
            if (!Convert.TryFromBase64String(padded, buffer, out var written))
            {
                return false;
            }

            bytes = buffer.AsSpan(0, written).ToArray();
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Cheap pre-check before attempting a decode of a whole subscription body.</summary>
    public static bool LooksLikeBase64(string text)
    {
        var sample = text.Length > 4096 ? text[..4096] : text;
        var valid = 0;
        foreach (var c in sample)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_' or '\r' or '\n' or ' ')
            {
                valid++;
            }
        }

        return sample.Length > 0 && valid == sample.Length && !sample.Contains("://", StringComparison.Ordinal);
    }
}
