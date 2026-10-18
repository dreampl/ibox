namespace JackiVpn.Domain.Visuals;

/// <summary>
/// Apple-style continuous corners ("squircles"). Apple has not published its formula; this is the closest public
/// approximation, Figma's "corner smoothing" construction (MartinRGB / figma-squircle): a corner of radius r with
/// smoothing s starts p = (1 + s)·r from the vertex, eases in on a cubic Bézier, follows a circular arc spanning
/// 90°·(1 − s) around the diagonal, and eases out on a mirrored Bézier — curvature grows gradually instead of
/// jumping from 0 to 1/r as a plain rounded rectangle does. s ≈ 0.6 matches iOS / macOS closely.
/// </summary>
public static class Squircle
{
    public const double AppleSmoothing = 0.6;

    /// <summary>How far from the vertex the corner begins along each edge.</summary>
    public static double Reach(double radius, double smoothing) => (1 + Math.Clamp(smoothing, 0, 1)) * Math.Max(0, radius);

    /// <summary>
    /// Points of one corner relative to its vertex (0, 0), for a corner whose edges run up (−y) and left (−x):
    /// from (0, −p) on the vertical edge to (−p, 0) on the horizontal one (the bottom-right corner of a shape, y down).
    /// Mirror the x values for a bottom-left corner.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Corner(double radius, double smoothing = AppleSmoothing, int steps = 8)
    {
        var r = Math.Max(0, radius);
        var s = Math.Clamp(smoothing, 0, 1);
        if (r < 0.01)
        {
            return [(0, 0)];
        }

        static double Rad(double deg) => deg * Math.PI / 180;
        var p = (1 + s) * r;
        var arcMeasure = 90 * (1 - s);
        var arcLength = Math.Sin(Rad(arcMeasure / 2)) * r * Math.Sqrt(2);
        var alpha = (90 - arcMeasure) / 2;
        var p3ToP4 = r * Math.Tan(Rad(alpha / 2));
        var beta = 45 * s;
        var c = p3ToP4 * Math.Cos(Rad(beta));
        var d = c * Math.Tan(Rad(beta));
        var b = (p - arcLength - c - d) / 3;
        var a = 2 * b;

        var points = new List<(double, double)>(steps * 3 + 3);

        // Ease-in Bézier down the vertical edge: (0, −p) → (−d, −p + a + b + c).
        var start = (X: 0.0, Y: -p);
        var end1 = (X: -d, Y: -p + a + b + c);
        Bezier(points, start, (0, -p + a), (0, -p + a + b), end1, steps);

        // The circular arc, centred on the corner's diagonal.
        var e = d + arcLength;
        var sum = d + e;
        var k = (sum + Math.Sqrt(Math.Max(0, (sum * sum) - (2 * ((d * d) + (e * e) - (r * r)))))) / 2;
        var center = (X: -k, Y: -k);
        var end2 = (X: end1.X - arcLength, Y: end1.Y + arcLength);
        var a0 = Math.Atan2(end1.Y - center.Y, end1.X - center.X);
        var a1 = Math.Atan2(end2.Y - center.Y, end2.X - center.X);
        for (var i = 1; i <= steps; i++)
        {
            var t = a0 + ((a1 - a0) * i / steps);
            points.Add((center.X + (r * Math.Cos(t)), center.Y + (r * Math.Sin(t))));
        }

        // Ease-out Bézier along the horizontal edge to (−p, 0).
        Bezier(points, end2, (end2.X - c, end2.Y + d), (end2.X - (b + c), end2.Y + d), (-p, 0), steps, skipFirst: true);
        return points;
    }

    private static void Bezier(List<(double, double)> points, (double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, int steps, bool skipFirst = false)
    {
        for (var i = skipFirst ? 1 : 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var u = 1 - t;
            points.Add((
                (u * u * u * p0.X) + (3 * u * u * t * p1.X) + (3 * u * t * t * p2.X) + (t * t * t * p3.X),
                (u * u * u * p0.Y) + (3 * u * u * t * p1.Y) + (3 * u * t * t * p2.Y) + (t * t * t * p3.Y)));
        }
    }
}
