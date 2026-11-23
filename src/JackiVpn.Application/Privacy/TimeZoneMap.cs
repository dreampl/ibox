using System.Text.RegularExpressions;

namespace JackiVpn.Application.Privacy;

/// <summary>Which time zone an exit is in: the geo lookup's IANA id, else the country's main zone.</summary>
public static partial class TimeZoneMap
{
    /// <summary>
    /// Main zone per country (the capital's or the most populous one for countries with several). Used only when
    /// the geo lookup gave no zone.
    /// </summary>
    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AE"] = "Asia/Dubai", ["AF"] = "Asia/Kabul", ["AL"] = "Europe/Tirane", ["AM"] = "Asia/Yerevan", ["AR"] = "America/Argentina/Buenos_Aires",
        ["AT"] = "Europe/Vienna", ["AU"] = "Australia/Sydney", ["AZ"] = "Asia/Baku", ["BA"] = "Europe/Sarajevo", ["BD"] = "Asia/Dhaka",
        ["BE"] = "Europe/Brussels", ["BG"] = "Europe/Sofia", ["BH"] = "Asia/Bahrain", ["BR"] = "America/Sao_Paulo", ["BY"] = "Europe/Minsk",
        ["CA"] = "America/Toronto", ["CH"] = "Europe/Zurich", ["CL"] = "America/Santiago", ["CN"] = "Asia/Shanghai", ["CO"] = "America/Bogota",
        ["CY"] = "Asia/Nicosia", ["CZ"] = "Europe/Prague", ["DE"] = "Europe/Berlin", ["DK"] = "Europe/Copenhagen", ["EE"] = "Europe/Tallinn",
        ["EG"] = "Africa/Cairo", ["ES"] = "Europe/Madrid", ["FI"] = "Europe/Helsinki", ["FR"] = "Europe/Paris", ["GB"] = "Europe/London",
        ["GE"] = "Asia/Tbilisi", ["GR"] = "Europe/Athens", ["HK"] = "Asia/Hong_Kong", ["HR"] = "Europe/Zagreb", ["HU"] = "Europe/Budapest",
        ["ID"] = "Asia/Jakarta", ["IE"] = "Europe/Dublin", ["IL"] = "Asia/Jerusalem", ["IN"] = "Asia/Kolkata", ["IQ"] = "Asia/Baghdad",
        ["IR"] = "Asia/Tehran", ["IS"] = "Atlantic/Reykjavik", ["IT"] = "Europe/Rome", ["JO"] = "Asia/Amman", ["JP"] = "Asia/Tokyo",
        ["KE"] = "Africa/Nairobi", ["KG"] = "Asia/Bishkek", ["KR"] = "Asia/Seoul", ["KW"] = "Asia/Kuwait", ["KZ"] = "Asia/Almaty",
        ["LB"] = "Asia/Beirut", ["LT"] = "Europe/Vilnius", ["LU"] = "Europe/Luxembourg", ["LV"] = "Europe/Riga", ["MA"] = "Africa/Casablanca",
        ["MD"] = "Europe/Chisinau", ["MK"] = "Europe/Skopje", ["MT"] = "Europe/Malta", ["MX"] = "America/Mexico_City", ["MY"] = "Asia/Kuala_Lumpur",
        ["NG"] = "Africa/Lagos", ["NL"] = "Europe/Amsterdam", ["NO"] = "Europe/Oslo", ["NZ"] = "Pacific/Auckland", ["OM"] = "Asia/Muscat",
        ["PH"] = "Asia/Manila", ["PK"] = "Asia/Karachi", ["PL"] = "Europe/Warsaw", ["PT"] = "Europe/Lisbon", ["QA"] = "Asia/Qatar",
        ["RO"] = "Europe/Bucharest", ["RS"] = "Europe/Belgrade", ["RU"] = "Europe/Moscow", ["SA"] = "Asia/Riyadh", ["SE"] = "Europe/Stockholm",
        ["SG"] = "Asia/Singapore", ["SI"] = "Europe/Ljubljana", ["SK"] = "Europe/Bratislava", ["TH"] = "Asia/Bangkok", ["TJ"] = "Asia/Dushanbe",
        ["TM"] = "Asia/Ashgabat", ["TR"] = "Europe/Istanbul", ["TW"] = "Asia/Taipei", ["UA"] = "Europe/Kyiv", ["US"] = "America/New_York",
        ["UZ"] = "Asia/Tashkent", ["VN"] = "Asia/Ho_Chi_Minh", ["ZA"] = "Africa/Johannesburg",
    };

    /// <summary>The exit's IANA zone: the lookup's id when it is well-formed, else the country's main zone, else null.</summary>
    public static string? ForExit(string? lookupZone, string? countryCode)
    {
        if (lookupZone is not null && IsIanaId(lookupZone))
        {
            return lookupZone;
        }

        return countryCode is { Length: 2 } && ByCountry.TryGetValue(countryCode, out var zone) ? zone : null;
    }

    /// <summary>Shape check only ("Area/City[/Sub]"); never a path, never control characters.</summary>
    public static bool IsIanaId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length is > 2 and <= 64 && IanaRegex().IsMatch(id);
    }

    /// <summary>The Windows id for an IANA zone ("Europe/Berlin" → "W. Europe Standard Time"), or null.</summary>
    public static string? ToWindowsId(string? ianaId) =>
        ianaId is not null && IsIanaId(ianaId) && TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windows) ? windows : null;

    [GeneratedRegex(@"^[A-Za-z]{2,20}(/[A-Za-z0-9_+\-]{1,30}){1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IanaRegex();
}
