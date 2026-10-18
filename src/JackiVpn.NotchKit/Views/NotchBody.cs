using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The notch's lower edge as an elastic membrane: a row of masses held at rest by springs and tied to their
/// neighbours (a damped wave equation). A pull at one point stretches the edge there and the neighbours follow; a hit
/// sends a ripple along it; let go, it swings back and settles. The top edge is never part of it — the island stays
/// fixed to the top of the screen and only ever stretches downwards from it.
/// </summary>
public sealed class Membrane
{
    private const int Nodes = 40;
    private const double Rest = 260;      // springs back to the rest shape (1/s²)
    private const double Tension = 2600;  // neighbours pull each other along (1/s²): waves travel ~50 nodes/s
    private const double Damping = 7.5;   // 1/s
    private const double SubStep = 1.0 / 240;
    private readonly double[] _y = new double[Nodes];
    private readonly double[] _v = new double[Nodes];
    private readonly double[] _a = new double[Nodes];
    private double _pullU;
    private double _pullTarget;
    private double _pullWidth;
    private double _pullStrength;
    private bool _pulling;

    /// <summary>Downward stretch (px) at <paramref name="u"/> (0 = left end, 1 = right end of the edge).</summary>
    public double At(double u)
    {
        var x = Math.Clamp(u, 0, 1) * (Nodes - 1);
        var i = Math.Min((int)x, Nodes - 2);
        var f = x - i;
        return (_y[i] * (1 - f)) + (_y[i + 1] * f);
    }

    /// <summary>Largest stretch anywhere (px): the window keeps room for it.</summary>
    public double Max
    {
        get
        {
            var m = 0.0;
            foreach (var y in _y)
            {
                m = Math.Max(m, y);
            }

            return m;
        }
    }

    public bool Pulling => _pulling;

    /// <summary>A hit at <paramref name="u"/>: the edge there is set moving (px/s, down is positive).</summary>
    public void Impulse(double u, double velocity, double width = 0.12)
    {
        for (var i = 0; i < Nodes; i++)
        {
            _v[i] += velocity * Weight(i, u, width);
        }
    }

    /// <summary>Holds the edge at <paramref name="u"/> towards a stretch of <paramref name="target"/> px (a soft spring).</summary>
    public void Pull(double u, double target, double width = 0.09, double strength = 900)
    {
        _pulling = true;
        _pullU = Math.Clamp(u, 0, 1);
        _pullTarget = target;
        _pullWidth = width;
        _pullStrength = strength;
    }

    /// <summary>Let go: the edge swings back on its own.</summary>
    public void Release() => _pulling = false;

    public void Reset()
    {
        Array.Clear(_y);
        Array.Clear(_v);
        _pulling = false;
    }

    /// <summary>Advances the simulation; false once the edge is at rest.</summary>
    public bool Step(double dt)
    {
        if (!_pulling && IsAtRest())
        {
            return false;
        }

        for (var t = 0.0; t < dt; t += SubStep)
        {
            var h = Math.Min(SubStep, dt - t);
            for (var i = 0; i < Nodes; i++)
            {
                // Free ends: the corners move with the edge next to them.
                var left = _y[Math.Max(0, i - 1)];
                var right = _y[Math.Min(Nodes - 1, i + 1)];
                var a = (-Rest * _y[i]) + (Tension * (left - (2 * _y[i]) + right)) - (Damping * _v[i]);
                if (_pulling)
                {
                    a += _pullStrength * Weight(i, _pullU, _pullWidth) * (_pullTarget - _y[i]);
                }

                _a[i] = a;
            }

            for (var i = 0; i < Nodes; i++)
            {
                _v[i] += _a[i] * h;
                // The island may dent a little upwards, never off the top edge.
                _y[i] = Math.Clamp(_y[i] + (_v[i] * h), -8, 120);
            }
        }

        if (!_pulling && IsAtRest())
        {
            Reset();
            return false;
        }

        return true;
    }

    private bool IsAtRest()
    {
        for (var i = 0; i < Nodes; i++)
        {
            if (Math.Abs(_y[i]) > 0.08 || Math.Abs(_v[i]) > 0.8)
            {
                return false;
            }
        }

        return true;
    }

    private static double Weight(int i, double u, double width)
    {
        var d = ((double)i / (Nodes - 1)) - u;
        return Math.Exp(-(d * d) / (2 * width * width));
    }
}

/// <summary>
/// The island itself: a black shape hanging from the top edge (with small inward curves where it meets the edge, so
/// it reads as part of it), rounded below, stretched by the <see cref="Membrane"/> and a sideways pull, over a soft
/// shadow drawn from the same outline (no blur effect, no picture — nothing to redo or stretch when it changes size).
/// Hit-testing follows the outline: the shadow never takes the pointer.
/// </summary>
public sealed class NotchBody : FrameworkElement
{
    private const double Ear = 9;
    private const int ShadowLayers = 6;
    private static readonly Brush[] ShadowBrushes = CreateShadowBrushes();
    private Geometry? _outline;

