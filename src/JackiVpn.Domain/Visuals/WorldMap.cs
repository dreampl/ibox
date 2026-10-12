namespace JackiVpn.Domain.Visuals;

/// <summary>
/// A dotted world map: land cells of a 144 x 60 grid (2.5 degrees per cell, longitude -180..180, latitude 83.75
/// down to -66.25; Antarctica left out), rasterized from Natural Earth 1:110m land (public domain, via world-atlas).
/// Shared by every platform's map view; nothing is downloaded at run time.
/// </summary>
public static class WorldMap
{
    public const int Columns = 144;
    public const int Rows = 60;
    public const double CellDegrees = 2.5;
    public const double TopLatitude = 83.75;

    private static readonly byte[] Mask = Convert.FromBase64String(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/5///gAAAAAAAAAAAAAAAAEz/P//8ABwAAAADAAAAAAAABKZAP//8AAAAA4AH4AAAA" +
        "AAADwDQAP/8AAAAGAH/4AIAAgEABfG/gH/4AAAAGHf//9/gA+AAAK8TH4Af//gH6FAAAAAAADAAAAAPB4D+/+ALAAAAAAAAA" +
        "AP////UcHwCAD3/////////+A////8BgDgAAPn////////74AeB//+B6AAAAPn///////4CAAIAf//h+AAACFP///////gOA" +
        "AAAP//7/gAANH////////wMAAAAH////wAAD/////////wAAAAAD///04AAD/////////wAAAAAD///8AAAB//X3/////gAA" +
        "AAAD///wAAAB6+Dv/////EAAAAAD///gAAAPhvbz////8IAAAAAB///AAAAPAp/z///+IIAAAAAB///AAAAA8Af/////EwAA" +
        "AAAAf/+AAAAH8AP/////BAAAAAAAX/4AAAAP/uv/////gAAAAAAAP8EAAAAP///v////gAAAAAAAD4EAAAA///3z////AAAA" +
        "AAAAB4AAAAA///3+H//+gAAAAAAAA4gAAAB///7/D+fgAAAAAAAAA9gYAAA///7+B8PQAAAAAAAAAHgAAAB///94B4HggAAA" +
        "AAAAAA4AAAB////gAwHwwAAAAAAAAAAoAAA///+QAwFgAAAAAAAAAAF/AAAf///wAAEAIAAAAAAAAAB/gAAPv//wAICAAAAA" +
        "AAAAAAB/8AAAD//gAAGGAAAAAAAAAAD/8AAAD//AAADOAAAAAAAAAAD//AAAD/+AAADOjAAAAAAAAAD//4AAD/8AAABAg8AA" +
        "AAAAAAD//8AAB/8AAAAgAeAAAAAAAAD//8AAB/8AAAABoIAAAAAAAAB//4AAB/8AAAAAAAAAAAAAAAB//wAAB/8QAAAADAAA" +
        "AAAAAAAf/wAAB/8wAAAAfkAAAAAAAAAP/wAAB/wwAAAAf+AAAAAAAAAP/wAAB/xgAAAD//AAAAAAAAAP/AAAA/xgAAAH//AA" +
        "AAAAAAAP+AAAA/gAAAAH//gAAAAAAAAf8AAAAfgAAAAD//gAAAAAAAAf8AAAAfAAAAAD//gAAAAAAAAf4AAAAcAAAAADw/gA" +
        "AAAAAAAfgAAAAAAAAAAAAPAAAAAAAAAfAAAAAAAAAAAAACACAAAAAAA8AAAAAAAAAAAAACAEAAAAAAAcAAAAAAAAAAAAAAAI" +
        "AAAAAAA4AAAAAAAAAAAAAAAQAAAAAAA4AAAAAAAAAAAAAAAAAAAAAAAwgAAAAAAAAAAAAAAAAAAAAAAYAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAAAAAAAAAAAA");

    public static bool IsLand(int column, int row) =>
        column is >= 0 and < Columns && row is >= 0 and < Rows && (Mask[((row * Columns) + column) / 8] & (0x80 >> (((row * Columns) + column) % 8))) != 0;

    /// <summary>Position of a coordinate on the grid (fractional column/row, 0-based cell centres).</summary>
    public static (double Column, double Row) Project(double latitude, double longitude) =>
        ((longitude + 180 - (CellDegrees / 2)) / CellDegrees, (TopLatitude - latitude) / CellDegrees);

    /// <summary>
    /// Approximate location of a country (capital or main hosting city) for the map pulse when the exit lookup did
    /// not return coordinates. Covers the usual VPN exit countries; null for others.
    /// </summary>
    public static (double Latitude, double Longitude)? CountryPoint(string? countryCode) => countryCode?.ToUpperInvariant() switch
    {
        "US" => (39.0, -77.5), "CA" => (43.7, -79.4), "MX" => (19.4, -99.1), "BR" => (-23.5, -46.6), "AR" => (-34.6, -58.4), "CL" => (-33.4, -70.6),
        "GB" => (51.5, -0.1), "IE" => (53.3, -6.3), "FR" => (48.9, 2.4), "DE" => (50.1, 8.7), "NL" => (52.4, 4.9), "BE" => (50.8, 4.4),
        "LU" => (49.6, 6.1), "CH" => (47.4, 8.5), "AT" => (48.2, 16.4), "IT" => (45.5, 9.2), "ES" => (40.4, -3.7), "PT" => (38.7, -9.1),
        "SE" => (59.3, 18.1), "NO" => (59.9, 10.8), "FI" => (60.2, 24.9), "DK" => (55.7, 12.6), "PL" => (52.2, 21.0), "CZ" => (50.1, 14.4),
        "HU" => (47.5, 19.0), "RO" => (44.4, 26.1), "BG" => (42.7, 23.3), "GR" => (38.0, 23.7), "LT" => (54.7, 25.3), "LV" => (56.9, 24.1),
        "EE" => (59.4, 24.7), "UA" => (50.5, 30.5), "RU" => (55.8, 37.6), "TR" => (41.0, 29.0), "IL" => (32.1, 34.8), "AE" => (25.2, 55.3),
        "SA" => (24.7, 46.7), "QA" => (25.3, 51.5), "BH" => (26.2, 50.6), "OM" => (23.6, 58.4), "IR" => (35.7, 51.4), "IQ" => (33.3, 44.4),
        "AM" => (40.2, 44.5), "GE" => (41.7, 44.8), "AZ" => (40.4, 49.9), "KZ" => (43.2, 76.9), "IN" => (19.1, 72.9), "PK" => (24.9, 67.0),
        "SG" => (1.35, 103.8), "HK" => (22.3, 114.2), "JP" => (35.7, 139.7), "KR" => (37.6, 127.0), "TW" => (25.0, 121.5), "CN" => (31.2, 121.5),
        "MY" => (3.1, 101.7), "TH" => (13.8, 100.5), "VN" => (10.8, 106.7), "ID" => (-6.2, 106.8), "PH" => (14.6, 121.0), "AU" => (-33.9, 151.2),
        "NZ" => (-36.8, 174.8), "ZA" => (-26.2, 28.0), "EG" => (30.0, 31.2), "NG" => (6.5, 3.4), "KE" => (-1.3, 36.8), "MD" => (47.0, 28.9),
        "RS" => (44.8, 20.5), "HR" => (45.8, 16.0), "SK" => (48.1, 17.1), "SI" => (46.1, 14.5), "IS" => (64.1, -21.9), "CY" => (35.2, 33.4),
        _ => null,
    };
}