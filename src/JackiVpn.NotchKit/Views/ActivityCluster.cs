using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using JackiVpn.Domain.Activity;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The activity circle beside the island, and the circles behind it.
/// <para>
/// One circle (the lead) shows the newest running task: its icon and a ring of how much is done (a turning arc when
/// the amount is unknown — never an invented number). It comes out of the island's right side like a drop of a thick
/// liquid: an analytic metaball (two cubic Béziers tangent to both circles, no blur, no threshold) whose neck thins
/// as the drop moves away and snaps at a set distance; the snap releases the neck's pull, the drop overshoots,
/// wobbles (a damped squash-and-stretch spring) and settles. Going back is the same film backwards: the neck forms
/// again, the drop sinks into the island and a small wave runs along its edge.
/// </para>
/// <para>
/// A click on the lead brings the other tasks out from behind it one by one (each with its own ring and a caption);
/// a finished task's circle goes back behind the lead and is gone.
/// </para>
/// All motion is stepped only while something moves (the caller's frame loop stops when <see cref="Step"/> says so).
/// </summary>
public sealed class ActivityCluster
{
    public const double LeadRadius = 16;
    public const double ItemRadius = 13;
    private const double Gap = 9;
    private const double NeckReach = 1.3;
    private const int MaxShown = 6;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CaptionBack = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x05, 0x06, 0x09)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)));

    private readonly List<Bubble> _bubbles = [];
    private ActivityItem? _lead;

    // The lead drop: distance of its centre from the centre of the island's rounded right end.
    private double _d;
    private double _dv;
    private double _dTarget;
    private bool _attached = true;

    // Squash and stretch of the drop (0 = round).
    private double _e;
    private double _ev;
    private double _leadProgress;
    private double _leadProgressV;
    private bool _open;

    /// <summary>The island's rounded end radius (half its collapsed height).</summary>
    public double EndRadius { get; set; } = 17;

    /// <summary>The icon of a task (its app's picture, a plugin's icon), or null for its initial.</summary>
    public Func<ActivityItem, ImageSource?>? IconFor { get; set; }

    /// <summary>The drop went back into the island (time for the wave).</summary>
    public event Action? Merged;

    /// <summary>The drop broke away from the island.</summary>
    public event Action? Detached;

    /// <summary>Angle of the turning arc (unknown amounts), advanced by the caller only while one is visible.</summary>
    public double Spin { get; set; }

    public bool Visible => _lead is not null || _d > 0.5;

    public bool IsOpen => _open;

    /// <summary>A visible task has an unknown amount (the arc must turn).</summary>
    public bool NeedsSpin => (_lead is { Progress: null, Finished: false } && Visible) || (_open && _bubbles.Any(b => b.Item.Progress is null && !b.Item.Finished && !b.Leaving));

    public int Count => _bubbles.Count(b => !b.Leaving);

    /// <summary>Takes the current tasks (running first, newest first).</summary>
    public void Set(IReadOnlyList<ActivityItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var shown = items.Take(MaxShown).ToList();
        foreach (var b in _bubbles)
        {
            var now = shown.FirstOrDefault(i => i.Source == b.Item.Source && i.Id == b.Item.Id);
            if (now is null)
            {
                b.Leave();
            }
            else
            {
                b.Item = now;
            }
        }

        var index = 0;
        foreach (var item in shown)
        {
            if (!_bubbles.Any(b => !b.Leaving && b.Item.Source == item.Source && b.Item.Id == item.Id))
            {
                var bubble = new Bubble(item) { Delay = _open ? 0.06 * index : 0 };
                _bubbles.Add(bubble);
            }

            index++;
        }

        var previous = _lead;
        _lead = shown.FirstOrDefault(i => !i.Finished) ?? shown.FirstOrDefault();
        if (_lead is null)
        {
            _open = false;
        }

        if (previous is null && _lead is not null)
        {
            _leadProgress = _lead.Progress ?? 0;
        }

        _dTarget = _lead is null ? 0 : EndRadius + Gap + LeadRadius;
        Slot();
    }

    /// <summary>Opens or closes the circles behind the lead.</summary>
    public void Toggle()
    {
        _open = !_open && _bubbles.Count(b => !b.Leaving) > 0;
        var i = 0;
        foreach (var b in _bubbles.Where(b => !b.Leaving))
        {
            b.Delay = _open ? 0.07 * i++ : 0;
        }

        Slot();
    }

    public void Close()
    {
        if (_open)
        {
            Toggle();
        }
    }

    private void Slot()
    {
        var i = 0;
        foreach (var b in _bubbles.Where(b => !b.Leaving))
        {
            b.TargetX = 0;
            b.TargetY = _open ? LeadRadius + 8 + ItemRadius + (i * ((2 * ItemRadius) + 7)) : 0;
            b.TargetScale = _open ? 1 : 0.45;
            b.TargetAlpha = _open ? 1 : 0;
            i++;
        }
    }

    /// <summary>One physics step; true while anything still moves.</summary>
    public bool Step(double dt)
    {
        var moving = false;
        if (dt <= 0)
        {
            return true;
        }

        // The lead: a spring towards its place, the neck pulling back while it holds.
        var touching = EndRadius + LeadRadius;
        var reach = EndRadius + (LeadRadius * NeckReach);
        var a = (150 * (_dTarget - _d)) - (2 * Math.Sqrt(150) * 0.62 * _dv);
        if (_attached && _d > touching * 0.7)
        {
            a -= 42 * (_d - (touching * 0.7));
        }

        _dv += a * dt;
        _d += _dv * dt;
        if (_dTarget == 0 && _attached && _d < LeadRadius * 0.5 && _dv < -1)
        {
            // Swallowed: the island's edge ripples once, and the drop rests at its centre.
            Merged?.Invoke();
            _d = 0;
            _dv = 0;
        }

        if (_d < 0)
        {
            _d = 0;
            _dv = 0;
        }

        if (_attached && _d > reach)
        {
            // The neck snaps: the pull is gone at once, the drop overshoots and wobbles.
            _attached = false;
            _ev += 7.5;
            Detached?.Invoke();
        }
        else if (!_attached && _d < reach * 0.92)
        {
            _attached = true;
        }

        // Stretched towards the island while the neck holds; a squash-and-stretch spring after the snap.
        var stretch = _attached && _d > EndRadius ? Math.Clamp((_d - EndRadius) / (reach - EndRadius), 0, 1) * 0.16 : 0;
        var ea = (320 * (stretch - _e)) - (2 * Math.Sqrt(320) * 0.16 * _ev);
        _ev += ea * dt;
        _e += _ev * dt;
        _e = Math.Clamp(_e, -0.35, 0.35);

        var target = _lead?.Progress ?? _leadProgress;
        var pa = (90 * (target - _leadProgress)) - (2 * Math.Sqrt(90) * _leadProgressV);
        _leadProgressV += pa * dt;
        _leadProgress += _leadProgressV * dt;

        moving |= Math.Abs(_dv) > 0.05 || Math.Abs(_dTarget - _d) > 0.05 || Math.Abs(_ev) > 0.01 || Math.Abs(_e - stretch) > 0.004 || Math.Abs(target - _leadProgress) > 0.001;
        if (!moving)
        {
            _d = _dTarget;
            _e = stretch;
            _ev = 0;
            _dv = 0;
            _leadProgress = target;
        }

        foreach (var b in _bubbles)
        {
            moving |= b.Step(dt);
        }

        _bubbles.RemoveAll(b => b.Leaving && b.Alpha < 0.02 && b.Scale < 0.5);
        return moving;
    }

    /// <summary>Where the lead's centre is, in the island body's coordinates.</summary>
    public Point LeadCenter(double bodyWidth) => new(bodyWidth - EndRadius + _d, EndRadius);

    /// <summary>Which circle is at a point: -1 the lead, 0… a circle behind it, null none.</summary>
    public int? HitTest(Point p, double bodyWidth)
    {
        if (!Visible || _d < EndRadius)
        {
            return null;
        }

        var c = LeadCenter(bodyWidth);
        if ((p - c).Length <= LeadRadius + 2)
        {
            return -1;
        }

        if (!_open)
        {
            return null;
        }

        var visible = _bubbles.Where(b => !b.Leaving).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            var b = visible[i];
            if ((p - new Point(c.X + b.X, c.Y + b.Y)).Length <= (ItemRadius * b.Scale) + 2)
            {
                return i;
            }
        }

        return null;
    }

    public ActivityItem? ItemAt(int index) => _bubbles.Where(b => !b.Leaving).ElementAtOrDefault(index)?.Item;

    // ------------------------------------------------------------------ drawing

    /// <summary>Draws behind the island (so everything comes out from under it).</summary>
    public void Draw(DrawingContext dc, double bodyWidth, Brush fill, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (!Visible)
        {
            return;
        }

        var c1 = new Point(bodyWidth - EndRadius, EndRadius);
        var c2 = LeadCenter(bodyWidth);

        // The circles behind the lead: back to front, so each comes out from under the one before.
        foreach (var b in Enumerable.Reverse(_bubbles))
        {
            if (b.Alpha < 0.02 || b.Scale < 0.05)
            {
                continue;
            }

            var center = new Point(c2.X + b.X, c2.Y + b.Y);
            dc.PushOpacity(b.Alpha);
            DrawCircle(dc, b.Item, center, ItemRadius * b.Scale, ItemRadius * b.Scale, b.Progress, fill, pixelsPerDip, small: true);
            if (_open && b.Alpha > 0.5 && !b.Leaving)
            {
                DrawCaption(dc, b.Item, new Point(center.X - (ItemRadius * b.Scale) - 6, center.Y), pixelsPerDip);
            }

            dc.Pop();
        }

        if (_attached && _d > 0.5)
        {
            var neck = Metaball(c1, EndRadius, c2, LeadRadius);
            if (neck is not null)
            {
                dc.DrawGeometry(fill, null, neck);
            }
        }

        if (_lead is { } lead)
        {
            var rx = LeadRadius * (1 + _e);
            var ry = LeadRadius * (1 - (_e * 0.8));
            DrawCircle(dc, lead, c2, rx, ry, _leadProgress, fill, pixelsPerDip, small: false);
            var others = _bubbles.Count(b => !b.Leaving && !b.Item.Finished);
            if (others > 1 && !_open)
            {
                // How many tasks are under it.
                var badge = new Point(c2.X + (rx * 0.72), c2.Y - (ry * 0.72));
                dc.DrawEllipse(Brushes.White, new Pen(fill, 1.6), badge, 6.6, 6.6);
                var n = Text(others.ToString(CultureInfo.InvariantCulture), 8.5, Brushes.Black, Bold, pixelsPerDip);
                dc.DrawText(n, new Point(badge.X - (n.Width / 2), badge.Y - (n.Height / 2)));
            }
        }
        else if (_d > 0.5)
        {
            // Going back in: an empty drop.
            dc.DrawEllipse(fill, null, c2, LeadRadius * (1 + _e), LeadRadius * (1 - (_e * 0.8)));
        }
    }

    private void DrawCircle(DrawingContext dc, ActivityItem item, Point c, double rx, double ry, double progress, Brush fill, double ppd, bool small)
    {
        dc.DrawEllipse(fill, null, c, rx, ry);
        var r = Math.Min(rx, ry);
        var ringR = r - (small ? 2.2 : 2.6);
        var thickness = small ? 2.0 : 2.4;
        var color = ColorOf(item);
        var pen = new Pen(color, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawEllipse(null, new Pen(Track, thickness), c, ringR, ringR);
        if (item.Finished || item.Progress is { })
        {
            var p = item.Finished ? 1 : Math.Clamp(progress, 0, 1);
            if (p >= 0.999)
            {
                dc.DrawEllipse(null, pen, c, ringR, ringR);
            }
            else if (p > 0.004)
            {
                dc.DrawGeometry(null, pen, Arc(c, ringR, -90, -90 + (360 * p)));
            }
        }
        else
        {
            // Unknown amount: a turning arc, no number.
            dc.DrawGeometry(null, pen, Arc(c, ringR, Spin - 90, Spin + 10));
        }

        var inner = ringR - (thickness / 2) - 1.5;
        if (item.State == ActivityState.Done)
        {
            Glyph(dc, "", c, inner * 1.15, color, ppd);
        }
        else if (item.State == ActivityState.Failed)
        {
            Glyph(dc, "", c, inner * 1.05, color, ppd);
        }
        else if (IconFor?.Invoke(item) is { } icon)
        {
            var s = inner * 1.32;
            dc.PushClip(new EllipseGeometry(c, inner, inner));
            dc.DrawImage(icon, new Rect(c.X - (s / 2), c.Y - (s / 2), s, s));
            dc.Pop();
        }
        else
        {
            var letter = Text(Initial(item), inner * 1.05, Brushes.White, Bold, ppd);
            dc.DrawText(letter, new Point(c.X - (letter.Width / 2), c.Y - (letter.Height / 2)));
        }

        if (item.State == ActivityState.Paused)
        {
            Glyph(dc, "", new Point(c.X + (r * 0.7), c.Y + (r * 0.7)), 8, Muted, ppd);
        }
    }

    private static void DrawCaption(DrawingContext dc, ActivityItem item, Point at, double ppd)
    {
        var title = Text(Short(item.Title, 26), 11, Brushes.White, Face, ppd);
        var line = Text(Describe(item), 9.5, Muted, Face, ppd);
        var w = Math.Max(title.Width, line.Width) + 14;
        var h = title.Height + line.Height + 4;
        // To the left of its circle, under the island (the window has room there, not to the right).
        var left = at.X - w;
        dc.DrawRoundedRectangle(CaptionBack, null, new Rect(left, at.Y - (h / 2), w, h), 7, 7);
        dc.DrawText(title, new Point(left + 7, at.Y - (h / 2) + 2));
        dc.DrawText(line, new Point(left + 7, at.Y - (h / 2) + 2 + title.Height));
    }

    /// <summary>The caption's second line: the amount (with its accuracy), size, speed — only what is known.</summary>
    public static string Describe(ActivityItem item)
    {
        var parts = new List<string>();
        if (item.State == ActivityState.Failed)
        {
            parts.Add("failed");
        }
        else if (item.State == ActivityState.Done)
        {
            parts.Add("done");
        }
        else if (item.State == ActivityState.Waiting)
        {
            parts.Add("waiting for you");
        }
        else if (item.State == ActivityState.Paused)
        {
            parts.Add("paused");
        }

        if (!item.Finished && item.Progress is { } p)
        {
            parts.Add((item.Accuracy == ProgressAccuracy.Estimate ? "≈" : string.Empty) + (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
        }

        if (item.Total is { } total)
        {
            parts.Add(item.Done is { } done && !item.Finished ? ActivityBus.Bytes(done) + " / " + ActivityBus.Bytes(total) : ActivityBus.Bytes(total));
        }
        else if (item.Done is { } done)
        {
            parts.Add(ActivityBus.Bytes(done));
        }

        if (!item.Finished && item.Rate is > 0 and var rate)
        {
            parts.Add(ActivityBus.Bytes(rate) + "/s");
        }

        if (item.Detail is { } detail && parts.Count < 3)
        {
            parts.Add(detail);
        }

        return parts.Count == 0 ? "working…" : string.Join("  ·  ", parts);
    }

    public static Brush ColorOf(ActivityItem item) => item.State switch
    {
        ActivityState.Waiting => Solid("#FBBF24"),
        ActivityState.Done => Solid("#34D399"),
        ActivityState.Failed => Solid("#F87171"),
        ActivityState.Paused => Solid("#9CA3AF"),
        _ => Solid(item.Color ?? DefaultColor(item.Source)),
    };

    public static string DefaultColor(string source) => source switch
    {
        "claude" => "#D97757",
        "chatgpt" or "codex" => "#10A37F",
        "aftereffects" => "#9999FF",
        "premiere" => "#9999FF",
        "mediaencoder" => "#8B8BFF",
        "photoshop" => "#31A8FF",
        "illustrator" => "#FF9A00",
        "explorer" => "#E5E7EB",
        _ when source.StartsWith("plugin:", StringComparison.Ordinal) => "#A78BFA",
        _ => "#3D9BFF",
    };

    private static readonly Dictionary<string, Brush> Brushes_ = new(StringComparer.OrdinalIgnoreCase);

    private static Brush Solid(string hex)
    {
        if (!Brushes_.TryGetValue(hex, out var b))
        {
            b = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
            Brushes_[hex] = b;
        }

        return b;
    }

    private static string Initial(ActivityItem item)
    {
        var name = item.Source.StartsWith("plugin:", StringComparison.Ordinal) ? item.Title : item.Source;
        return name.Length == 0 ? "•" : char.ToUpperInvariant(name[0]).ToString();
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void Glyph(DrawingContext dc, string glyph, Point c, double size, Brush brush, double ppd)
    {
        var t = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe MDL2 Assets"), Math.Max(4, size), brush, ppd);
        dc.DrawText(t, new Point(c.X - (t.Width / 2), c.Y - (t.Height / 2)));
    }

    private static FormattedText Text(string s, double size, Brush brush, Typeface face, double ppd) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, ppd);

    private static StreamGeometry Arc(Point c, double r, double fromDeg, double toDeg)
    {
        var a0 = fromDeg * Math.PI / 180;
        var a1 = toDeg * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + (r * Math.Cos(a0)), c.Y + (r * Math.Sin(a0))), false, false);
            ctx.ArcTo(new Point(c.X + (r * Math.Cos(a1)), c.Y + (r * Math.Sin(a1))), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
        }

        g.Freeze();
        return g;
    }

    /// <summary>
    /// The neck between two circles as one closed path (after Hiroyuki Sato's metaball construction): two points on
    /// each circle at a spread angle, joined by cubic Béziers whose handles shrink with distance. The spread itself
    /// narrows as the circles part, so the neck thins to nothing exactly at the snap distance. Null = no neck.
    /// </summary>
    public static StreamGeometry? Metaball(Point c1, double r1, Point c2, double r2, double handle = 2.4, double spread = 0.5)
    {
        var d = (c2 - c1).Length;
        var max = r1 + (r2 * NeckReach);
        if (r1 <= 0 || r2 <= 0 || d > max || d <= Math.Abs(r1 - r2))
        {
            return null;
        }

        // Thinner towards the snap.
        var parting = Math.Clamp((d - (r1 + r2)) / Math.Max(0.001, max - (r1 + r2)), 0, 1);
        var v = spread * (1 - (parting * parting * (3 - (2 * parting))));
        double u1 = 0, u2 = 0;
        if (d < r1 + r2)
        {
            u1 = Math.Acos(Math.Clamp(((r1 * r1) + (d * d) - (r2 * r2)) / (2 * r1 * d), -1, 1));
            u2 = Math.Acos(Math.Clamp(((r2 * r2) + (d * d) - (r1 * r1)) / (2 * r2 * d), -1, 1));
        }

        var between = Math.Atan2(c2.Y - c1.Y, c2.X - c1.X);
        var maxSpread = Math.Acos(Math.Clamp((r1 - r2) / d, -1, 1));
        var a1 = between + u1 + ((maxSpread - u1) * v);
        var a2 = between - u1 - ((maxSpread - u1) * v);
        var a3 = between + Math.PI - u2 - ((Math.PI - u2 - maxSpread) * v);
        var a4 = between - Math.PI + u2 + ((Math.PI - u2 - maxSpread) * v);
        var p1 = At(c1, a1, r1);
        var p2 = At(c1, a2, r1);
        var p3 = At(c2, a3, r2);
        var p4 = At(c2, a4, r2);
        var total = r1 + r2;
        var d2 = Math.Min(v * handle, (p1 - p3).Length / total) * Math.Min(1, d * 2 / total);
        var h1 = At(p1, a1 - (Math.PI / 2), r1 * d2);
        var h2 = At(p2, a2 + (Math.PI / 2), r1 * d2);
        var h3 = At(p3, a3 + (Math.PI / 2), r2 * d2);
        var h4 = At(p4, a4 - (Math.PI / 2), r2 * d2);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p1, true, true);
            ctx.BezierTo(h1, h3, p3, true, true);
            ctx.ArcTo(p4, new Size(r2, r2), 0, d > r1, SweepDirection.Counterclockwise, true, true);
            ctx.BezierTo(h4, h2, p2, true, true);
        }

        g.Freeze();
        return g;
    }

    private static Point At(Point c, double angle, double r) => new(c.X + (r * Math.Cos(angle)), c.Y + (r * Math.Sin(angle)));

    private static T Frozen<T>(T f)
        where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>A circle behind the lead: position relative to the lead, scale, opacity and shown progress on springs.</summary>
    private sealed class Bubble(ActivityItem item)
    {
        private double _xv;
        private double _yv;
        private double _sv;
        private double _av;
        private double _pv;

        public ActivityItem Item { get; set; } = item;

        public bool Leaving { get; private set; }

        public double Delay { get; set; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public double Scale { get; private set; } = 0.45;

        public double Alpha { get; private set; }

        public double Progress { get; private set; } = item.Progress ?? 0;

        public double TargetX { get; set; }

        public double TargetY { get; set; }

        public double TargetScale { get; set; } = 0.45;

        public double TargetAlpha { get; set; }

        public void Leave()
        {
            Leaving = true;
            TargetX = 0;
            TargetY = 0;
            TargetScale = 0.3;
            TargetAlpha = 0;
            Delay = 0;
        }

        public bool Step(double dt)
        {
            if (Delay > 0)
            {
                Delay -= dt;
                return true;
            }

            var moving = false;
            moving |= Spring(ref _xv, X, TargetX, 210, 0.62, dt, out var x);
            moving |= Spring(ref _yv, Y, TargetY, 210, 0.62, dt, out var y);
            moving |= Spring(ref _sv, Scale, TargetScale, 260, 0.55, dt, out var s);
            moving |= Spring(ref _av, Alpha, TargetAlpha, 300, 1, dt, out var a);
            moving |= Spring(ref _pv, Progress, Item.Progress ?? Progress, 90, 1, dt, out var p);
            X = x;
            Y = y;
            Scale = s;
            Alpha = Math.Clamp(a, 0, 1);
            Progress = p;
            return moving;
        }

        private static bool Spring(ref double v, double value, double target, double k, double ratio, double dt, out double next)
        {
            var acc = (k * (target - value)) - (2 * Math.Sqrt(k) * ratio * v);
            v += acc * dt;
            next = value + (v * dt);
            if (Math.Abs(v) < 0.01 && Math.Abs(target - next) < 0.002)
            {
                v = 0;
                next = target;
                return false;
            }

            return true;
        }
    }
}
