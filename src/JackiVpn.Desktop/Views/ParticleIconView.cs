using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Orb dots that assemble into an icon (<see cref="ParticleMorph"/>): <see cref="Assemble"/> flies them from a turning
/// orb onto the icon's outline, <see cref="Disperse"/> sends them back. Frames run only while the dots move (and,
/// with <see cref="Shimmer"/>, while the icon is held); a settled icon is a still drawing.
/// </summary>
public sealed class ParticleIconView : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(MorphIcon), typeof(ParticleIconView), new FrameworkPropertyMetadata(MorphIcon.Link, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ParticleIconView)d).LoadOutline()));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(ParticleIconView), new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ParticleIconView)d)._brushes.Clear()));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<(MorphIcon, int), List<(double X, double Y)>> Outlines = [];
    private readonly ParticleMorph _morph;
    private readonly Dictionary<int, SolidColorBrush> _brushes = [];
    private double _progress;
    private double _target;
    private double _last;
    private bool _subscribed;
    private Action? _whenDone;

    public ParticleIconView()
        : this(56)
    {
    }

    public ParticleIconView(int dots)
    {
        IsHitTestVisible = false;
        _morph = new ParticleMorph(dots);
        LoadOutline();
        Unloaded += (_, _) => Unsubscribe();
    }

    public MorphIcon Icon { get => (MorphIcon)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>Keep twinkling while the icon is held (mode badges); off = a still icon once assembled.</summary>
    public bool Shimmer { get; set; }

    /// <summary>Seconds for the full flight orb → icon.</summary>
    public double Duration { get; set; } = 1.15;

    /// <summary>0 = orb, 1 = icon.</summary>
    public double Progress => _progress;

    public void Assemble(Action? done = null) => Go(1, done);

    public void Disperse(Action? done = null) => Go(0, done);

    /// <summary>Jump to a state without the flight (reduced motion, first show).</summary>
    public void Snap(double progress)
    {
        _progress = _target = progress;
        InvalidateVisual();
    }

    private void Go(double target, Action? done)
    {
        _target = target;
        _whenDone = done;
        if (Motion.Reduced)
        {
            Snap(target);
            Finish();
            return;
        }

        Subscribe();
    }

    private void Finish()
    {
        var done = _whenDone;
        _whenDone = null;
        done?.Invoke();
    }

    private void LoadOutline()
    {
        var key = (Icon, _morph.Count);
        if (!Outlines.TryGetValue(key, out var points))
        {
            points = Sample(MorphIcons.Path(Icon), _morph.Count);
            Outlines[key] = points;
        }

        _morph.SetOutline(points);
        InvalidateVisual();
    }

    /// <summary>Points spaced evenly along the icon's whole outline (all strokes), in 0…1 of its 24 px box.</summary>
    private static List<(double X, double Y)> Sample(string path, int count)
    {
        var flat = Geometry.Parse(path).GetFlattenedPathGeometry(0.02, ToleranceType.Absolute);
        var lines = new List<List<Point>>();
        foreach (var figure in flat.Figures)
        {
            var line = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case System.Windows.Media.PolyLineSegment pl:
                        line.AddRange(pl.Points);
                        break;
                    case System.Windows.Media.LineSegment l:
                        line.Add(l.Point);
                        break;
                }
            }

            if (figure.IsClosed)
            {
                line.Add(figure.StartPoint);
            }

            lines.Add(line);
        }

        var total = 0.0;
        foreach (var line in lines)
        {
            for (var i = 1; i < line.Count; i++)
            {
                total += (line[i] - line[i - 1]).Length;
            }
        }

        var result = new List<(double, double)>(count);
        if (total <= 0)
        {
            return result;
        }

        var step = total / count;
        var next = step / 2;
        var walked = 0.0;
        foreach (var line in lines)
        {
            for (var i = 1; i < line.Count && result.Count < count; i++)
            {
                var a = line[i - 1];
                var b = line[i];
                var length = (b - a).Length;
                while (next <= walked + length && result.Count < count)
                {
                    var t = length <= 0 ? 0 : (next - walked) / length;
                    result.Add(((a.X + ((b.X - a.X) * t)) / 24, (a.Y + ((b.Y - a.Y) * t)) / 24));
                    next += step;
                }

                walked += length;
            }
        }

        return result;
    }

    private void Subscribe()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _last = Clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            _subscribed = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        if (_progress != _target)
        {
            var step = dt / Duration;
            _progress = _target > _progress ? Math.Min(_target, _progress + step) : Math.Max(_target, _progress - step);
            if (_progress == _target)
            {
                Finish();
            }
        }
        else if (!(Shimmer && _progress > 0))
        {
            Unsubscribe();
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4 || (_progress <= 0 && _target <= 0))
        {
            return;
        }

        _morph.Compute(Clock.Elapsed.TotalSeconds, _progress, size);
        var ox = (ActualWidth - size) / 2;
        var oy = (ActualHeight - size) / 2;
        for (var i = 0; i < _morph.Count; i++)
        {
            dc.DrawEllipse(BrushFor(_morph.Alpha[i]), null, new Point(ox + _morph.X[i], oy + _morph.Y[i]), _morph.Radius[i], _morph.Radius[i]);
        }
    }

    private SolidColorBrush BrushFor(double alpha)
    {
        var key = (int)Math.Clamp(Math.Round(alpha * 31), 0, 31);
        if (!_brushes.TryGetValue(key, out var brush))
        {
            var c = Tint;
            brush = new SolidColorBrush(Color.FromArgb((byte)(key * 255 / 31), c.R, c.G, c.B));
            brush.Freeze();
            _brushes[key] = brush;
        }

        return brush;
    }
}
