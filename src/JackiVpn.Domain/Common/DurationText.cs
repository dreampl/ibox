using System.Globalization;

namespace JackiVpn.Domain.Common;

/// <summary>Go-style duration strings used by cores ("20ms", "1s", "5m").</summary>
public static class DurationText
{
    public static TimeSpan? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().ToLowerInvariant();
        double factorMs;
        string number;
        if (value.EndsWith("ms", StringComparison.Ordinal))
        {
            factorMs = 1;
            number = value[..^2];
        }
        else if (value.EndsWith('s'))
        {
            factorMs = 1000;
            number = value[..^1];
        }
        else if (value.EndsWith('m'))
        {
            factorMs = 60_000;
            number = value[..^1];
        }
        else if (value.EndsWith('h'))
        {
            factorMs = 3_600_000;
            number = value[..^1];
        }
        else
        {
            // Bare numbers are milliseconds.
            factorMs = 1;
            number = value;
        }

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0 && n < 1e9
            ? TimeSpan.FromMilliseconds(n * factorMs)
            : null;
    }

    public static string Format(TimeSpan d) =>
        d.TotalMilliseconds < 1000 || d.TotalMilliseconds % 1000 != 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(long)d.TotalMilliseconds}ms")
            : string.Create(CultureInfo.InvariantCulture, $"{(long)d.TotalSeconds}s");
}
