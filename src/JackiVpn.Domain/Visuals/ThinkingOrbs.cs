// C# port of thinking-orbs (https://github.com/Jakubantalik/thinking-orbs), MIT License,
// Copyright (c) Jakub Antalik. Geometry transcribed from src/engine/*.ts and src/presets.ts:
// dotted, depth-shaded, z-sorted 3D orbs drawn with plain circle fills. Frames are computed into
// reusable buffers (no per-frame allocation), so many orbs can run at 60 fps on a phone.
namespace JackiVpn.Domain.Visuals;

/// <summary>The nine orb animations.</summary>
public enum OrbState
{
    /// <summary>Particles on tilted orbits.</summary>
    Working = 0,

    /// <summary>A scan meridian sweeps a dotted globe.</summary>
    Searching,

    /// <summary>Bands scramble, then click back solved.</summary>
    Solving,

    /// <summary>A waveform rolls through the rings.</summary>
    Listening,

    /// <summary>A constellation wires itself.</summary>
    Connecting,

    /// <summary>Three strands plait around the sphere.</summary>
    Weaving,

    /// <summary>An undulating multi-band sash.</summary>
    Composing,

    /// <summary>A ring slowly morphing.</summary>
    Breathing,

    /// <summary>Dotted outline: circle → triangle → square.</summary>
    Shaping,
}

/// <summary>One dot of a frame (ink 0 = darkest on paper; mirrored on dark backgrounds).</summary>
public struct OrbDot
{
    public double X;
    public double Y;
    public double Z;
    public double R;
    public double White;
    public double A;
}

/// <summary>An edge of the "connecting" constellation.</summary>
public struct OrbLine
{
    public double X1;
    public double Y1;
    public double X2;
    public double Y2;
    public double White;
    public double A;
    public double W;
}

/// <summary>A finished draw list: lines first, then dots in this order (already z-sorted and clamped).</summary>
public sealed class OrbFrame
{
    internal OrbDot[] DotBuffer = new OrbDot[256];
    internal OrbLine[] LineBuffer = new OrbLine[64];

    public int DotCount { get; internal set; }
    public int LineCount { get; internal set; }

    public ReadOnlySpan<OrbDot> Dots => DotBuffer.AsSpan(0, DotCount);
    public ReadOnlySpan<OrbLine> Lines => LineBuffer.AsSpan(0, LineCount);

    internal void Clear()
    {
        DotCount = 0;
        LineCount = 0;
    }

    internal void Add(double x, double y, double z, double r, double white, double a = 1)
    {
        if (DotCount == DotBuffer.Length)
        {
            Array.Resize(ref DotBuffer, DotBuffer.Length * 2);
        }

        DotBuffer[DotCount++] = new OrbDot { X = x, Y = y, Z = z, R = r, White = white, A = a };
    }

    internal void AddLine(double x1, double y1, double x2, double y2, double white, double a, double w)
    {
        if (LineCount == LineBuffer.Length)
        {
            Array.Resize(ref LineBuffer, LineBuffer.Length * 2);
        }

        LineBuffer[LineCount++] = new OrbLine { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, White = white, A = a, W = w };
    }

    private static readonly Comparison<OrbDot> ByDepth = static (a, b) => a.Z.CompareTo(b.Z);

    /// <summary>Drop invisible marks, clamp radii to the floor, z-sort far → near (stable, like the original).</summary>
    internal void FinalizeFrame(double rMin)
    {
        var n = 0;
        for (var i = 0; i < DotCount; i++)
        {
            var d = DotBuffer[i];
            if (d.A < 0.02)
            {
                continue;
            }

            d.R = Math.Max(rMin, d.R);
            DotBuffer[n++] = d;
        }

        DotCount = n;
        StableSort(DotBuffer, n);
        var m = 0;
        for (var i = 0; i < LineCount; i++)
        {
            if (LineBuffer[i].A >= 0.02)
            {
                LineBuffer[m++] = LineBuffer[i];
            }
        }

        LineCount = m;
    }

    private OrbDot[] _sortTemp = new OrbDot[256];

    /// <summary>Insertion-merge stable sort (Array.Sort is unstable; equal depths must keep emission order).</summary>
    private void StableSort(OrbDot[] a, int n)
    {
        if (_sortTemp.Length < n)
        {
            _sortTemp = new OrbDot[a.Length];
        }

        var tmp = _sortTemp;
        const int run = 24;
        for (var lo = 0; lo < n; lo += run)
        {
            var hi = Math.Min(n, lo + run);
            for (var i = lo + 1; i < hi; i++)
            {
                var v = a[i];
                var j = i - 1;
                while (j >= lo && a[j].Z > v.Z)
                {
                    a[j + 1] = a[j];
                    j--;
                }

                a[j + 1] = v;
            }
        }

        for (var width = run; width < n; width *= 2)
        {
            for (var lo = 0; lo < n - width; lo += 2 * width)
            {
                var mid = lo + width;
                var hi = Math.Min(n, lo + 2 * width);
                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi)
                {
                    tmp[k++] = a[j].Z < a[i].Z ? a[j++] : a[i++];
                }

                while (i < mid)
                {
                    tmp[k++] = a[i++];
                }

                while (j < hi)
                {
                    tmp[k++] = a[j++];
                }

                Array.Copy(tmp, lo, a, lo, hi - lo);
            }
        }
    }
}

