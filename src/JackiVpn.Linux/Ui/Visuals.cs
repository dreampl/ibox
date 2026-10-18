using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Domain.Visuals;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>A round country flag (shared vector shapes, <see cref="Flags"/>); an initials badge for other countries.</summary>
public sealed class FlagView : Control
{
    public static readonly StyledProperty<string?> CountryCodeProperty = AvaloniaProperty.Register<FlagView, string?>(nameof(CountryCode));

    static FlagView() => AffectsRender<FlagView>(CountryCodeProperty);

    public string? CountryCode { get => GetValue(CountryCodeProperty); set => SetValue(CountryCodeProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 4)
        {
            return;
        }

        var ox = (Bounds.Width - size) / 2;
        var oy = (Bounds.Height - size) / 2;
        var circle = new EllipseGeometry(new Rect(ox, oy, size, size));
        var shapes = Flags.For(CountryCode);
        if (shapes is null)
        {
            context.DrawGeometry(Gradient(Accent, Accent2), null, circle);
            var text = new FormattedText(Flags.Initials(CountryCode), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size * 0.36, Brushes.White);
            context.DrawText(text, new Point(ox + ((size - text.Width) / 2), oy + ((size - text.Height) / 2)));
            return;
        }

        using (context.PushGeometryClip(circle))
        {
            foreach (var s in shapes)
            {
                var brush = new SolidColorBrush(Color.FromUInt32(s.Color));
                var p = s.Points;
                switch (s.Kind)
                {
                    case FlagShapeKind.Rect:
                        context.DrawRectangle(brush, null, new Rect(ox + (p[0] * size) - 0.3, oy + (p[1] * size) - 0.3, (p[2] * size) + 0.6, (p[3] * size) + 0.6));
                        break;
                    case FlagShapeKind.Circle:
                        context.DrawEllipse(brush, null, new Point(ox + (p[0] * size), oy + (p[1] * size)), p[2] * size, p[2] * size);
                        break;
                    case FlagShapeKind.Polygon:
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(new Point(ox + (p[0] * size), oy + (p[1] * size)), true);
                            for (var i = 2; i + 1 < p.Length; i += 2)
                            {
                                ctx.LineTo(new Point(ox + (p[i] * size), oy + (p[i + 1] * size)));
                            }

                            ctx.EndFigure(true);
                        }

                        context.DrawGeometry(brush, null, g);
                        break;
                }
            }
        }

        // Soft rim and top sheen: the flat flag sits like a small clay button.
        var sheen = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 0.6, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1) },
        };
        context.DrawGeometry(sheen, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1), circle);
    }
}

/// <summary>
/// Dotted world map with a soft pulse on the exit location. The dots are drawn once (per size and theme); only the
/// small pulse layer over the exit redraws while connected and visible, so the animation touches a few hundred pixels
/// instead of the whole map.
/// </summary>
public sealed class WorldMapView : Panel
{
    public static readonly StyledProperty<double?> LatitudeProperty = AvaloniaProperty.Register<WorldMapView, double?>(nameof(Latitude));
    public static readonly StyledProperty<double?> LongitudeProperty = AvaloniaProperty.Register<WorldMapView, double?>(nameof(Longitude));
    public static readonly StyledProperty<string?> CountryCodeProperty = AvaloniaProperty.Register<WorldMapView, string?>(nameof(CountryCode));
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<WorldMapView, bool>(nameof(IsActive));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly Dots _dots = new();
    private readonly Pulse _pulse = new();

