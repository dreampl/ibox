using System;
using System.Windows;
using System.Windows.Media;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Dotted world map (see <see cref="WorldMap"/>) with a soft pulse on the exit location. The dots are one frozen
/// geometry drawn only when the size or theme changes; the pulse is a separate small visual animated at 24 fps
/// while connected and on screen.
/// </summary>
public sealed class WorldMapView : FrameworkElement
{
    public static readonly DependencyProperty LatitudeProperty = DependencyProperty.Register(
        nameof(Latitude), typeof(double?), typeof(WorldMapView), new PropertyMetadata(null, (d, _) => ((WorldMapView)d).DrawPulse()));

    public static readonly DependencyProperty LongitudeProperty = DependencyProperty.Register(
        nameof(Longitude), typeof(double?), typeof(WorldMapView), new PropertyMetadata(null, (d, _) => ((WorldMapView)d).DrawPulse()));

    /// <summary>Fallback position when the exit has no coordinates.</summary>
    public static readonly DependencyProperty CountryCodeProperty = DependencyProperty.Register(
        nameof(CountryCode), typeof(string), typeof(WorldMapView), new PropertyMetadata(null, (d, _) => ((WorldMapView)d).DrawPulse()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(WorldMapView), new PropertyMetadata(false, (d, _) => ((WorldMapView)d).OnActiveChanged()));

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DrawingVisual _dots = new();
    private readonly DrawingVisual _pulse = new();
    private readonly ViewClock _clock;
    private Size _drawnFor;

    public WorldMapView()
    {
        IsHitTestVisible = false;
        AddVisualChild(_dots);
        AddVisualChild(_pulse);
        _clock = new ViewClock(this, TimeSpan.FromMilliseconds(42), () =>
        {
            DrawPulse();
            return false;
        })
        { Enabled = false };
        ThemeManager.Changed += (_, _) =>
        {
            _drawnFor = default;
            InvalidateVisual();
        };
    }

    public double? Latitude { get => (double?)GetValue(LatitudeProperty); set => SetValue(LatitudeProperty, value); }

    public double? Longitude { get => (double?)GetValue(LongitudeProperty); set => SetValue(LongitudeProperty, value); }

    public string? CountryCode { get => (string?)GetValue(CountryCodeProperty); set => SetValue(CountryCodeProperty, value); }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _dots : _pulse;

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        return new Size(w, Math.Min(double.IsInfinity(availableSize.Height) ? double.MaxValue : availableSize.Height, w * WorldMap.Rows / WorldMap.Columns));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size != _drawnFor)
        {
            _drawnFor = size;
            DrawDots();
        }

        DrawPulse();
    }

    /// <summary>Cell size and offset that fit the whole map into the element.</summary>
    private (double Cell, double X, double Y) Layout()
    {
        var cell = Math.Min(ActualWidth / WorldMap.Columns, ActualHeight / WorldMap.Rows);
        return (cell, (ActualWidth - (cell * WorldMap.Columns)) / 2, (ActualHeight - (cell * WorldMap.Rows)) / 2);
    }

    private void DrawDots()
    {
        var (cell, ox, oy) = Layout();
        using var dc = _dots.RenderOpen();
        if (cell <= 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        var r = cell * 0.3;
        using (var ctx = geometry.Open())
        {
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
                    ctx.BeginFigure(new Point(cx - r, cy), isFilled: true, isClosed: true);
                    ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                    ctx.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(TryFindResource("B.MapDot") as Brush ?? Brushes.SlateGray, null, geometry);
    }

    private (double Lat, double Lon)? Location() =>
        Latitude is { } lat && Longitude is { } lon ? (lat, lon) : WorldMap.CountryPoint(CountryCode);

    private void OnActiveChanged()
    {
        _clock.Enabled = IsActive && !Motion.Reduced;
        DrawPulse();
    }

    private void DrawPulse()
    {
        using var dc = _pulse.RenderOpen();
        if (!IsActive || Location() is not { } loc)
        {
            return;
        }

        var (cell, ox, oy) = Layout();
        if (cell <= 0)
        {
            return;
        }

        var (col, row) = WorldMap.Project(loc.Lat, loc.Lon);
        var center = new Point(ox + ((col + 0.5) * cell), oy + ((row + 0.5) * cell));
        var color = TryFindResource("C.Success") is Color c ? c : Colors.LimeGreen;

        // A ring that grows and fades every 2.4 s, around a solid core with a soft halo.
        var t = Motion.Reduced ? 0.35 : (Clock.Elapsed.TotalSeconds % 2.4) / 2.4;
        var ring = (cell * 1.2) + (t * cell * 5.5);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - t)), color.R, color.G, color.B)), 1.6), center, ring, ring);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)), null, center, cell * 1.6, cell * 1.6);
        dc.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, Math.Max(1, cell * 0.3)), center, cell * 0.85, cell * 0.85);
    }
}
