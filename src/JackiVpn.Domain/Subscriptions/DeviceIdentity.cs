using System.Security.Cryptography;
using JackiVpn.Domain.Common;

namespace JackiVpn.Domain.Subscriptions;

/// <summary>
/// What a device-limited provider (PasarGuard ≥ 5.1, Remnawave, …) needs to count devices: the de-facto standard
/// <c>x-hwid</c>, <c>x-device-os</c>, <c>x-ver-os</c> and <c>x-device-model</c> request headers.
/// The ID is random per installation (never derived from hardware), so it cannot fingerprint the machine and is
/// meaningless outside this app; the model is coarse ("Windows PC", phone model) — never the computer name.
/// </summary>
public sealed record DeviceIdentity(string Hwid, string Os, string OsVersion, string Model)
{
    public const string HwidHeader = "x-hwid";
    public const string OsHeader = "x-device-os";
    public const string OsVersionHeader = "x-ver-os";
    public const string ModelHeader = "x-device-model";

    /// <summary>32 lowercase hex characters (within every panel's accepted 10–64 [A-Za-z0-9=-] range).</summary>
    public static string NewHwid() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValidHwid(string? value) =>
        value is { Length: >= 10 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or '-');

    public IReadOnlyDictionary<string, string> Headers()
    {
        if (!IsValidHwid(Hwid))
        {
            throw new InvalidOperationException("Invalid device ID.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [HwidHeader] = Hwid };
        Add(headers, OsHeader, Os);
        Add(headers, OsVersionHeader, OsVersion);
        Add(headers, ModelHeader, Model);
        return headers;
    }

    private static void Add(Dictionary<string, string> headers, string name, string value)
    {
        // Header values must be printable ASCII without control characters; anything else is dropped.
        var clean = new string(value.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        if (clean.Length is > 0 and <= 64 && !NetText.HasControlChars(clean))
        {
            headers[name] = clean;
        }
    }
}
