using JackiVpn.Domain.Visuals;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// A thinking orb (dotted, depth-shaded 3D animation) for loading and waiting states. All orbs share the frame
/// clock and one time base (so they stay in phase); an orb draws only while it is loaded, visible and the app is
/// in the foreground, and shows one still frame with "Remove animations".
/// </summary>
public sealed class OrbView : GraphicsView
{
    public static readonly BindableProperty StateProperty = BindableProperty.Create(nameof(State), typeof(OrbState), typeof(OrbView), OrbState.Working,
        propertyChanged: (b, _, _) => ((OrbView)b).Invalidate());

    public static readonly BindableProperty TintProperty = BindableProperty.Create(nameof(Tint), typeof(Color), typeof(OrbView), Colors.White,
        propertyChanged: (b, _, _) => ((OrbView)b).Invalidate());

    public static readonly BindableProperty IsRunningProperty = BindableProperty.Create(nameof(IsRunning), typeof(bool), typeof(OrbView), true,
        propertyChanged: (b, _, _) => ((OrbView)b).UpdateClock());

    private static readonly long Epoch = Environment.TickCount64;
    private readonly Func<double, bool> _frame;
    private bool _subscribed;

    /// <summary>False while the app is in the background (set by the platform).</summary>
    public static bool AppVisible { get; set; } = true;

    public OrbView()
    {
        Drawable = new OrbDrawable(this);
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        _frame = _ =>
        {
            if (!ShouldRun)
            {
                _subscribed = false;
                return false;
            }

            Invalidate();
            return true;
        };
        Loaded += (_, _) => UpdateClock();
        Unloaded += (_, _) => _subscribed = false;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsVisible) or nameof(Opacity))
            {
                UpdateClock();
            }
        };
    }

    public OrbState State
    {
        get => (OrbState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Ink of near dots (far dots fade toward the background).</summary>
    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    private bool ShouldRun => IsLoaded && IsVisible && Opacity > 0.01 && IsRunning && AppVisible && !Motion.Reduced;

    /// <summary>Call when the app returns to the foreground.</summary>
    public void Resume() => UpdateClock();

    private void UpdateClock()
    {
        if (ShouldRun && !_subscribed)
        {
            _subscribed = true;
            FrameTicker.Add(_frame);
        }

        Invalidate();
    }

    internal double Time(OrbPreset preset) =>
        Motion.Reduced || !IsRunning ? ThinkingOrbs.StaticTime : (Environment.TickCount64 - Epoch) / 1000.0 * preset.Speed;

    private sealed class OrbDrawable(OrbView owner) : IDrawable
    {
        private readonly OrbFrame _frame = new();

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var size = Math.Min(dirtyRect.Width, dirtyRect.Height);
            if (size < 8)
            {
                return;
            }

            var preset = ThinkingOrbs.Resolve(owner.State, size);
            ThinkingOrbs.Compute(preset, size, owner.Time(preset), _frame);
            var ox = dirtyRect.X + ((dirtyRect.Width - size) / 2);
            var oy = dirtyRect.Y + ((dirtyRect.Height - size) / 2);
            var tint = owner.Tint;
            foreach (var l in _frame.Lines)
            {
                var k = ThinkingOrbs.Gray(l.White, dark: true) / 255f;
                canvas.StrokeColor = new Color(tint.Red * k, tint.Green * k, tint.Blue * k, (float)l.A);
                canvas.StrokeSize = (float)l.W;
                canvas.DrawLine((float)(ox + l.X1), (float)(oy + l.Y1), (float)(ox + l.X2), (float)(oy + l.Y2));
            }

            foreach (var d in _frame.Dots)
            {
                var k = ThinkingOrbs.Gray(d.White, dark: true) / 255f;
                canvas.FillColor = new Color(tint.Red * k, tint.Green * k, tint.Blue * k, (float)d.A);
                canvas.FillCircle((float)(ox + d.X), (float)(oy + d.Y), (float)d.R);
            }
        }
    }
}
