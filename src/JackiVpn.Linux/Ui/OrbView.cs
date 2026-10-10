using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// A thinking orb (dotted, depth-shaded 3D loading animation) — the same engine as Windows and Android. All orbs
/// share one 30 fps clock that runs only while an orb is on screen; brushes are quantized and cached.
/// </summary>
public sealed class OrbView : Control
{
    public static readonly StyledProperty<OrbState> StateProperty = AvaloniaProperty.Register<OrbView, OrbState>(nameof(State), OrbState.Working);

    public static readonly StyledProperty<Color> TintProperty = AvaloniaProperty.Register<OrbView, Color>(nameof(Tint), Colors.White);

    private static readonly List<OrbView> Running = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private readonly OrbFrame _frame = new();
    private readonly Dictionary<int, IImmutableSolidColorBrush> _brushes = [];
    private readonly Dictionary<int, ImmutablePen> _pens = [];

    static OrbView()
    {
        AffectsRender<OrbView>(StateProperty, TintProperty);
    }

    public OrbView()
    {
        IsHitTestVisible = false;
    }

    public OrbState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public Color Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!Running.Contains(this))
        {
            Running.Add(this);
        }

        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Running.Remove(this) && Running.Count == 0)
        {
            _timer?.Stop();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TintProperty)
        {
            _brushes.Clear();
            _pens.Clear();
        }
    }

    private static void Tick()
    {
        foreach (var orb in Running)
        {
            if (orb.IsEffectivelyVisible)
            {
                orb.InvalidateVisual();
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 8)
        {
            return;
        }

        var preset = ThinkingOrbs.Resolve(State, size);
        ThinkingOrbs.Compute(preset, size, Clock.Elapsed.TotalSeconds * preset.Speed, _frame);
        var ox = (Bounds.Width - size) / 2;
        var oy = (Bounds.Height - size) / 2;
        foreach (var l in _frame.Lines)
        {
            context.DrawLine(PenFor(l.White, l.A, l.W), new Point(ox + l.X1, oy + l.Y1), new Point(ox + l.X2, oy + l.Y2));
        }

        foreach (var d in _frame.Dots)
        {
            context.DrawEllipse(BrushFor(d.White, d.A), null, new Point(ox + d.X, oy + d.Y), d.R, d.R);
        }
    }

    /// <summary>64 ink × 32 alpha levels.</summary>
    private static int KeyOf(double white, double alpha) =>
        ((ThinkingOrbs.Gray(white, dark: true) >> 2) << 5) | (int)Math.Clamp(Math.Round(alpha * 31), 0, 31);

    private IImmutableSolidColorBrush BrushFor(double white, double alpha)
    {
        var key = KeyOf(white, alpha);
        if (!_brushes.TryGetValue(key, out var brush))
        {
            var tint = Tint;
            var k = (key >> 5) / 63.0;
            brush = new ImmutableSolidColorBrush(Color.FromArgb((byte)((key & 31) * 255 / 31), (byte)(tint.R * k), (byte)(tint.G * k), (byte)(tint.B * k)));
            _brushes[key] = brush;
        }

        return brush;
    }

    private ImmutablePen PenFor(double white, double alpha, double width)
    {
        var key = (KeyOf(white, alpha) << 8) | Math.Clamp((int)Math.Round(width * 10), 0, 255);
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new ImmutablePen(BrushFor(white, alpha), width, lineCap: PenLineCap.Round);
            _pens[key] = pen;
        }

        return pen;
    }
}
