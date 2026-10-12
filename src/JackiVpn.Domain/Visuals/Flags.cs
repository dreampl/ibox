namespace JackiVpn.Domain.Visuals;

public enum FlagShapeKind
{
    /// <summary>X, Y, Width, Height.</summary>
    Rect,

    /// <summary>CenterX, CenterY, Radius.</summary>
    Circle,

    /// <summary>X1, Y1, X2, Y2, …</summary>
    Polygon,
}

/// <summary>One filled shape of a flag, in a 0…1 square (flags are shown round, composed for the circle).</summary>
public sealed record FlagShape(FlagShapeKind Kind, uint Color, double[] Points);

/// <summary>
/// Simple vector flags of the usual VPN exit countries, drawn for a round badge (like the mockup's flag circles):
/// stripes, Nordic crosses, discs, stars. Every platform renders the same shapes; countries without one get an
/// initials badge. Drawn from the flags' public designs, simplified (no fine emblems).
/// </summary>
public static class Flags
{
    private const uint White = 0xFFFFFFFF;
    private const uint Red = 0xFFE0242F;
    private const uint Blue = 0xFF0B3D91;
    private const uint Black = 0xFF1A1A1A;

    public static IReadOnlyList<FlagShape>? For(string? countryCode) => countryCode?.ToUpperInvariant() switch
    {
        "US" => Us(),
        "GB" => Gb(),
        "DE" => H(Black, 0xFFDD0000, 0xFFFFCE00),
        "NL" => H(0xFFAE1C28, White, 0xFF21468B),
        "FR" => V(0xFF002395, White, 0xFFED2939),
        "IT" => V(0xFF009246, White, 0xFFCE2B37),
        "IE" => V(0xFF169B62, White, 0xFFFF883E),
        "BE" => V(Black, 0xFFFDDA24, 0xFFEF3340),
        "RO" => V(0xFF002B7F, 0xFFFCD116, 0xFFCE1126),
        "MD" => V(0xFF0046AE, 0xFFFFD200, 0xFFCC092F),
        "AT" => H(0xFFED2939, White, 0xFFED2939),
        "LU" => H(0xFFEF3340, White, 0xFF00A3E0),
        "RU" => H(White, 0xFF0039A6, 0xFFD52B1E),
        "BG" => H(White, 0xFF00966E, 0xFFD62612),
        "HU" => H(0xFFCE2939, White, 0xFF477050),
        "LT" => H(0xFFFDB913, 0xFF006A44, 0xFFC1272D),
        "EE" => H(0xFF0072CE, Black, White),
        "LV" => [R(0, 0, 1, 1, 0xFF9E3039), R(0, 0.42, 1, 0.16, White)],
        "UA" => H(0xFF0057B7, 0xFFFFD700),
        "PL" => H(White, 0xFFDC143C),
        "ID" => H(0xFFCE1126, White),
        "AM" => H(0xFFD90012, 0xFF0033A0, 0xFFF2A800),
        "RS" => H(0xFFC6363C, 0xFF0C4076, White),
        "HR" => H(0xFFFF0000, White, 0xFF171796),
        "SI" => H(White, 0xFF0000FF, 0xFFFF0000),
        "SK" => H(White, 0xFF0B4EA2, 0xFFEE1C25),
        "CZ" => [R(0, 0, 1, 0.5, White), R(0, 0.5, 1, 0.5, 0xFFD7141A), P(0xFF11457E, 0, 0, 0.55, 0.5, 0, 1)],
        "IR" => [.. H(0xFF239F40, White, 0xFFDA0000), C(0.5, 0.5, 0.09, 0xFFDA0000), C(0.5, 0.5, 0.06, White)],
        "IN" => [.. H(0xFFFF9933, White, 0xFF138808), C(0.5, 0.5, 0.11, 0xFF000080), C(0.5, 0.5, 0.085, White), C(0.5, 0.5, 0.025, 0xFF000080)],
        "JP" => [R(0, 0, 1, 1, White), C(0.5, 0.5, 0.27, 0xFFBC002D)],
        "BD" => [R(0, 0, 1, 1, 0xFF006A4E), C(0.46, 0.5, 0.27, 0xFFF42A41)],
        "KR" => [R(0, 0, 1, 1, White), C(0.5, 0.5, 0.24, 0xFFCD2E3A), P(0xFF0047A0, 0.26, 0.5, 0.74, 0.5, 0.74, 0.62, 0.62, 0.74, 0.5, 0.74, 0.38, 0.66, 0.3, 0.6), C(0.38, 0.5, 0.12, 0xFFCD2E3A), C(0.62, 0.5, 0.12, 0xFF0047A0)],
        "CN" => [R(0, 0, 1, 1, 0xFFEE1C25), Star(0.32, 0.34, 0.17, 0xFFFFFF00), Star(0.58, 0.18, 0.05, 0xFFFFFF00), Star(0.68, 0.32, 0.05, 0xFFFFFF00), Star(0.68, 0.5, 0.05, 0xFFFFFF00), Star(0.58, 0.62, 0.05, 0xFFFFFF00)],
        "VN" => [R(0, 0, 1, 1, 0xFFDA251D), Star(0.5, 0.52, 0.27, 0xFFFFFF00)],
        "TW" => [R(0, 0, 1, 1, 0xFFFE0000), R(0, 0, 0.55, 0.52, 0xFF000095), C(0.275, 0.26, 0.12, White), C(0.275, 0.26, 0.085, 0xFF000095), C(0.275, 0.26, 0.07, White)],
        "HK" => [R(0, 0, 1, 1, 0xFFDE2910), C(0.5, 0.5, 0.2, White), C(0.5, 0.5, 0.07, 0xFFDE2910)],
        "SG" => [R(0, 0, 1, 0.5, 0xFFEF3340), R(0, 0.5, 1, 0.5, White), C(0.3, 0.26, 0.15, White), C(0.36, 0.26, 0.14, 0xFFEF3340), Star(0.5, 0.18, 0.04, White), Star(0.58, 0.26, 0.04, White), Star(0.54, 0.36, 0.04, White), Star(0.44, 0.36, 0.04, White), Star(0.42, 0.26, 0.04, White)],
        "TR" => [R(0, 0, 1, 1, 0xFFE30A17), C(0.42, 0.5, 0.25, White), C(0.48, 0.5, 0.2, 0xFFE30A17), Star(0.68, 0.5, 0.11, White)],
        "PK" => [R(0, 0, 1, 1, 0xFF01411C), R(0, 0, 0.25, 1, White), C(0.6, 0.5, 0.24, White), C(0.66, 0.44, 0.21, 0xFF01411C), Star(0.68, 0.4, 0.08, White)],
        "AE" => [R(0, 0, 1, 1 / 3.0, 0xFF00732F), R(0, 1 / 3.0, 1, 1 / 3.0, White), R(0, 2 / 3.0, 1, 1 / 3.0, Black), R(0, 0, 0.3, 1, 0xFFFF0000)],
        "QA" => [R(0, 0, 1, 1, 0xFF8A1538), P(White, 0, 0, 0.36, 0, 0.44, 0.06, 0.36, 0.12, 0.44, 0.18, 0.36, 0.24, 0.44, 0.3, 0.36, 0.36, 0.44, 0.42, 0.36, 0.48, 0.44, 0.54, 0.36, 0.6, 0.44, 0.66, 0.36, 0.72, 0.44, 0.78, 0.36, 0.84, 0.44, 0.9, 0.36, 1, 0, 1)],
        "SA" => [R(0, 0, 1, 1, 0xFF006C35), R(0.22, 0.6, 0.56, 0.035, White), R(0.25, 0.38, 0.5, 0.12, 0x55FFFFFF)],
        "IL" => [R(0, 0, 1, 1, White), R(0, 0.14, 1, 0.12, 0xFF0038B8), R(0, 0.74, 1, 0.12, 0xFF0038B8), P(0xFF0038B8, 0.5, 0.33, 0.65, 0.59, 0.35, 0.59), P(0xFF0038B8, 0.5, 0.67, 0.35, 0.41, 0.65, 0.41), P(White, 0.5, 0.38, 0.6, 0.555, 0.4, 0.555), P(White, 0.5, 0.62, 0.4, 0.445, 0.6, 0.445)],
        "GR" => [.. Enumerable.Range(0, 9).Select(i => R(0, i / 9.0, 1, 1 / 9.0, i % 2 == 0 ? 0xFF0D5EAF : White)), R(0, 0, 0.5, 5 / 9.0, 0xFF0D5EAF), R(0.2, 0, 0.1, 5 / 9.0, White), R(0, 2 / 9.0, 0.5, 1 / 9.0, White)],
        "SE" => Nordic(0xFF006AA7, 0xFFFECC00),
        "FI" => Nordic(White, 0xFF003580),
        "DK" => Nordic(0xFFC8102E, White),
        "NO" => Nordic(0xFFBA0C2F, White, 0xFF00205B),
        "IS" => Nordic(0xFF02529C, White, 0xFFDC1E35),
        "CH" => [R(0, 0, 1, 1, 0xFFDA291C), R(0.42, 0.2, 0.16, 0.6, White), R(0.2, 0.42, 0.6, 0.16, White)],
        "GE" => [R(0, 0, 1, 1, White), R(0.43, 0, 0.14, 1, 0xFFFF0000), R(0, 0.43, 1, 0.14, 0xFFFF0000), C(0.22, 0.22, 0.06, 0xFFFF0000), C(0.78, 0.22, 0.06, 0xFFFF0000), C(0.22, 0.78, 0.06, 0xFFFF0000), C(0.78, 0.78, 0.06, 0xFFFF0000)],
        "CA" => [R(0, 0, 1, 1, White), R(0, 0, 0.24, 1, 0xFFD52B1E), R(0.76, 0, 0.24, 1, 0xFFD52B1E), P(0xFFD52B1E, 0.5, 0.22, 0.56, 0.36, 0.64, 0.32, 0.6, 0.5, 0.7, 0.44, 0.68, 0.54, 0.62, 0.6, 0.53, 0.62, 0.53, 0.76, 0.47, 0.76, 0.47, 0.62, 0.38, 0.6, 0.32, 0.54, 0.3, 0.44, 0.4, 0.5, 0.36, 0.32, 0.44, 0.36)],
        "MX" => [.. V(0xFF006847, White, 0xFFCE1126), C(0.5, 0.5, 0.09, 0xFF8C6A3F)],
        "BR" => [R(0, 0, 1, 1, 0xFF009C3B), P(0xFFFFDF00, 0.5, 0.14, 0.92, 0.5, 0.5, 0.86, 0.08, 0.5), C(0.5, 0.5, 0.2, 0xFF002776), R(0.31, 0.48, 0.38, 0.04, White)],
        "AR" => [.. H(0xFF74ACDF, White, 0xFF74ACDF), C(0.5, 0.5, 0.08, 0xFFF6B40E)],
        "CL" => [R(0, 0, 1, 0.5, White), R(0, 0.5, 1, 0.5, 0xFFD52B1E), R(0, 0, 0.42, 0.5, 0xFF0039A6), Star(0.21, 0.26, 0.11, White)],
        "TH" => [R(0, 0, 1, 1, 0xFFA51931), R(0, 1 / 6.0, 1, 4 / 6.0, 0xFFF4F5F8), R(0, 2 / 6.0, 1, 2 / 6.0, 0xFF2D2A4A)],
        "KZ" => [R(0, 0, 1, 1, 0xFF00AFCA), C(0.55, 0.45, 0.16, 0xFFFEC50C)],
        "EG" => [.. H(0xFFCE1126, White, Black), C(0.5, 0.5, 0.07, 0xFFC09300)],
        "NG" => V(0xFF008751, White, 0xFF008751),
        "ZA" => [R(0, 0, 1, 0.5, 0xFFE03C31), R(0, 0.5, 1, 0.5, 0xFF001489), P(White, 0, 0, 0.2, 0, 0.55, 0.38, 1, 0.38, 1, 0.62, 0.55, 0.62, 0.2, 1, 0, 1), P(0xFF007749, 0, 0.06, 0.12, 0.06, 0.5, 0.44, 1, 0.44, 1, 0.56, 0.5, 0.56, 0.12, 0.94, 0, 0.94), P(Black, 0, 0.16, 0.38, 0.5, 0, 0.84)],
        "AU" => [R(0, 0, 1, 1, 0xFF012169), .. Union(0, 0, 0.55, 0.5), Star(0.28, 0.76, 0.12, White), Star(0.76, 0.3, 0.06, White), Star(0.66, 0.5, 0.06, White), Star(0.86, 0.46, 0.06, White), Star(0.76, 0.76, 0.06, White)],
        "NZ" => [R(0, 0, 1, 1, 0xFF012169), .. Union(0, 0, 0.55, 0.5), Star(0.74, 0.3, 0.07, 0xFFC8102E), Star(0.64, 0.5, 0.07, 0xFFC8102E), Star(0.84, 0.46, 0.06, 0xFFC8102E), Star(0.74, 0.76, 0.08, 0xFFC8102E)],
        "PT" => [R(0, 0, 0.42, 1, 0xFF046A38), R(0.42, 0, 0.58, 1, 0xFFDA291C), C(0.42, 0.5, 0.17, 0xFFFFE900), C(0.42, 0.5, 0.11, 0xFFDA291C), C(0.42, 0.5, 0.08, White)],
        "ES" => [R(0, 0, 1, 1, 0xFFAA151B), R(0, 0.25, 1, 0.5, 0xFFF1BF00), R(0.26, 0.4, 0.14, 0.2, 0xFFAA151B)],
        _ => null,
    };