    public WorldMapView()
    {
        IsHitTestVisible = false;
        Children.Add(_dots);
        Children.Add(_pulse);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(42), DispatcherPriority.Render, (_, _) => _pulse.InvalidateVisual());
    }

    public double? Latitude { get => GetValue(LatitudeProperty); set => SetValue(LatitudeProperty, value); }

    public double? Longitude { get => GetValue(LongitudeProperty); set => SetValue(LongitudeProperty, value); }

    public string? CountryCode { get => GetValue(CountryCodeProperty); set => SetValue(CountryCodeProperty, value); }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
        {
            UpdateTimer();
        }

        if (change.Property == IsActiveProperty || change.Property == LatitudeProperty || change.Property == LongitudeProperty || change.Property == CountryCodeProperty)
        {
            InvalidateArrange(); // the pulse layer moves to the new exit
            _pulse.InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        if (IsActive && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 330 : availableSize.Width;
        return new Size(w, Math.Min(double.IsInfinity(availableSize.Height) ? double.MaxValue : availableSize.Height, w * WorldMap.Rows / WorldMap.Columns));
    }

    private static (double Cell, double X, double Y) Layout(Size size)
    {
        var cell = Math.Min(size.Width / WorldMap.Columns, size.Height / WorldMap.Rows);
        return (cell, (size.Width - (cell * WorldMap.Columns)) / 2, (size.Height - (cell * WorldMap.Rows)) / 2);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _dots.Arrange(new Rect(finalSize));
        var (cell, ox, oy) = Layout(finalSize);
        if (IsActive && cell > 0 && (Latitude is { } lat && Longitude is { } lon ? (lat, lon) : WorldMap.CountryPoint(CountryCode)) is { } loc)
        {
            var (pc, pr) = WorldMap.Project(loc.Item1, loc.Item2);
            var reach = (cell * 6.7) + 2;
            var cx = ox + ((pc + 0.5) * cell);
            var cy = oy + ((pr + 0.5) * cell);
            _pulse.Cell = cell;
            _pulse.Arrange(new Rect(cx - reach, cy - reach, reach * 2, reach * 2));
        }
        else
        {
            _pulse.Arrange(default);
        }

        return finalSize;
    }

    /// <summary>The land dots: one cached geometry, redrawn only when the size or the theme changes.</summary>
    private sealed class Dots : Control
    {
        private StreamGeometry? _geometry;
        private Size _for;

        public override void Render(DrawingContext context)
        {
            var (cell, ox, oy) = Layout(Bounds.Size);
            if (cell <= 0)
            {
                return;
            }

            if (_geometry is null || _for != Bounds.Size)
            {
                _for = Bounds.Size;
                _geometry = new StreamGeometry();
                var r = cell * 0.3;
                using var ctx = _geometry.Open();
                for (var row = 0; row < WorldMap.Rows; row++)
                {
                    for (var col = 0; col < WorldMap.Columns; col++)
                    {
                        if (!WorldMap.IsLand(col, row))
                        {
                            continue;
                        }

                        var cx = ox + ((col + 0.5) * cell);
                        var cy = oy + ((row + 0.5) * cell);
                        ctx.BeginFigure(new Point(cx - r, cy), true);
                        ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
                        ctx.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
                        ctx.EndFigure(true);
                    }
                }
            }

            context.DrawGeometry(new SolidColorBrush(Look.P.MapDot), null, _geometry);
        }
    }

    /// <summary>The exit: a ring that grows and fades every 2.4 s around a solid core with a soft halo.</summary>
    private sealed class Pulse : Control
    {
        public double Cell { get; set; }

        public override void Render(DrawingContext context)
        {
            if (Cell <= 0 || Bounds.Width <= 0)
            {
                return;
            }

            var cell = Cell;
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var color = Success;
            var t = (Clock.Elapsed.TotalSeconds % 2.4) / 2.4;
            var ring = (cell * 1.2) + (t * cell * 5.5);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - t)), color.R, color.G, color.B)), 1.6), center, ring, ring);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)), null, center, cell * 1.6, cell * 1.6);
            context.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, Math.Max(1, cell * 0.3)), center, cell * 0.85, cell * 0.85);
        }
    }
}
/// <summary>Live throughput: download and upload as smooth lines over soft gradient areas, auto-scaled.</summary>
public sealed class TrafficChart : Control
{
    private IReadOnlyList<double> _down = [];
    private IReadOnlyList<double> _up = [];