    public NotchBody()
    {
        IsHitTestVisible = true;
        SnapsToDevicePixels = false;
    }

    public Membrane Membrane { get; } = new();

    public double BodyWidth { get; private set; } = 200;

    public double BodyHeight { get; private set; } = 34;

    public double Radius { get; private set; } = 17;

    /// <summary>Sideways pull (px) of the lower part, centred at <see cref="LateralCenter"/> (0…1 of the width).</summary>
    public double Lateral { get; set; }

    public double LateralCenter { get; set; } = 0.5;

    /// <summary>Background of the body (black; a request tints it a little).</summary>
    public Brush Fill { get; set; } = Brushes.Black;

    public void SetSize(double width, double height, double radius)
    {
        BodyWidth = Math.Max(20, width);
        BodyHeight = Math.Max(16, height);
        Radius = Math.Clamp(radius, 0, Math.Min(BodyWidth, BodyHeight) / 2);
        Width = BodyWidth;
        Height = BodyHeight;
        InvalidateVisual();
    }

    /// <summary>Redraw after the membrane or the sideways pull moved.</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>Corner smoothing of the lower corners (0 = plain arc; 0.6 ≈ Apple's continuous corners).</summary>
    public double Smoothing { get; set; } = Domain.Visuals.Squircle.AppleSmoothing;

    /// <summary>Liquid glass: off (black), drawn (gradient, grain, light on the edge) or real (what is behind, blurred).</summary>
    public GlassLevel Glass { get; set; }

    /// <summary>How much glass shows towards the bottom (0…1); the top always stays pure black.</summary>
    public double GlassStrength { get; set; } = 0.7;

    /// <summary>For real glass: the blurred picture of what is behind the island (null = the drawn glass).</summary>
    public Brush? Backdrop { get; set; }

    /// <summary>Small circles that come out of the island's left side (plugin chips).</summary>
    public List<Satellite> Satellites { get; } = [];

    /// <summary>The activity circle and the circles behind it (right side).</summary>
    public ActivityCluster? Cluster { get; set; }

    /// <summary>For real glass: the average colour of what is behind (the tint that keeps text readable comes from it).</summary>
    public Color BackdropAverage { get; set; } = Color.FromRgb(0x30, 0x30, 0x30);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        for (var i = ShadowLayers; i >= 1; i--)
        {
            drawingContext.DrawGeometry(ShadowBrushes[i - 1], null, Outline(i * 2.4, i * 1.3, ears: false));
        }

        foreach (var s in Satellites)
        {
            DrawSatellite(drawingContext, s);
        }

        Cluster?.Draw(drawingContext, BodyWidth, Fill, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _outline = Outline(0, 0, ears: true);
        drawingContext.DrawGeometry(Fill, null, _outline);
        if (Glass != GlassLevel.Off && GlassStrength > 0.01)
        {
            DrawGlass(drawingContext, _outline);
        }
    }

    // ------------------------------------------------------------------ liquid glass

    private static readonly ImageBrush Grain = CreateGrain();

