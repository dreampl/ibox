using JackiVpn.Domain.Visuals;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// The IBOX "O" as a living eye (shared <see cref="EyeMotion"/>): asleep it is grey and breathes by half a pixel;
/// seeking, a "connecting" thinking orb turns inside the O; awake the frame lights up and the blue pupil looks
/// around and blinks. Draws on the shared frame clock only while visible and the app is in front; ~12 fps when
/// nothing but the slow breath moves.
/// </summary>
public sealed class EyeMarkView : GraphicsView
{
    public static readonly BindableProperty StateProperty = BindableProperty.Create(nameof(State), typeof(EyeState), typeof(EyeMarkView), EyeState.Asleep,
        propertyChanged: (b, _, n) =>
        {
            var v = (EyeMarkView)b;
            v._eye.State = (EyeState)n;
            if ((EyeState)n == EyeState.Awake)
            {
                v._eye.Poke();
            }

            v.UpdateClock();
        });

    public static readonly BindableProperty AwakeFrameProperty = BindableProperty.Create(nameof(AwakeFrame), typeof(Color), typeof(EyeMarkView), Colors.White,
        propertyChanged: (b, _, _) => ((EyeMarkView)b).Invalidate());

    /// <summary>Colours of the sleeping (off) eye — set to tones of the surface it sits on, so it belongs to it.</summary>
    public static readonly BindableProperty AsleepFrameProperty = BindableProperty.Create(nameof(AsleepFrame), typeof(Color), typeof(EyeMarkView), Color.FromArgb("#5E6472"),
        propertyChanged: (b, _, _) => ((EyeMarkView)b).Invalidate());

    public static readonly BindableProperty AsleepPupilProperty = BindableProperty.Create(nameof(AsleepPupil), typeof(Color), typeof(EyeMarkView), Color.FromArgb("#4A5568"),
        propertyChanged: (b, _, _) => ((EyeMarkView)b).Invalidate());

    /// <summary>Static mark (no motion) — e.g. a logo in a header.</summary>
    public static readonly BindableProperty AnimatedProperty = BindableProperty.Create(nameof(Animated), typeof(bool), typeof(EyeMarkView), true,
        propertyChanged: (b, _, _) => ((EyeMarkView)b).UpdateClock());

    private static readonly long Epoch = Environment.TickCount64;
    private readonly EyeMotion _eye = new();
    private readonly Func<double, bool> _frame;
    private readonly OrbFrame _orbFrame = new();
    private bool _subscribed;
    private double _sinceDraw;