    /// <summary>Two-letter label for countries without a drawn flag.</summary>
    public static string Initials(string? countryCode) => countryCode is { Length: 2 } ? countryCode.ToUpperInvariant() : "?";

    private static FlagShape R(double x, double y, double w, double h, uint color) => new(FlagShapeKind.Rect, color, [x, y, w, h]);

    private static FlagShape C(double cx, double cy, double r, uint color) => new(FlagShapeKind.Circle, color, [cx, cy, r]);

    private static FlagShape P(uint color, params double[] points) => new(FlagShapeKind.Polygon, color, points);

    private static FlagShape[] H(params uint[] colors) =>
        colors.Select((c, i) => R(0, (double)i / colors.Length, 1, 1.0 / colors.Length, c)).ToArray();

    private static FlagShape[] V(params uint[] colors) =>
        colors.Select((c, i) => R((double)i / colors.Length, 0, 1.0 / colors.Length, 1, c)).ToArray();

    /// <summary>Nordic cross, the vertical bar left of centre; an optional inner cross (Norway, Iceland).</summary>
    private static FlagShape[] Nordic(uint field, uint cross, uint? inner = null)
    {
        var list = new List<FlagShape> { R(0, 0, 1, 1, field), R(0.3, 0, 0.18, 1, cross), R(0, 0.41, 1, 0.18, cross) };
        if (inner is { } i)
        {
            list.Add(R(0.34, 0, 0.1, 1, i));
            list.Add(R(0, 0.45, 1, 0.1, i));
        }

        return [.. list];
    }