/// <summary>Resolved tuning of one (state, size) pair: mode options and the clock speed.</summary>
public sealed class OrbPreset
{
    private readonly Dictionary<string, double> _o;

    internal OrbPreset(OrbState state, int size, double speed, Dictionary<string, double> opts)
    {
        State = state;
        Size = size;
        Speed = speed;
        _o = opts;
    }

    public OrbState State { get; }

    /// <summary>Design size (64 = avatar scale, 20 = inline text scale).</summary>
    public int Size { get; }

    public double Speed { get; }

    internal double O(string key, double fallback) => _o.TryGetValue(key, out var v) ? v : fallback;

    internal bool Has(string key) => _o.TryGetValue(key, out var v) && v != 0;
}

/// <summary>The geometry engine: one call computes one frame for (state, size, t).</summary>
public static class ThinkingOrbs
{
    public static string Label(OrbState state) => state switch
    {
        OrbState.Working => "Working…",
        OrbState.Searching => "Searching…",
        OrbState.Solving => "Solving…",
        OrbState.Listening => "Listening…",
        OrbState.Connecting => "Connecting…",
        OrbState.Weaving => "Weaving…",
        OrbState.Composing => "Composing…",
        OrbState.Breathing => "Thinking…",
        _ => "Shaping…",
    };

    /// <summary>Time used for the static frame shown with reduced motion.</summary>
    public const double StaticTime = 0.6;

    // ---------------------------------------------------------------- presets

    private static readonly Dictionary<(OrbState, int), OrbPreset> Cache = [];
    private static readonly Lock CacheGate = new();

    /// <summary>The preset for a size: 20 below 40 px, otherwise 64 (separate designs, not a scale factor).</summary>
    public static OrbPreset Resolve(OrbState state, double pixelSize)
    {
        var size = pixelSize < 40 ? 20 : 64;
        lock (CacheGate)
        {
            if (Cache.TryGetValue((state, size), out var hit))
            {
                return hit;
            }

            var (speed, count, sz, extra) = Preset(state, size);
            var opts = new Dictionary<string, double>(BaseProfile(state), StringComparer.Ordinal);
            if (count != 1)
            {
                ScaleCounts(opts, count);
            }

            if (sz != 1)
            {
                ScaleRadii(opts, sz);
            }

            foreach (var (k, v) in extra)
            {
                opts[k] = v;
            }

            var resolved = new OrbPreset(state, size, speed, opts);
            Cache[(state, size)] = resolved;
            return resolved;
        }
    }

    private static (double Speed, double Count, double Size, (string, double)[] Extra) Preset(OrbState state, int size)
    {
        var small = size == 20;
        return state switch
        {
            OrbState.Working => small ? (3.9, 0.238, 2.4, []) : (1.885, 1, 1, []),
            OrbState.Searching => small ? (2.665, 0.105, 1.75, [("scanMul", 4.335), ("dimBase", 0.45)]) : (2.015, 0.42, 1.15, [("scanMul", 4.08), ("dimBase", 0.45)]),
            OrbState.Solving => small ? (1.95, 0.088, 1.9, []) : (1.82, 0.35, 1.05, []),
            OrbState.Listening => small ? (3.998, 0.105, 1.6, []) : (4.388, 0.341, 1, []),
            OrbState.Connecting => small ? (6.63, 0.25, 1.52, []) : (3.315, 1.35, 0.95, []),
            OrbState.Weaving => small ? (2.75, 0.1125, 1.36, []) : (1.625, 0.5, 1, []),
            OrbState.Composing => small ? (3.12, 0.051, 1.073, [("spin", 0), ("bandMul", 4.94), ("wobMul", 1)]) : (2.34, 0.25, 0.85, [("spin", 0), ("bandMul", 3.9), ("wobMul", 1)]),
            OrbState.Breathing => small ? (3.78, 0.028, 1.622, [("spin", 0), ("bandMul", 3.968), ("wobMul", 0.565)]) : (3.24, 0.25, 0.956, [("spin", 0), ("bandMul", 3.627), ("wobMul", 0.368)]),
            _ => small ? (2.08, 0.53, 1.011, [("spread", 1.45)]) : (2.405, 0.702, 0.395, [("spread", 1.45)]),
        };
    }