    public void SetSeries(IReadOnlyList<double> down, IReadOnlyList<double> up)
    {
        _down = down;
        _up = up;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 10 || h < 10)
        {
            return;
        }

        var grid = new Pen(new SolidColorBrush(Ui.Divider), 1, new DashStyle([2, 4], 0));
        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        var max = NiceMax(Math.Max(_down.DefaultIfEmpty(0).Max(), _up.DefaultIfEmpty(0).Max()));
        const double top = 14;
        var plotH = h - top - 2;

        void Series(IReadOnlyList<double> values, Color color, double thickness)
        {
            if (values.Count < 2)
            {
                return;
            }

            var step = w / Math.Max(1, values.Count - 1);
            var pts = values.Select((v, i) => new Point(i * step, top + plotH - (Math.Clamp(v / max, 0, 1) * plotH))).ToList();
            var line = Smooth(pts, close: false, h);
            var area = Smooth(pts, close: true, h);
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(Look.P.IsLight ? (byte)0x55 : (byte)0x66, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) },
            };
            context.DrawGeometry(fill, null, area);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), line);
        }

        Series(_down, Chart1, 2.4);
        Series(_up, Chart2, 2);
        var label = new FormattedText(Speed(max), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 10, new SolidColorBrush(Muted));
        context.DrawText(label, new Point(2, 0));
    }

    private static double NiceMax(double value)
    {
        var v = Math.Max(value, 64 * 1024);
        var magnitude = Math.Pow(2, Math.Ceiling(Math.Log2(v)));
        return magnitude * 0.75 >= v ? magnitude * 0.75 : magnitude;
    }

    /// <summary>Catmull-Rom spline through the points; closed = down to the bottom edge (area).</summary>
    private static StreamGeometry Smooth(List<Point> p, bool close, double bottom)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(p[0], close);
        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = i > 0 ? p[i - 1] : p[i];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = i + 2 < p.Count ? p[i + 2] : p2;
            ctx.CubicBezierTo(new Point(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6)), new Point(p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6)), p2);
        }

        if (close)
        {
            ctx.LineTo(new Point(p[^1].X, bottom));
            ctx.LineTo(new Point(p[0].X, bottom));
        }

        ctx.EndFigure(close);
        return g;
    }
}

/// <summary>270° speed gauge (track + blue→violet value arc) whose value glides to new readings.</summary>
public sealed class SpeedGauge : Control
{
    private static readonly double[] Scales = [10, 25, 50, 100, 250, 500, 1000, 2500];
    private double _shown;
    private double _target;
    private DispatcherTimer? _glide;

    public double Value
    {
        get => _target;
        set
        {
            _target = Math.Max(0, value);
            _glide ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                _shown += (_target - _shown) * 0.18;
                if (Math.Abs(_target - _shown) < 0.01)
                {
                    _shown = _target;
                    _glide!.Stop();
                }

                InvalidateVisual();
            });
            _glide.Start();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 20)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, (Bounds.Height / 2) + (size * 0.06));
        var radius = (size / 2) - 10;
        var max = Scales.FirstOrDefault(s => s >= _shown * 1.05, Scales[^1]);
        var fraction = Math.Clamp(_shown / max, 0, 1);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(InputBg), 10, lineCap: PenLineCap.Round), Arc(center, radius, 135, 270));
        // A short accent stub at the start even at zero, as in the mockup.
        var shownFraction = Math.Max(fraction, 0.06);
        context.DrawGeometry(null, new Pen(AccentGradient, 10, lineCap: PenLineCap.Round), Arc(center, radius, 135, 270 * shownFraction));
    }

    private static Point Polar(Point c, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(c.X + (r * Math.Cos(rad)), c.Y + (r * Math.Sin(rad)));
    }

    private static StreamGeometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(Polar(c, r, startDeg), false);
        ctx.ArcTo(Polar(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return g;
    }
}