    public EyeMarkView()
    {
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        Drawable = new Painter(this);
        _frame = dt =>
        {
            if (!ShouldRun)
            {
                _subscribed = false;
                return false;
            }

            _eye.Step(dt);
            _sinceDraw += dt;
            // Full rate while something moves (and for the orb); a gentle 12 fps for the sleeping breath.
            if (_eye.IsMoving || State == EyeState.Seeking || _sinceDraw >= 1 / 12.0)
            {
                _sinceDraw = 0;
                Invalidate();
            }

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

    public EyeState State { get => (EyeState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public Color AwakeFrame { get => (Color)GetValue(AwakeFrameProperty); set => SetValue(AwakeFrameProperty, value); }

    public bool Animated { get => (bool)GetValue(AnimatedProperty); set => SetValue(AnimatedProperty, value); }

    public Color AsleepFrame { get => (Color)GetValue(AsleepFrameProperty); set => SetValue(AsleepFrameProperty, value); }

    public Color AsleepPupil { get => (Color)GetValue(AsleepPupilProperty); set => SetValue(AsleepPupilProperty, value); }

    public EyeMotion Eye => _eye;

    private bool ShouldRun => Animated && IsLoaded && IsVisible && Opacity > 0.01 && OrbView.AppVisible && !Motion.Reduced;

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

    private sealed class Painter(EyeMarkView owner) : IDrawable
    {
        private static readonly Color SeekFrame = Color.FromArgb("#C9CED8");
        private static readonly Color Blue = Color.FromArgb("#3D9BFF");

        public void Draw(ICanvas canvas, RectF rect)
        {
            var scale = (float)Math.Min(rect.Width / EyeMark.Width, rect.Height / EyeMark.Height);
            if (scale <= 0)
            {
                return;
            }

            var eye = owner._eye;
            var ox = rect.X + ((rect.Width - (float)(EyeMark.Width * scale)) / 2) + (float)eye.DriftX;
            var oy = rect.Y + ((rect.Height - (float)(EyeMark.Height * scale)) / 2) + (float)eye.DriftY;
            canvas.SaveState();
            canvas.Translate(ox, oy);
            canvas.Scale(scale, scale);

            var state = owner.State;
            canvas.FillColor = state switch { EyeState.Awake => owner.AwakeFrame, EyeState.Seeking => SeekFrame, _ => owner.AsleepFrame };
            var frame = new PathF();
            frame.MoveTo(30, 0);
            frame.LineTo(126, 0);
            frame.LineTo(126, 127);
            frame.LineTo(8, 127);
            frame.QuadTo(0, 127, 0, 119);
            frame.LineTo(0, 30);
            frame.LineTo(27, 30);
            frame.LineTo(27, 100);
            frame.LineTo(99, 100);
            frame.LineTo(99, 27);
            frame.LineTo(30, 27);
            frame.Close();
            if (state == EyeState.Awake && owner.Animated)
            {
                // Lit O on a bright button: a soft shadow keeps its edge readable.
                canvas.SetShadow(new SizeF(0, 2), 8, Colors.Black.WithAlpha(0.35f));
            }

            canvas.FillPath(frame);
            canvas.SetShadow(SizeF.Zero, 0, Colors.Transparent);

            if (state == EyeState.Seeking)
            {
                // A "connecting" orb inside the O while it tries to reach the server.
                DrawOrb(canvas, (float)(EyeMark.WindowLeft - 4), (float)(EyeMark.WindowTop - 4), (float)(EyeMark.WindowWidth + 8), scale);
            }
            else
            {
                var (px, py) = EyeMark.PupilAt(eye.LookX, eye.LookY);
                var h = (float)(EyeMark.PupilSize * eye.Openness);
                var y = (float)(py + ((EyeMark.PupilSize - h) / 2));
                if (state == EyeState.Awake)
                {
                    canvas.SetShadow(new SizeF(0, 0), 10, Blue.WithAlpha(0.8f));
                }

                canvas.FillColor = state == EyeState.Awake ? Blue : owner.AsleepPupil;
                canvas.FillRoundedRectangle((float)px, y, (float)EyeMark.PupilSize, h, 1.5f);
            }

            canvas.RestoreState();
        }

        private void DrawOrb(ICanvas canvas, float x, float y, float size, float scale)
        {
            // Computed at the on-screen size so dots keep their pixel size, drawn in mark units.
            var pixels = size * scale;
            var preset = ThinkingOrbs.Resolve(OrbState.Connecting, Math.Max(pixels, 48));
            var t = Motion.Reduced ? ThinkingOrbs.StaticTime : (Environment.TickCount64 - Epoch) / 1000.0 * preset.Speed;
            var computeSize = Math.Max(pixels, 48);
            ThinkingOrbs.Compute(preset, computeSize, t, owner._orbFrame);
            var k = size / (float)computeSize;
            foreach (var l in owner._orbFrame.Lines)
            {
                var g = (0.35f + (0.65f * (ThinkingOrbs.Gray(l.White, dark: true) / 255f))) * (float)l.A;
                canvas.StrokeColor = Blue.WithAlpha(g);
                canvas.StrokeSize = (float)l.W * k;
                canvas.DrawLine(x + ((float)l.X1 * k), y + ((float)l.Y1 * k), x + ((float)l.X2 * k), y + ((float)l.Y2 * k));
            }

            foreach (var d in owner._orbFrame.Dots)
            {
                var g = (0.35f + (0.65f * (ThinkingOrbs.Gray(d.White, dark: true) / 255f))) * (float)d.A;
                canvas.FillColor = Color.FromRgb(0.6f + (0.4f * g), 0.8f + (0.2f * g), 1f).WithAlpha(g);
                canvas.FillCircle(x + ((float)d.X * k), y + ((float)d.Y * k), (float)d.R * k);
            }
        }
    }
}
