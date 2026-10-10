using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JackiVpn.Desktop.Views;

/// <summary>Live throughput chart: download as a filled smooth area, upload as a line, auto-scaled.</summary>
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty DownProperty = DependencyProperty.Register(
        nameof(Down), typeof(IReadOnlyList<double>), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpProperty = DependencyProperty.Register(
        nameof(Up), typeof(IReadOnlyList<double>), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownStrokeProperty = DependencyProperty.Register(
        nameof(DownStroke), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownFillProperty = DependencyProperty.Register(
        nameof(DownFill), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpStrokeProperty = DependencyProperty.Register(
        nameof(UpStroke), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Down { get => (IReadOnlyList<double>?)GetValue(DownProperty); set => SetValue(DownProperty, value); }

    public IReadOnlyList<double>? Up { get => (IReadOnlyList<double>?)GetValue(UpProperty); set => SetValue(UpProperty, value); }

    public Brush DownStroke { get => (Brush)GetValue(DownStrokeProperty); set => SetValue(DownStrokeProperty, value); }

    public Brush? DownFill { get => (Brush?)GetValue(DownFillProperty); set => SetValue(DownFillProperty, value); }

    public Brush UpStroke { get => (Brush)GetValue(UpStrokeProperty); set => SetValue(UpStrokeProperty, value); }

    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var grid = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        grid.Freeze();
        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        var down = Down ?? [];
        var up = Up ?? [];
        var max = NiceMax(Math.Max(down.DefaultIfEmpty(0).Max(), up.DefaultIfEmpty(0).Max()));
        var top = 14.0;
        var plotH = h - top - 2;

        if (down.Count > 1)
        {
            var line = Smooth(Points(down, w, plotH, top, max));
            var area = line.Clone();
            if (area.Figures.Count > 0)
            {
                var f = area.Figures[0];
                f.Segments.Add(new LineSegment(new Point(w, h), false));
                f.Segments.Add(new LineSegment(new Point(f.StartPoint.X, h), false));
                f.IsClosed = true;
            }

            if (DownFill is { } fill)
            {
                dc.DrawGeometry(fill, null, area);
            }

            dc.DrawGeometry(null, new Pen(DownStroke, 2) { LineJoin = PenLineJoin.Round }, line);
        }

        if (up.Count > 1)
        {
            dc.DrawGeometry(null, new Pen(UpStroke, 1.6) { LineJoin = PenLineJoin.Round }, Smooth(Points(up, w, plotH, top, max)));
        }

        var label = new FormattedText(ViewModels.Format.Speed((long)max), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(2, 0));
    }

    private static double NiceMax(double value)
    {
        var v = Math.Max(value, 64 * 1024);
        var magnitude = Math.Pow(2, Math.Ceiling(Math.Log2(v)));
        return magnitude * 0.75 >= v ? magnitude * 0.75 : magnitude;
    }

    private static List<Point> Points(IReadOnlyList<double> values, double w, double plotH, double top, double max)
    {
        var step = w / Math.Max(1, values.Count - 1);
        var pts = new List<Point>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            pts.Add(new Point(i * step, top + plotH - Math.Clamp(values[i] / max, 0, 1) * plotH));
        }

        return pts;
    }

    /// <summary>Catmull-Rom spline through the points (smooth, passes through every sample).</summary>
    private static PathGeometry Smooth(List<Point> p)
    {
        var fig = new PathFigure { StartPoint = p[0], IsFilled = true };
        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = i > 0 ? p[i - 1] : p[i];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = i + 2 < p.Count ? p[i + 2] : p2;
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }

        return new PathGeometry([fig]);
    }
}

/// <summary>270° speed gauge whose value glides to new readings.</summary>
public sealed class SpeedGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SpeedGauge), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(SpeedGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly double[] Scales = [10, 25, 50, 100, 250, 500, 1000, 2500];

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (SpeedGauge)d;
        var to = Math.Max(0, (double)e.NewValue);
        if (Motion.Reduced)
        {
            g.BeginAnimation(DisplayValueProperty, null);
            g.DisplayValue = to;
            return;
        }

        g.BeginAnimation(DisplayValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2 + size * 0.06);
        var radius = size / 2 - 10;
        var max = Scales.FirstOrDefault(s => s >= DisplayValue * 1.05, Scales[^1]);
        var fraction = Math.Clamp(DisplayValue / max, 0, 1);
        var track = new Pen(TrackBrush, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, radius, 135, 270));
        if (fraction > 0.002)
        {
            dc.DrawGeometry(null, new Pen(ValueBrush, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(center, radius, 135, 270 * fraction));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var zero = new FormattedText("0", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, LabelBrush, dpi);
        var top = new FormattedText(max.ToString("0", CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, LabelBrush, dpi);
        var a0 = Polar(center, radius, 135);
        var a1 = Polar(center, radius, 45);
        dc.DrawText(zero, new Point(a0.X - zero.Width / 2, a0.Y + 8));
        dc.DrawText(top, new Point(a1.X - top.Width / 2, a1.Y + 8));
    }

    private static Point Polar(Point c, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        var start = Polar(c, r, startDeg);
        var end = Polar(c, r, startDeg + sweepDeg);
        var fig = new PathFigure { StartPoint = start, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
    }
}
