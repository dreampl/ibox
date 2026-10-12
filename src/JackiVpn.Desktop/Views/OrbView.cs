using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// A thinking orb (dotted, depth-shaded 3D loading animation). All orbs share one clock so they stay in phase,
/// draw only while visible, and fall back to one static frame with reduced motion.
/// </summary>
public sealed class OrbView : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(OrbState), typeof(OrbView), new FrameworkPropertyMetadata(OrbState.Working, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(OrbView), new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((OrbView)d)._brushes.Clear()));

    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed), typeof(double), typeof(OrbView), new PropertyMetadata(1.0));

    /// <summary>
    /// Draw with the detail of an orb this many pixels wide, scaled down to fit (more dots and lines than the small
    /// presets have); 0 = the actual size.
    /// </summary>
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(double), typeof(OrbView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((OrbView)d)._pens.Clear()));

    /// <summary>Lifts the darkest ink to this level (0…1) so far dots stay visible on black backgrounds.</summary>
    public static readonly DependencyProperty MinInkProperty = DependencyProperty.Register(
        nameof(MinInk), typeof(double), typeof(OrbView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => { ((OrbView)d)._brushes.Clear(); ((OrbView)d)._pens.Clear(); }));

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(OrbView), new PropertyMetadata(true, (d, _) => ((OrbView)d).UpdateSubscription()));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly OrbFrame _frame = new();
    private readonly Dictionary<int, SolidColorBrush> _brushes = [];
    private readonly Dictionary<int, Pen> _pens = [];
    private readonly ViewClock _clock;
    private TimeSpan _lastTick;

    public OrbView()
    {
        IsHitTestVisible = false;

        // An orb always moves while it runs: drawn every frame (at most ~60 fps), never while off screen.
        _clock = new ViewClock(this, TimeSpan.FromMilliseconds(16), () =>
        {
            var now = Clock.Elapsed;
            if (now - _lastTick >= TimeSpan.FromMilliseconds(15))
            {
                _lastTick = now;
                InvalidateVisual();
            }

            return true;
        });
    }

    public OrbState State { get => (OrbState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>Ink colour of near dots (far dots fade toward transparent dark).</summary>
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    public double Speed { get => (double)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

    public double Detail { get => (double)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public double MinInk { get => (double)GetValue(MinInkProperty); set => SetValue(MinInkProperty, value); }

    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    private void UpdateSubscription()
    {
        _clock.Enabled = IsRunning;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8)
        {
            return;
        }

        // High detail: compute the orb at a larger size and draw it scaled down (vector, so it stays crisp).
        var computeSize = Detail > size ? Detail : size;
        var preset = ThinkingOrbs.Resolve(State, computeSize);
        var t = Motion.Reduced || !IsRunning ? ThinkingOrbs.StaticTime : Clock.Elapsed.TotalSeconds * preset.Speed * Speed;
        ThinkingOrbs.Compute(preset, computeSize, t, _frame);
        var ox = (ActualWidth - size) / 2;
        var oy = (ActualHeight - size) / 2;
        var scaled = computeSize > size;
        if (scaled)
        {
            dc.PushTransform(new MatrixTransform(size / computeSize, 0, 0, size / computeSize, ox, oy));
            ox = oy = 0;
        }

        foreach (var l in _frame.Lines)
        {
            dc.DrawLine(PenFor(l.White, l.A, l.W), new Point(ox + l.X1, oy + l.Y1), new Point(ox + l.X2, oy + l.Y2));
        }

        foreach (var d in _frame.Dots)
        {
            dc.DrawEllipse(BrushFor(d.White, d.A), null, new Point(ox + d.X, oy + d.Y), d.R, d.R);
        }

        if (scaled)
        {
            dc.Pop();
        }
    }

    /// <summary>Brushes quantized to 64 ink × 32 alpha levels, created once and frozen.</summary>
    private static int KeyOf(double white, double alpha) =>
        ((ThinkingOrbs.Gray(white, dark: true) >> 2) << 5) | (int)Math.Clamp(Math.Round(alpha * 31), 0, 31);

    private SolidColorBrush BrushFor(double white, double alpha)
    {
        var key = KeyOf(white, alpha);
        var g = key >> 5;
        var a = key & 31;
        if (!_brushes.TryGetValue(key, out var brush))
        {
            var tint = Tint;
            var k = MinInk + ((1 - MinInk) * (g / 63.0));
            brush = new SolidColorBrush(Color.FromArgb((byte)(a * 255 / 31), (byte)(tint.R * k), (byte)(tint.G * k), (byte)(tint.B * k)));
            brush.Freeze();
            _brushes[key] = brush;
        }

        return brush;
    }

    private Pen PenFor(double white, double alpha, double width)
    {
        var key = (KeyOf(white, alpha) << 8) | Math.Clamp((int)Math.Round(width * 10), 0, 255);
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(BrushFor(white, alpha), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            _pens[key] = pen;
        }

        return pen;
    }
}