    /// <summary>A five-pointed star (polygon of ten points).</summary>
    private static FlagShape Star(double cx, double cy, double r, uint color)
    {
        var pts = new double[20];
        for (var k = 0; k < 10; k++)
        {
            var radius = k % 2 == 0 ? r : r * 0.4;
            var angle = (-Math.PI / 2) + (k * Math.PI / 5);
            pts[2 * k] = cx + (radius * Math.Cos(angle));
            pts[(2 * k) + 1] = cy + (radius * Math.Sin(angle));
        }

        return new FlagShape(FlagShapeKind.Polygon, color, pts);
    }

    /// <summary>The Union Jack in a box (United Kingdom; the canton of Australia and New Zealand).</summary>
    private static FlagShape[] Union(double x, double y, double w, double h)
    {
        const uint Navy = 0xFF012169;
        const uint Cross = 0xFFC8102E;
        double X(double u) => x + (u * w);
        double Y(double v) => y + (v * h);
        FlagShape Band(double t, uint c) =>
            P(c, X(0), Y(0), X(t), Y(0), X(1), Y(1 - t), X(1), Y(1), X(1 - t), Y(1), X(0), Y(t));
        FlagShape Band2(double t, uint c) =>
            P(c, X(1), Y(0), X(1), Y(t), X(t), Y(1), X(0), Y(1), X(0), Y(1 - t), X(1 - t), Y(0));
        return
        [
            R(x, y, w, h, Navy),
            Band(0.12, White), Band2(0.12, White),
            Band(0.05, Cross), Band2(0.05, Cross),
            R(X(0.4), y, w * 0.2, h, White), R(x, Y(0.37), w, h * 0.26, White),
            R(X(0.44), y, w * 0.12, h, Cross), R(x, Y(0.42), w, h * 0.16, Cross),
        ];
    }

    private static FlagShape[] Gb() => Union(0, 0, 1, 1);

    /// <summary>Stars and stripes, composed for a circle: the canton fills the upper left.</summary>
    private static FlagShape[] Us()
    {
        var list = new List<FlagShape>();
        for (var i = 0; i < 13; i++)
        {
            list.Add(R(0, i / 13.0, 1, 1 / 13.0, i % 2 == 0 ? 0xFFB22234 : White));
        }

        list.Add(R(0, 0, 0.5, 7 / 13.0, 0xFF3C3B6E));
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                list.Add(C(0.07 + (col * 0.115) + (row % 2 * 0.055), 0.07 + (row * 0.12), 0.022, White));
            }
        }

        return [.. list];
    }
}