    private static Dictionary<string, double> BaseProfile(OrbState state) => state switch
    {
        OrbState.Searching => new() { ["latRings"] = 17, ["lonDensity"] = 44, ["rBase"] = 0.6, ["rDepth"] = 1.7, ["rBoost"] = 1.0, ["inkFar"] = 0.62, ["inkSpan"] = 0.54, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Working => new() { ["orbitN"] = 12, ["ghostN"] = 40, ["ghostR"] = 0.9, ["ghostA"] = 0.5, ["particles"] = 3, ["partR"] = 1.2, ["partRDepth"] = 1.6, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Solving => new() { ["latRings"] = 15, ["lonDensity"] = 40, ["moveCount"] = 14, ["rBase"] = 0.6, ["rDepth"] = 1.7, ["rActive"] = 0.3, ["inkFar"] = 0.62, ["inkSpan"] = 0.54, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Listening => new() { ["rings"] = 15, ["lonDensity"] = 40, ["rBase"] = 0.6, ["rDepth"] = 1.7, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Connecting => new() { ["nodeN"] = 30, ["thr"] = 0.72, ["signals"] = 5, ["nodeR"] = 1.4, ["nodeRDepth"] = 1.8, ["lineW"] = 0.8, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Weaving => new() { ["strandN"] = 52, ["turns"] = 3.0, ["ghostN"] = 150, ["rBase"] = 1.2, ["rDepth"] = 1.8, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Composing => new() { ["lanes"] = 5, ["segs"] = 88, ["ghostN"] = 150, ["rBase"] = 1.1, ["rDepth"] = 1.7, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        OrbState.Breathing => new() { ["lanes"] = 5, ["segs"] = 88, ["ghostN"] = 0, ["faceOn"] = 1, ["rBase"] = 1.1, ["rDepth"] = 1.7, ["rsPow"] = 0.6, ["rMin"] = 0.3 },
        _ => new() { ["rDot"] = 0.021, ["iconD"] = 1, ["rMin"] = 0.25 },
    };

    private static readonly (string, string)[] CountPairs = [("latRings", "lonDensity"), ("rings", "lonDensity"), ("lanes", "segs")];
    private static readonly string[] CountKeys = ["orbitN", "ghostN", "nodeN", "strandN", "signals"];
    private static readonly string[] RadiusKeys = ["rBase", "rDepth", "rActive", "rDot", "ghostR", "partR", "partRDepth", "nodeR", "nodeRDepth"];

    /// <summary>JavaScript Math.round (halves round up).</summary>
    private static double JsRound(double v) => Math.Floor(v + 0.5);

    private static void ScaleCounts(Dictionary<string, double> o, double scale)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var rt = Math.Sqrt(scale);
        foreach (var (a, b) in CountPairs)
        {
            if (o.TryGetValue(a, out var va) && o.TryGetValue(b, out var vb) && !done.Contains(a) && !done.Contains(b))
            {
                o[a] = Math.Max(2, JsRound(va * rt));
                o[b] = Math.Max(2, JsRound(vb * rt));
                done.Add(a);
                done.Add(b);
            }
        }

        foreach (var k in CountKeys)
        {
            // 0 means the mode opted out of that layer (ring has no ghost sphere).
            if (o.TryGetValue(k, out var v) && v != 0 && !done.Contains(k))
            {
                o[k] = Math.Max(1, JsRound(v * scale));
            }
        }

        if (o.TryGetValue("iconD", out var d))
        {
            o["iconD"] = Math.Max(0.02, d * scale);
        }
    }

    private static void ScaleRadii(Dictionary<string, double> o, double scale)
    {
        foreach (var k in RadiusKeys)
        {
            if (o.TryGetValue(k, out var v))
            {
                o[k] = v * scale;
            }
        }

        o["rSizeMul"] = (o.TryGetValue("rSizeMul", out var m) ? m : 1) * scale;
    }

    // ---------------------------------------------------------------- primitives

    private static double Lerp(double a, double b, double f) => a + ((b - a) * f);

    private static double Frac(double x) => x - Math.Floor(x);

    private static double HashD(double a, double b)
    {
        var h = Math.Sin((a * 12.9898) + (b * 78.233)) * 43758.5453;
        return h - Math.Floor(h);
    }

    private static double VNoise(double x, double y)
    {
        var xi = Math.Floor(x);
        var yi = Math.Floor(y);
        var fx = x - xi;
        var fy = y - yi;
        fx = fx * fx * (3 - (2 * fx));
        fy = fy * fy * (3 - (2 * fy));
        var a = HashD(xi, yi);
        var b = HashD(xi + 1, yi);
        var c = HashD(xi, yi + 1);
        var d = HashD(xi + 1, yi + 1);
        return a + ((b - a) * fx) + ((c - a) * fy) + ((a - b - c + d) * fx * fy);
    }

    private static readonly double Golden = Math.PI * (3 - Math.Sqrt(5));

    private static (double X, double Y, double Z) FibDir(int i, int n)
    {
        var y = 1 - (2 * (i + 0.5) / n);
        var rad = Math.Sqrt(1 - (y * y));
        var a = i * Golden;
        return (rad * Math.Cos(a), y, rad * Math.Sin(a));
    }

    private static double AngleDelta(double a, double b) => Math.Atan2(Math.Sin(a - b), Math.Cos(a - b));

    /// <summary>Spin + tilt + orthographic projection.</summary>
    private readonly struct Proj(double yaw, double tilt, double cx, double cy, double scale)
    {
        private readonly double _st = Math.Sin(tilt);
        private readonly double _ct = Math.Cos(tilt);
        private readonly double _sy = Math.Sin(yaw);
        private readonly double _cyw = Math.Cos(yaw);

        public (double X, double Y, double Z) P(double x, double y, double z)
        {
            var x1 = (x * _cyw) + (z * _sy);
            var z1 = (-x * _sy) + (z * _cyw);
            var y1 = (y * _ct) - (z1 * _st);
            var z2 = (y * _st) + (z1 * _ct);
            return (cx + (x1 * scale), cy - (y1 * scale), z2);
        }
    }

    private static double RadiusScale(double size, double pow) => Math.Pow(size / 300, pow);

    // ---------------------------------------------------------------- frame

    /// <summary>
    /// Computes the frame at time <paramref name="t"/> (seconds × <see cref="OrbPreset.Speed"/>) for a square of
    /// <paramref name="size"/> device-independent pixels into <paramref name="frame"/>.
    /// </summary>
    public static void Compute(OrbPreset p, double size, double t, OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(frame);
        frame.Clear();
        switch (p.State)
        {
            case OrbState.Working:
                Orbits(p, size, t, frame);
                break;
            case OrbState.Searching:
                Globe(p, size, t, frame);
                break;
            case OrbState.Solving:
                Rubik(p, size, t, frame);
                break;
            case OrbState.Listening:
                Wave(p, size, t, frame);
                break;
            case OrbState.Connecting:
                Web(p, size, t, frame);
                break;
            case OrbState.Weaving:
                Braid(p, size, t, frame);
                break;
            case OrbState.Composing:
            case OrbState.Breathing:
                Ribbon(p, size, t, frame);
                break;
            default:
                Morph(p, size, t, frame);
                break;
        }

        frame.FinalizeFrame(p.O("rMin", 0.3));
    }

    private static void Orbits(OrbPreset o, double size, double t, OrbFrame f)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.82;
        var pt = new Proj(t * 0.12, 0.3, cx, cy, 1);
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var orbitN = (int)o.O("orbitN", 12);
        var ghostN = (int)o.O("ghostN", 40);
        var particles = (int)o.O("particles", 3);
        var ghostR = o.O("ghostR", 0.9);
        var ghostA = o.O("ghostA", 0.5);
        var partR = o.O("partR", 1.2);
        var partRDepth = o.O("partRDepth", 1.6);
        for (var orb = 0; orb < orbitN; orb++)
        {
            var h1 = HashD(orb, 1.7);
            var h2 = HashD(orb, 5.2);
            var h3 = HashD(orb, 8.9);
            var ro = r * (0.45 + (0.52 * h1));
            var th = h1 * 2 * Math.PI;
            var phi = Math.Acos((2 * h2) - 1);
            var nx = Math.Sin(phi) * Math.Cos(th);
            var ny = Math.Cos(phi);
            var nz = Math.Sin(phi) * Math.Sin(th);
            var ux = -ny;
            var uy = nx;
            const double uz = 0;
            var ul = Math.Max(1e-6, Math.Sqrt((ux * ux) + (uy * uy)));
            ux /= ul;
            uy /= ul;
            var vx = (ny * uz) - (nz * uy);
            var vy = (nz * ux) - (nx * uz);
            var vz = (nx * uy) - (ny * ux);
            var speed = (0.25 + (0.55 * h3)) * (h3 > 0.5 ? 1 : -1);
            for (var k = 0; k < ghostN; k++)
            {
                var a = (double)k / ghostN * 2 * Math.PI;
                var (px, py, z) = pt.P(((ux * Math.Cos(a)) + (vx * Math.Sin(a))) * ro, ((uy * Math.Cos(a)) + (vy * Math.Sin(a))) * ro, ((uz * Math.Cos(a)) + (vz * Math.Sin(a))) * ro);
                var depth = ((z / ro) + 1) / 2;
                f.Add(px, py, z, ghostR * rs, 0.72, ghostA * (0.4 + (0.6 * depth)));
            }

            for (var m = 0; m < particles; m++)
            {
                var a = (t * speed) + ((double)m / particles * 2 * Math.PI) + (h2 * 6);
                var (px, py, z) = pt.P(((ux * Math.Cos(a)) + (vx * Math.Sin(a))) * ro, ((uy * Math.Cos(a)) + (vy * Math.Sin(a))) * ro, ((uz * Math.Cos(a)) + (vz * Math.Sin(a))) * ro);
                var depth = ((z / ro) + 1) / 2;
                f.Add(px, py, z, (partR + (partRDepth * depth)) * rs, 0.3 - (0.22 * depth));
            }
        }
    }

    private static void Globe(OrbPreset o, double size, double t, OrbFrame f)
    {
        const double spin = 0.5;
        var cx = size / 2;
        var cy = size / 2;
        var radius = size / 2 * 0.82;
        var tilt = 0.4 + (0.06 * Math.Sin(t * 0.35));
        var pt = new Proj(t * spin, tilt, cx, cy, radius);
        var scan = t * (spin + ((1.7 - spin) * o.O("scanMul", 1)));
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var dimBase = o.O("dimBase", 1);
        var latRings = (int)o.O("latRings", 17);
        var lonDensity = o.O("lonDensity", 44);
        var rBase = o.O("rBase", 0.6);
        var rDepth = o.O("rDepth", 1.7);
        var rBoost = o.O("rBoost", 1);
        var inkFar = o.O("inkFar", 0.62);
        var inkSpan = o.O("inkSpan", 0.54);
        for (var li = 0; li <= latRings; li++)
        {
            var lat = (-Math.PI / 2) + ((double)li / latRings * Math.PI);
            var cosLat = Math.Cos(lat);
            var sinLat = Math.Sin(lat);
            var lonCount = (int)Math.Max(1, JsRound(Math.Abs(cosLat) * lonDensity));
            for (var lj = 0; lj < lonCount; lj++)
            {
                var lon = (double)lj / lonCount * 2 * Math.PI;
                var (px, py, z) = pt.P(cosLat * Math.Cos(lon), sinLat, cosLat * Math.Sin(lon));
                var depth = (z + 1) / 2;
                var d = AngleDelta(lon + (t * spin), scan);
                var boost = Math.Exp(-(d * d) / 0.18) * Math.Max(0, z);
                f.Add(px, py, z, (rBase + (rDepth * depth) + (rBoost * boost)) * rs, inkFar - (inkSpan * depth), dimBase + ((1 - dimBase) * Math.Min(1, boost)));
            }
        }
    }

    private static void Rubik(OrbPreset o, double size, double t, OrbFrame f)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.82;
        var pt = new Proj(t * 0.55, 0.35 + (0.1 * Math.Sin(t * 0.9)), cx, cy, r);
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var moveCount = (int)o.O("moveCount", 14);
        Span<int> axis = stackalloc int[moveCount];
        Span<double> lo = stackalloc double[moveCount];
        Span<double> ang = stackalloc double[moveCount];
        Span<double> amount = stackalloc double[moveCount];
        for (var i = 0; i < moveCount; i++)
        {
            axis[i] = (int)Math.Min(2, Math.Floor(HashD(i, 2.3) * 3));
            lo[i] = -1.0 + (0.5 * Math.Min(3, Math.Floor(HashD(i, 5.9) * 4)));
            ang[i] = (HashD(i, 7.7) < 0.5 ? 1 : -1) * Math.PI / 2;
        }

        // Solve cycle: eased moves scramble, then replay in reverse so everything clicks back solved.
        const double slotDur = 0.42;
        const double rest = 1.2;
        var cyc = (2 * moveCount * slotDur) + rest;
        var tc = t % cyc;
        var active = -1;
        if (tc < 2 * moveCount * slotDur)
        {
            var slot = (int)Math.Floor(tc / slotDur);
            var pr = (tc - (slot * slotDur)) / slotDur;
            var cl = Math.Min(1, pr / 0.7);
            var ep = 1 - Math.Pow(1 - cl, 3);
            if (slot < moveCount)
            {
                for (var i = 0; i < slot; i++)
                {
                    amount[i] = 1;
                }

                amount[slot] = ep;
                active = slot;
            }
            else
            {
                var u = (2 * moveCount) - 1 - slot;
                for (var i = 0; i < u; i++)
                {
                    amount[i] = 1;
                }

                amount[u] = 1 - ep;
                active = u;
            }
        }

        var latRings = (int)o.O("latRings", 15);
        var lonDensity = o.O("lonDensity", 40);
        var rBase = o.O("rBase", 0.6);
        var rDepth = o.O("rDepth", 1.7);
        var rActive = o.O("rActive", 0.3);
        var inkFar = o.O("inkFar", 0.62);
        var inkSpan = o.O("inkSpan", 0.54);
        for (var li = 0; li <= latRings; li++)
        {
            var lat = (-Math.PI / 2) + ((double)li / latRings * Math.PI);
            var cosLat = Math.Cos(lat);
            var sinLat = Math.Sin(lat);
            var lonCount = (int)Math.Max(1, JsRound(Math.Abs(cosLat) * lonDensity));
            for (var lj = 0; lj < lonCount; lj++)
            {
                var lon = (double)lj / lonCount * 2 * Math.PI;
                double x = cosLat * Math.Cos(lon), y = sinLat, z = cosLat * Math.Sin(lon);
                var inActive = false;
                for (var i = 0; i < moveCount; i++)
                {
                    if (amount[i] <= 0)
                    {
                        continue;
                    }

                    var coord = axis[i] == 0 ? x : axis[i] == 1 ? y : z;
                    if (coord < lo[i] || coord >= lo[i] + 0.5)
                    {
                        continue;
                    }

                    if (i == active)
                    {
                        inActive = true;
                    }

                    var a = ang[i] * amount[i];
                    var ca = Math.Cos(a);
                    var sa = Math.Sin(a);
                    if (axis[i] == 0)
                    {
                        var y2 = (y * ca) - (z * sa);
                        z = (y * sa) + (z * ca);
                        y = y2;
                    }
                    else if (axis[i] == 1)
                    {
                        var x2 = (x * ca) + (z * sa);
                        z = (-x * sa) + (z * ca);
                        x = x2;
                    }
                    else
                    {
                        var x2 = (x * ca) - (y * sa);
                        y = (x * sa) + (y * ca);
                        x = x2;
                    }
                }

                var (px, py, zr) = pt.P(x, y, z);
                var depth = (zr + 1) / 2;
                f.Add(px, py, zr, (rBase + (rDepth * depth) + (inActive ? rActive : 0)) * rs, inkFar - (inkSpan * depth) - (inActive ? 0.14 : 0));
            }
        }
    }

    private static void Wave(OrbPreset o, double size, double t, OrbFrame f)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.874;
        var pt = new Proj(t * 0.18, 0.38, cx, cy, 1);
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var rings = (int)o.O("rings", 15);
        var lonDensity = o.O("lonDensity", 40);
        var rBase = o.O("rBase", 0.6);
        var rDepth = o.O("rDepth", 1.7);
        for (var ri = 0; ri <= rings; ri++)
        {
            var lat = (-Math.PI / 2) + ((double)ri / rings * Math.PI);
            var cosLat = Math.Cos(lat);
            var sinLat = Math.Sin(lat);
            var w = (0.62 * Math.Sin((t * 2.1) - (ri * 0.52))) + (0.38 * Math.Sin((t * 1.27) + (ri * 0.83)));
            var rr = r * (0.88 + (0.105 * w));
            var lonCount = (int)Math.Max(1, JsRound(Math.Abs(cosLat) * lonDensity));
            var crest = Math.Max(0, w);
            for (var lj = 0; lj < lonCount; lj++)
            {
                var lon = (double)lj / lonCount * 2 * Math.PI;
                var (px, py, z) = pt.P(cosLat * Math.Cos(lon) * rr, sinLat * rr, cosLat * Math.Sin(lon) * rr);
                var depth = ((z / r) + 1) / 2;
                f.Add(px, py, z, (rBase + (rDepth * depth)) * (1 + (0.4 * crest)) * rs, 0.66 - (0.56 * depth) - (0.1 * crest));
            }
        }
    }

    [ThreadStatic]
    private static (double X, double Y, double Z)[]? _nodes;

    private static void Web(OrbPreset o, double size, double t, OrbFrame f)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.8 * o.O("spread", 1);
        var pt = new Proj(t * 0.12, 0.32, cx, cy, r);
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var nodeN = (int)o.O("nodeN", 30);
        var thr = o.O("thr", 0.72);
        var nodeR = o.O("nodeR", 1.4);
        var nodeRDepth = o.O("nodeRDepth", 1.8);
        var lineW = o.O("lineW", 0.8);
        if (_nodes is null || _nodes.Length < nodeN)
        {
            _nodes = new (double, double, double)[nodeN];
        }

        var nodes = _nodes;
        for (var i = 0; i < nodeN; i++)
        {
            var d = FibDir(i, nodeN);
            var x = d.X + (0.3 * (VNoise((i * 0.31) + 9, t * 0.24) - 0.5) * 2);
            var y = d.Y + (0.3 * (VNoise((i * 0.53) + 27, t * 0.21) - 0.5) * 2);
            var z = d.Z + (0.3 * (VNoise((i * 0.77) + 55, t * 0.27) - 0.5) * 2);
            var l = Math.Sqrt((x * x) + (y * y) + (z * z));
            nodes[i] = (x / l, y / l, z / l);
        }

        for (var i = 0; i < nodeN; i++)
        {
            for (var j = i + 1; j < nodeN; j++)
            {
                var dx = nodes[i].X - nodes[j].X;
                var dy = nodes[i].Y - nodes[j].Y;
                var dz = nodes[i].Z - nodes[j].Z;
                var dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                if (dist >= thr)
                {
                    continue;
                }

                var (x1, y1, z1) = pt.P(nodes[i].X, nodes[i].Y, nodes[i].Z);
                var (x2, y2, z2) = pt.P(nodes[j].X, nodes[j].Y, nodes[j].Z);
                var depth = (((z1 + z2) / 2) + 1) / 2;
                f.AddLine(x1, y1, x2, y2, 0.42, (1 - (dist / thr)) * (0.3 + (0.55 * depth)), Math.Max(0.6, lineW * rs));
            }
        }

        for (var i = 0; i < nodeN; i++)
        {
            var (px, py, z) = pt.P(nodes[i].X, nodes[i].Y, nodes[i].Z);
            var depth = (z + 1) / 2;
            var pulse = 1 + (0.25 * Math.Sin((t * 1.4) + (i * 2.7)));
            f.Add(px, py, z, (nodeR + (nodeRDepth * depth)) * pulse * rs, 0.55 - (0.45 * depth));
        }

        var signals = (int)o.O("signals", 5);
        for (var s = 0; s < signals; s++)
        {
            var seg = Math.Floor((t * 0.55) + (s * 7.31));
            var a = (int)Math.Floor(HashD(seg, (s * 3.1) + 1.7) * nodeN);
            var b = (int)Math.Floor(HashD(seg, (s * 5.7) + 4.2) * nodeN);
            if (a == b)
            {
                continue;
            }

            var fr = Frac((t * 0.55) + (s * 7.31));
            var x = Lerp(nodes[a].X, nodes[b].X, fr);
            var y = Lerp(nodes[a].Y, nodes[b].Y, fr);
            var z = Lerp(nodes[a].Z, nodes[b].Z, fr);
            var l = Math.Max(1e-6, Math.Sqrt((x * x) + (y * y) + (z * z)));
            var (px, py, zr) = pt.P(x / l, y / l, z / l);
            var depth = (zr + 1) / 2;
            f.Add(px, py, zr, ((nodeR * 1.5) + (nodeRDepth * depth)) * rs, 0.05, 0.5 + (0.5 * depth));
        }
    }

    private static void Braid(OrbPreset o, double size, double t, OrbFrame f)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.76;
        var pt = new Proj(t * 0.4, 0.3, cx, cy, 1);
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var ghostN = (int)o.O("ghostN", 150);
        for (var i = 0; i < ghostN; i++)
        {
            var d = FibDir(i, ghostN);
            var (px, py, z) = pt.P(d.X * r, d.Y * r, d.Z * r);
            var depth = ((z / r) + 1) / 2;
            f.Add(px, py, z, 0.8 * rs, 0.78, 0.1 + (0.22 * depth));
        }

        var strandN = (int)o.O("strandN", 52);
        var turns = o.O("turns", 3);
        var rBase = o.O("rBase", 1.2);
        var rDepth = o.O("rDepth", 1.8);
        for (var s = 0; s < 3; s++)
        {
            var phase = s / 3.0 * 2 * Math.PI;
            for (var i = 0; i < strandN; i++)
            {
                var u = ((Frac(((double)i / strandN) + (t * 0.045)) * 2) - 1) * 0.96;
                var surf = Math.Sqrt(Math.Max(0, 1 - (u * u)));
                var endFade = Math.Min(1, (1 - Math.Abs(u)) / 0.1);
                var a = (u * Math.PI * turns) + phase;
                var weave = 1 + (0.075 * Math.Sin((u * Math.PI * turns * 2) + (phase * 2) + (t * 0.8)));
                var rr = surf * r * weave;
                var (px, py, zr) = pt.P(Math.Cos(a) * rr, u * r * weave, Math.Sin(a) * rr);
                var depth = ((zr / r) + 1) / 2;
                f.Add(px, py, zr, (rBase + (rDepth * depth)) * rs, 0.55 - (0.45 * depth), endFade * (0.45 + (0.55 * depth)));
            }
        }
    }

    private static void Ribbon(OrbPreset o, double size, double t, OrbFrame f)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.78;
        var spin = o.O("spin", 1);
        const double camTilt = 0.3;
        var pt = new Proj(t * 0.1 * spin, camTilt, cx, cy, 1);
        var rs = RadiusScale(size, o.O("rsPow", 0.6));
        var ghostN = (int)o.O("ghostN", 150);
        for (var i = 0; i < ghostN; i++)
        {
            var d = FibDir(i, ghostN);
            var (px, py, z) = pt.P(d.X * r, d.Y * r, d.Z * r);
            var depth = ((z / r) + 1) / 2;
            f.Add(px, py, z, 0.8 * rs, 0.78, 0.1 + (0.22 * depth));
        }

        var faceOn = o.Has("faceOn");
        var ya = t * 0.24 * spin;
        var ta = faceOn ? -camTilt : 0.55 + (0.3 * Math.Sin(t * 0.18) * spin);
        var ux = Math.Cos(ya);
        const double uy = 0;
        var uz = Math.Sin(ya);
        var vx = -uz * Math.Sin(ta);
        var vy = Math.Cos(ta);
        var vz = ux * Math.Sin(ta);
        var nx = (uy * vz) - (uz * vy);
        var ny = (uz * vx) - (ux * vz);
        var nz = (ux * vy) - (uy * vx);
        var wobMul = o.O("wobMul", 1);
        var wobAmp = 0.23 * wobMul;
        var baseR = faceOn ? r / (1 + (0.85 * wobAmp)) : r;
        var baseLanes = o.O("lanes", 5);
        var segs = (int)o.O("segs", 88);
        var lanes = (int)Math.Max(1, JsRound(baseLanes * o.O("bandMul", 1)));
        var rBase = o.O("rBase", 1.1);
        var rDepth = o.O("rDepth", 1.7);
        for (var w = 0; w < lanes; w++)
        {
            var laneOff = (w - ((lanes - 1) / 2.0)) * 0.075;
            var edge = Math.Abs(w - ((lanes - 1) / 2.0)) / Math.Max(1, (lanes - 1) / 2.0);
            for (var k = 0; k < segs; k++)
            {
                var a = (double)k / segs * 2 * Math.PI;
                var wob = ((0.16 * Math.Sin((a * 3) - (t * 1.7) + (w * 0.22))) + (0.07 * Math.Sin((a * 5) + (t * 1.1)))) * wobMul;
                var radial = faceOn ? 1 + wob : 1;
                var off = faceOn ? laneOff : laneOff + wob;
                var x = (ux * Math.Cos(a)) + (vx * Math.Sin(a)) + (nx * off);
                var y = (uy * Math.Cos(a)) + (vy * Math.Sin(a)) + (ny * off);
                var z = (uz * Math.Cos(a)) + (vz * Math.Sin(a)) + (nz * off);
                var l = Math.Sqrt((x * x) + (y * y) + (z * z));
                var rr = baseR * radial;
                var (px, py, zr) = pt.P(x / l * rr, y / l * rr, z / l * rr);
                var depth = ((zr / r) + 1) / 2;
                f.Add(px, py, zr, (rBase + (rDepth * depth)) * (1 - (0.25 * edge)) * rs, 0.52 - (0.44 * depth) + (0.18 * edge), 0.4 + (0.6 * depth));
            }
        }
    }

