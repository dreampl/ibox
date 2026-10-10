namespace JackiVpn.Domain.Visuals;

/// <summary>Icons the dots can assemble into (24 × 24 outline paths in the style of Lucide, ISC licence).</summary>
public enum MorphIcon
{
    Link = 0,
    Unlink,
    Gamepad,
    Leaf,
    Play,
    Monitor,
}

public static class MorphIcons
{
    /// <summary>Stroke outline of an icon in a 24 × 24 box (SVG path syntax; WPF/Avalonia/Android parse it as is).</summary>
    public static string Path(MorphIcon icon) => icon switch
    {
        MorphIcon.Link => "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
        MorphIcon.Unlink => "M18.84 12.25l1.72-1.71a5 5 0 0 0-.12-7.07 5 5 0 0 0-6.95 0l-1.72 1.71 M5.17 11.75l-1.71 1.71a5 5 0 0 0 .12 7.07 5 5 0 0 0 6.95 0l1.71-1.71 M8 2v3 M2 8h3 M16 19v3 M19 16h3",
        MorphIcon.Gamepad => "M6 11h4 M8 9v4 M15 12h.5 M18 10h.5 M17.32 5H6.68a4 4 0 0 0-3.98 3.59C2.6 9.42 2 14.46 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.41-1.41A2 2 0 0 1 9.83 16h4.34a2 2 0 0 1 1.41.59L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.55-.6-6.58-.69-7.26A4 4 0 0 0 17.32 5z",
        MorphIcon.Leaf => "M11 20A7 7 0 0 1 9.8 6.1C15.5 5 17 4.48 19 2c1 2 2 4.18 2 8 0 5.5-4.78 10-10 10z M2 21c0-3 1.85-5.36 5.08-6C9.5 14.52 12 13 13 12",
        MorphIcon.Play => "M7 4.5v15a1 1 0 0 0 1.5.86l12.5-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5z",
        _ => "M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8 21h8 M12 17v4",
    };

    /// <summary>The icon a traffic mode shows for a while after it starts (null for the normal mode).</summary>
    public static MorphIcon? ForMode(VisualMode mode) => mode switch
    {
        VisualMode.Gaming => MorphIcon.Gamepad,
        VisualMode.Eco => MorphIcon.Leaf,
        VisualMode.Streaming => MorphIcon.Play,
        VisualMode.Work => MorphIcon.Monitor,
        _ => null,
    };

    /// <summary>Colour of a mode's icon: gaming magenta-red, eco green, streaming blue, work white.</summary>
    public static uint ColorOf(VisualMode mode) => mode switch
    {
        VisualMode.Gaming => 0xFFF43F7A,
        VisualMode.Eco => 0xFF4ADE80,
        VisualMode.Streaming => 0xFF3D9BFF,
        VisualMode.Work => 0xFFF5F7FA,
        _ => EyeMark.PupilBlue,
    };
}

/// <summary>
/// Thinking-orb dots that assemble into an icon and dissolve back: every dot starts on a slowly turning sphere (the
/// orb), then flies — each a little after the previous one — to its place on the icon's outline; held, the icon
/// shimmers softly. Renderers sample the outline (<see cref="SetOutline"/>, points in 0…1) and draw the dots.
/// </summary>
public sealed class ParticleMorph
{
    private readonly double[] _sx;
    private readonly double[] _sy;
    private readonly double[] _sz;
    private readonly double[] _phase;
    private double[] _tx = [];
    private double[] _ty = [];

    public ParticleMorph(int count, int seed = 7)
    {
        Count = count;
        _sx = new double[count];
        _sy = new double[count];
        _sz = new double[count];
        _phase = new double[count];
        var random = new Random(seed);
        // Fibonacci sphere: evenly spread starting points.
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < count; i++)
        {
            var y = 1 - (2 * (i + 0.5) / count);
            var r = Math.Sqrt(1 - (y * y));
            var a = golden * i;
            (_sx[i], _sy[i], _sz[i]) = (Math.Cos(a) * r, y, Math.Sin(a) * r);
            _phase[i] = random.NextDouble() * Math.PI * 2;
        }

        X = new double[count];
        Y = new double[count];
        Radius = new double[count];
        Alpha = new double[count];
    }

    public int Count { get; }

    /// <summary>Output of <see cref="Compute"/>: dot centres in pixels, radii and opacity.</summary>
    public double[] X { get; }

    public double[] Y { get; }

    public double[] Radius { get; }

    public double[] Alpha { get; }

    /// <summary>Target points on the icon outline (0…1 in the icon box), as many as there are dots.</summary>
    public void SetOutline(IReadOnlyList<(double X, double Y)> points)
    {
        _tx = new double[Count];
        _ty = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            var p = points.Count == 0 ? (0.5, 0.5) : points[(int)((long)i * points.Count / Count)];
            (_tx[i], _ty[i]) = p;
        }
    }

    /// <summary>
    /// Positions for <paramref name="progress"/> (0 = orb, 1 = icon) at time <paramref name="time"/> (seconds) in a
    /// square of <paramref name="size"/> pixels.
    /// </summary>
    public void Compute(double time, double progress, double size)
    {
        var spin = time * 0.9;
        var (cs, sn) = (Math.Cos(spin), Math.Sin(spin));
        var tilt = 0.35;
        var (ct, st) = (Math.Cos(tilt), Math.Sin(tilt));
        var dotOrb = Math.Max(0.6, size * 0.022);
        var dotIcon = Math.Max(0.7, size * 0.03);
        var hasOutline = _tx.Length == Count;
        for (var i = 0; i < Count; i++)
        {
            // The orb: rotate about the vertical axis, tilt towards the viewer, project.
            var x = (_sx[i] * cs) + (_sz[i] * sn);
            var z = (-_sx[i] * sn) + (_sz[i] * cs);
            var y = (_sy[i] * ct) - (z * st);
            z = (_sy[i] * st) + (z * ct);
            var ox = 0.5 + (x * 0.4);
            var oy = 0.5 + (y * 0.4);
            var depth = (z + 1) / 2;

            // Each dot leaves a little after the previous one (a wave through the orb).
            var delay = (double)i / Count * 0.35;
            var p = Math.Clamp((progress - delay) / 0.65, 0, 1);
            var e = p < 0.5 ? 4 * p * p * p : 1 - (Math.Pow((-2 * p) + 2, 3) / 2);
            var tx = hasOutline ? _tx[i] : ox;
            var ty = hasOutline ? _ty[i] : oy;
            // A soft arc on the way (not a straight line).
            var lift = Math.Sin(e * Math.PI) * 0.08;
            X[i] = ((ox + ((tx - ox) * e)) + (lift * Math.Cos(_phase[i]))) * size;
            Y[i] = ((oy + ((ty - oy) * e)) + (lift * Math.Sin(_phase[i]))) * size;

            var shimmer = 0.82 + (0.18 * Math.Sin((time * 2.4) + _phase[i]));
            Alpha[i] = ((0.25 + (0.75 * depth)) * (1 - e)) + (shimmer * e);
            Radius[i] = (dotOrb * (0.6 + (0.6 * depth)) * (1 - e)) + (dotIcon * e);
        }
    }
}