    // Neutral greys (the old blue-grey read as a blue cast under the island).
    private static readonly Brush DrawnGlass = Frozen(new LinearGradientBrush(
        [new GradientStop(Color.FromArgb(0xFF, 0x26, 0x26, 0x28), 0), new GradientStop(Color.FromArgb(0xFF, 0x33, 0x33, 0x36), 0.55), new GradientStop(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x20), 1)],
        new Point(0, 0), new Point(1, 1)));

    private static readonly Pen EdgeLight = Frozen(new Pen(new LinearGradientBrush(
        [new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35), new GradientStop(Color.FromArgb(0x70, 255, 255, 255), 1)],
        new Point(0, 0), new Point(0, 1)), 1.1));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static ImageBrush CreateGrain()
    {
        // A fixed grain (frosted look) made once; the same tile everywhere.
        const int size = 96;
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Almost none: a trace of grain so flat glass does not band, nothing that reads as noise.
            var v = (byte)random.Next(170, 256);
            var a = (byte)random.Next(0, 5);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(v * a / 255);
            pixels[i + 3] = a;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return Frozen(new ImageBrush(bitmap) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute });
    }

    /// <summary>Pure black at the top, glass growing towards the bottom; the edge catches a line of light.</summary>
    private void DrawGlass(DrawingContext dc, Geometry outline)
    {
        var strength = Math.Clamp(GlassStrength, 0, 1);
        var mask = new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.18), new GradientStop(Color.FromArgb((byte)(255 * strength * 0.9), 0, 0, 0), 1)],
            new Point(0, 0), new Point(0, 1));
        dc.PushClip(outline);
        dc.PushOpacityMask(mask);
        var area = new Rect(-Ear, 0, BodyWidth + (2 * Ear), BodyHeight + Math.Max(0, Membrane.Max) + 2);
        if (Glass == GlassLevel.Real && Backdrop is { } backdrop)
        {
            // What is behind, already bent at the rim by the lens (GlassPipeline), placed pixel for pixel over the
            // spot it came from; then a veil of its own average colour, darkened, so white text stays readable
            // without colouring the glass (no fixed tint).
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, BodyWidth, BodyHeight + Math.Max(0, Membrane.Max) + 2));
            var avg = BackdropAverage;
            var veil = new SolidColorBrush(Color.FromArgb(0x58, (byte)(avg.R * 0.22), (byte)(avg.G * 0.22), (byte)(avg.B * 0.22)));
            veil.Freeze();
            dc.DrawRectangle(veil, null, area);
        }
        else
        {
            dc.DrawRectangle(DrawnGlass, null, area);
            dc.DrawRectangle(Grain, null, area);
        }
        dc.Pop();
        dc.Pop();
        dc.PushOpacity(strength);
        dc.DrawGeometry(null, EdgeLight, outline);
        dc.Pop();
    }

    private static readonly Typeface ChipFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);


    // ------------------------------------------------------------------ satellites

    /// <summary>A circle outside the island; while close it is joined to it by a neck, like a drop pulling away.</summary>
    private void DrawSatellite(DrawingContext dc, Satellite s)
    {
        // Left-side satellites (plugin chips) are the right-side drawing mirrored.
        var dir = s.Left ? -1 : 1;
        var edgeX = s.Left ? 0 : BodyWidth;
        var cx = edgeX + (dir * s.Out);
        var cy = s.Y;
        var r = s.Radius * s.Scale;
        if (r < 0.3)
        {
            return;
        }

        var gap = (dir * (cx - edgeX)) - r;
        if (gap < r * 1.6)
        {
            // The neck thins as the circle moves away and is gone at 1.6 r.
            var k = Math.Clamp(1 - (gap / (r * 1.6)), 0, 1);
            var half = r * (0.25 + (0.6 * k));
            var neck = new StreamGeometry();
            using (var g = neck.Open())
            {
                g.BeginFigure(new Point(edgeX - (2 * dir), cy - (half + (4 * k))), true, true);
                g.BezierTo(new Point(edgeX + (dir * gap * 0.5), cy - (half * 0.55)), new Point(cx - (dir * r * 0.6), cy - (r * 0.8)), new Point(cx, cy - (r * 0.85)), true, true);
                g.LineTo(new Point(cx, cy + (r * 0.85)), true, true);
                g.BezierTo(new Point(cx - (dir * r * 0.6), cy + (r * 0.8)), new Point(edgeX + (dir * gap * 0.5), cy + (half * 0.55)), new Point(edgeX - (2 * dir), cy + half + (4 * k)), true, true);
            }

            neck.Freeze();
            dc.DrawGeometry(Fill, null, neck);
        }

        dc.DrawEllipse(Fill, null, new Point(cx, cy), r + 2.5, r + 2.5);
        dc.DrawEllipse(s.Color, null, new Point(cx, cy), r, r);
        if (s.Ring is { } ring)
        {
            dc.DrawEllipse(null, ring, new Point(cx, cy), r + 1.2, r + 1.2);
        }

        if (s.Icon is { } icon)
        {
            var i = r * 1.1;
            dc.DrawImage(icon, new Rect(cx - (i / 2), cy - (i / 2), i, i));
        }
        else if (s.Text is { Length: > 0 } text)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ChipFace, r * (text.Length > 2 ? 0.95 : 1.2), Brushes.White, 1.25);
            dc.DrawText(ft, new Point(cx - (ft.Width / 2), cy - (ft.Height / 2)));
        }
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        ArgumentNullException.ThrowIfNull(hitTestParameters);
        var p = hitTestParameters.HitPoint;
        return _outline?.FillContains(p) == true || Cluster?.HitTest(p, BodyWidth) is not null ? new PointHitTestResult(this, p) : null;
    }

    private static Brush[] CreateShadowBrushes()
    {
        var brushes = new Brush[ShadowLayers];
        for (var i = 0; i < ShadowLayers; i++)
        {
            // Darkest close to the island, fading outwards.
            var alpha = (byte)Math.Round(255 * 0.075 * (1 - (i / (double)(ShadowLayers + 1))));
            var b = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            b.Freeze();
            brushes[i] = b;
        }

        return brushes;
    }

    /// <summary>The outline, grown by <paramref name="inflate"/> (shadow layers) and lowered by <paramref name="drop"/>.</summary>
    private StreamGeometry Outline(double inflate, double drop, bool ears)
    {
        var w = BodyWidth;
        var h = BodyHeight;
        var r = Math.Min(Radius + inflate, (h + inflate + drop) / 2);
        var left = -inflate;
        var right = w + inflate;
        var bottom = h + inflate + drop;
        var points = new List<Point>(160);

        // Left side, from the top edge down (the top edge itself is a straight line at y = 0, never moved).
        if (ears)
        {
            Arc(points, new Point(left - Ear, Ear), Ear, -Math.PI / 2, 0, 6);
        }
        else
        {
            points.Add(new Point(left, 0));
        }

        // Continuous (squircle) lower corners: they begin further from the corner and bend gradually.
        var corner = Domain.Visuals.Squircle.Corner(r, Smoothing, 7);
        var reach = Math.Min(Domain.Visuals.Squircle.Reach(r, Smoothing), Math.Min((right - left) / 2, bottom - (ears ? Ear : 0)));
        var squeeze = reach / Math.Max(0.01, Domain.Visuals.Squircle.Reach(r, Smoothing));
        Line(points, new Point(left, ears ? Ear : 0), new Point(left, bottom - reach), 6);
        foreach (var (cx, cy) in corner)
        {
            // Bottom-left: the bottom-right corner mirrored, walked from the side to the bottom.
            points.Add(new Point(left - (cx * squeeze), bottom + (cy * squeeze)));
        }

        Line(points, new Point(left + reach, bottom), new Point(right - reach, bottom), Math.Max(8, (int)((right - left) / 3)));
        for (var i = corner.Count - 1; i >= 0; i--)
        {
            // Bottom-right, from the bottom edge up to the side (the corner's points run the other way).
            points.Add(new Point(right + (corner[i].X * squeeze), bottom + (corner[i].Y * squeeze)));
        }

        Line(points, new Point(right, bottom - reach), new Point(right, ears ? Ear : 0), 6);
        if (ears)
        {
            Arc(points, new Point(right + Ear, Ear), Ear, Math.PI, Math.PI * 3 / 2, 6);
        }
        else
        {
            points.Add(new Point(right, 0));
        }

        // The stretch grows towards the bottom: nothing at the top edge, all of it at the lowest point.
        var lateral = Lateral;
        var center = LateralCenter * w;
        var spread = Math.Max(30, w * 0.22);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var depth = Math.Clamp(p.Y / Math.Max(1, h), 0, 1.2);
            var k = Math.Pow(depth, 1.3);
            var y = p.Y + (Membrane.At(p.X / w) * k);
            var x = p.X;
            if (lateral != 0)
            {
                var d = (p.X - center) / spread;
                x += lateral * depth * depth * Math.Exp(-d * d);
            }

            points[i] = new Point(x, y);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            ctx.PolyLineTo(points.GetRange(1, points.Count - 1), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void Line(List<Point> points, Point from, Point to, int steps)
    {
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            points.Add(new Point(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t)));
        }
    }

    /// <summary>Points on a circle from angle <paramref name="a0"/> to <paramref name="a1"/> (y grows downwards).</summary>
    private static void Arc(List<Point> points, Point center, double radius, double a0, double a1, int steps)
    {
        for (var i = 0; i <= steps; i++)
        {
            var a = a0 + ((a1 - a0) * i / steps);
            points.Add(new Point(center.X + (radius * Math.Cos(a)), center.Y + (radius * Math.Sin(a))));
        }
    }
}

/// <summary>How the island's lower part looks.</summary>
public enum GlassLevel
{
    /// <summary>Black, as before.</summary>
    Off = 0,

    /// <summary>Drawn glass: gradient, grain and a line of light on the edge (no capture, almost free).</summary>
    Drawn,

    /// <summary>What is behind the open island, blurred and refracted (a few pictures a second, only while open).</summary>
    Real,
}

/// <summary>A small state circle beside the island (see <see cref="NotchBody.Satellites"/>).</summary>
public sealed class Satellite
{
    /// <summary>How far the centre is outside the island's right edge (px; negative = still inside).</summary>
    public double Out { get; set; }

    public double Y { get; set; } = 17;

    public double Radius { get; set; } = 7;

    /// <summary>0…1: grows as it comes out.</summary>
    public double Scale { get; set; } = 1;

    public Brush Color { get; set; } = Brushes.White;

    public Pen? Ring { get; set; }

    public ImageSource? Icon { get; set; }

    /// <summary>A short number or word inside the circle (plugin chips).</summary>
    public string? Text { get; set; }

    /// <summary>Out of the left side instead of the right.</summary>
    public bool Left { get; set; }
}