    // --- morph: circle → triangle → square, dots laid evenly along the blended outline

    private const double Hold = 1.4;
    private const double MorphTime = 0.9;
    private const double Seg = Hold + MorphTime;
    private const int MorphSamples = 160;

    private static readonly (double X, double Y)[] Triangle = [(0.0, -0.26), (0.24, 0.16), (-0.24, 0.16)];
    private static readonly (double X, double Y)[] Square = [(0, -0.2), (0.2, -0.2), (0.2, 0.2), (-0.2, 0.2), (-0.2, -0.2)];
    private static readonly double[] TriangleLengths = Lengths(Triangle);
    private static readonly double[] SquareLengths = Lengths(Square);

    private static double[] Lengths((double X, double Y)[] verts)
    {
        var l = new double[verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % verts.Length];
            l[i] = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
        }

        return l;
    }

    private static (double X, double Y) PolyAt((double X, double Y)[] verts, double[] lengths, double f)
    {
        var total = 0.0;
        foreach (var l in lengths)
        {
            total += l;
        }

        var target = f * total;
        var i = 0;
        while (target > lengths[i] && i < verts.Length - 1)
        {
            target -= lengths[i];
            i++;
        }

        var a = verts[i];
        var b = verts[(i + 1) % verts.Length];
        var ff = lengths[i] != 0 ? Math.Min(1, target / lengths[i]) : 0;
        return (a.X + ((b.X - a.X) * ff), a.Y + ((b.Y - a.Y) * ff));
    }

    private static (double X, double Y) ShapeAt(int shape, double f)
    {
        switch (shape)
        {
            case 0:
                var a = (-Math.PI / 2) + (f * 2 * Math.PI);
                return (Math.Cos(a) * 0.24, Math.Sin(a) * 0.24);
            case 1:
                return PolyAt(Triangle, TriangleLengths, f);
            default:
                return PolyAt(Square, SquareLengths, f);
        }
    }

    [ThreadStatic]
    private static (double X, double Y)[]? _morphPts;

    [ThreadStatic]
    private static double[]? _morphLen;

    private static void Morph(OrbPreset o, double size, double t, OrbFrame f)
    {
        const int shapes = 3;
        var tc = t % (Seg * shapes);
        var k = (int)Math.Floor(tc / Seg);
        var local = tc - (k * Seg);
        var m = local > Hold ? Smooth((local - Hold) / MorphTime) : 0;
        var sprd = o.O("spread", 1);
        var pts = _morphPts ??= new (double, double)[MorphSamples];
        var len = _morphLen ??= new double[MorphSamples];
        for (var i = 0; i < MorphSamples; i++)
        {
            var fr = (double)i / MorphSamples;
            var a = ShapeAt(k, fr);
            var b = ShapeAt((k + 1) % shapes, fr);
            pts[i] = ((a.X + ((b.X - a.X) * m)) * sprd, (a.Y + ((b.Y - a.Y) * m)) * sprd);
        }

        var total = 0.0;
        for (var i = 0; i < MorphSamples; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % MorphSamples];
            len[i] = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            total += len[i];
        }

        var n = (int)Math.Max(6, JsRound(34 * o.O("iconD", 1)));
        var re = o.O("rDot", 0.021) * 1.35 * sprd;
        var pulse = 1 + (0.02 * Math.Sin(local * 3.1));
        var c2 = size / 2;
        var seg = 0;
        var acc = 0.0;
        for (var k2 = 0; k2 < n; k2++)
        {
            var target = (double)k2 / n * total;
            while (acc + len[seg] < target && seg < MorphSamples - 1)
            {
                acc += len[seg];
                seg++;
            }

            var a = pts[seg];
            var b = pts[(seg + 1) % MorphSamples];
            var fr = len[seg] != 0 ? Math.Min(1, (target - acc) / len[seg]) : 0;
            var x = (a.X + ((b.X - a.X) * fr)) * pulse;
            var y = (a.Y + ((b.Y - a.Y) * fr)) * pulse;
            f.Add(c2 + (x * size), c2 + (y * size), 0, Math.Max(0.35, re * size), 0.1);
        }
    }

    private static double Smooth(double x) => x * x * (3 - (2 * x));

    /// <summary>Gray level 0–255 of an ink value on a dark (mirrored) or light background.</summary>
    public static byte Gray(double white, bool dark)
    {
        var w = Math.Min(1, Math.Max(0, white));
        return (byte)JsRound((dark ? 1 - w : w) * 255);
    }

    /// <summary>The orb that fits a connection state.</summary>
    public static OrbState ForConnection(Connection.ConnectionState state) => state switch
    {
        Connection.ConnectionState.Preparing => OrbState.Working,
        Connection.ConnectionState.Testing => OrbState.Searching,
        Connection.ConnectionState.Connecting => OrbState.Connecting,
        Connection.ConnectionState.Reconnecting or Connection.ConnectionState.Recovering or Connection.ConnectionState.SwitchingServer => OrbState.Solving,
        _ => OrbState.Breathing,
    };
}
