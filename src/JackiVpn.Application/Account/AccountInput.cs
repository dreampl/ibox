using System.Globalization;

namespace JackiVpn.Application.Account;

public static class AccountInput
{
    public static double Gigabytes(string text, bool allowZero = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = new string(text.Trim().Select(c => c is >= '\u06f0' and <= '\u06f9' ? (char)('0' + c - '\u06f0') : c is >= '\u0660' and <= '\u0669' ? (char)('0' + c - '\u0660') : c == '\u066b' ? '.' : c).ToArray()).Replace("\u066c", "", StringComparison.Ordinal);
        if (!double.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0 || value > 100000 || !allowZero && value == 0)
        {
            throw new ArgumentException(allowZero ? "Enter a volume from 0 to 100000 GB. Use 0 for no member cap." : "Enter a positive volume up to 100000 GB.", nameof(text));
        }
        return value;
    }
}
