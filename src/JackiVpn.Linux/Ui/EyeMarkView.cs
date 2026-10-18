using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// The IBOX "O" as a living eye, as on Windows (motion shared in <see cref="EyeMotion"/>): asleep it is grey and
/// breathes by half a pixel, seeking it shows the thinking orb inside the O, awake the frame lights up and the blue
/// pupil glances around, blinks and follows the pointer. Drawn directly (no child controls but the orb); the clock
/// runs at the display rate only while something moves and slows to a few ticks a second otherwise.
/// </summary>
public sealed class EyeMarkView : Panel
{
    private static readonly Geometry Frame = Geometry.Parse("F1 " + EyeMark.FramePath);
    private static readonly Color SleepFrame = Color.FromRgb(0x5E, 0x64, 0x72);
    private static readonly Color SeekFrame = Color.FromRgb(0xC9, 0xCE, 0xD8);
    private static readonly Color SleepPupil = Color.FromRgb(0x4A, 0x55, 0x68);
    private static readonly Color Blue = Color.FromRgb(0x3D, 0x9B, 0xFF);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan SlowIdle = TimeSpan.FromMilliseconds(250);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly OrbView _orb = new() { State = OrbState.Connecting, Tint = Blue, Opacity = 0, IsHitTestVisible = false };
    private EyeState _state = EyeState.Asleep;
    private Color _frameFrom = SleepFrame;
    private Color _pupilFrom = SleepPupil;
    private double _fade = 1; // 0…1 progress of the colour change
    private double _last;
    private readonly EyeCanvas _canvas;
    private (double X, double Y, double Open, double Dx, double Dy) _drawn;

    public EyeMarkView()
    {
        IsHitTestVisible = false;
        _canvas = new EyeCanvas(this);
        Children.Add(_canvas);
        Children.Add(_orb);
        _timer = new DispatcherTimer(Idle, DispatcherPriority.Render, (_, _) => Tick());
        AttachedToVisualTree += (_, _) =>
        {
            _last = _clock.Elapsed.TotalSeconds;
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                _timer.IsEnabled = IsVisible && TopLevel.GetTopLevel(this) is not null;
            }
        };
    }

    public EyeMotion Eye { get; } = new();

    /// <summary>Colour of the lit frame (white by default; tinted on light buttons).</summary>
    public Color AwakeFrame { get; set; } = Colors.White;

    public EyeState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            (_frameFrom, _pupilFrom) = (FrameColor(), PupilColor());
            _fade = 0;
            _state = value;
            Eye.State = value;
            if (value == EyeState.Awake)
            {
                Eye.Poke();
            }

            Wake();
        }
    }

    /// <summary>Calm while nobody looks closely (a faded notch): no glancing, a rare slow blink.</summary>
    public bool Resting
    {
        get => Eye.Resting;
        set => Eye.Resting = value;
    }

    /// <summary>Looks towards a point in this control's coordinates (the pointer), with the given reach in pixels.</summary>
    public void LookAt(Point local, double reachX = 320, double reachY = 220)
    {
        if (_state != EyeState.Awake || Eye.Resting)
        {
            return;
        }

        Eye.LookAt((local.X - (Bounds.Width / 2)) / reachX, (local.Y - (Bounds.Height / 2)) / reachY);
        Wake();
    }

    /// <summary>Something moved it from outside: draw at the display rate until it settles.</summary>
    public void Wake()
    {
        if (_timer.Interval != Fast)
        {
            _timer.Interval = Fast;
        }

        _canvas.InvalidateVisual();
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _last, 0.5);
        _last = now;
        while (dt > 0)
        {
            var step = Math.Min(dt, 0.05);
            Eye.Step(step);
            dt -= step;
        }

        var fading = _fade < 1;
        if (fading)
        {
            _fade = Math.Min(1, _fade + (Fast.TotalSeconds / 0.42));
        }

        _orb.Opacity = _state == EyeState.Seeking ? Math.Max(_orb.Opacity, _fade) : Math.Min(_orb.Opacity, 1 - _fade);
        _orb.IsVisible = _orb.Opacity > 0.01;
        var (x, y) = EyeMark.PupilAt(Eye.LookX, Eye.LookY);
        var now2 = (x, y, Eye.Openness, Eye.DriftX, Eye.DriftY);
        if (fading || Math.Abs(now2.x - _drawn.X) + Math.Abs(now2.y - _drawn.Y) + Math.Abs(now2.Openness - _drawn.Open) > 0.01
            || Math.Abs(now2.DriftX - _drawn.Dx) + Math.Abs(now2.DriftY - _drawn.Dy) > 0.02)
        {
            _drawn = now2;
            _canvas.InvalidateVisual();
        }

        var interval = Eye.IsMoving || fading ? Fast : _state != EyeState.Awake || Eye.Resting ? SlowIdle : Idle;
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }
    }

    private Color FrameColor() => _state switch
    {
        EyeState.Awake => AwakeFrame,
        EyeState.Seeking => SeekFrame,
        _ => SleepFrame,
    };

    private Color PupilColor() => _state == EyeState.Awake ? Blue : SleepPupil;

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + ((b.A - a.A) * t)), (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

    private (double Scale, double X, double Y) Fit()
    {
        var s = Math.Min(Bounds.Width / EyeMark.Width, Bounds.Height / EyeMark.Height);
        return (s, (Bounds.Width - (EyeMark.Width * s)) / 2, (Bounds.Height - (EyeMark.Height * s)) / 2);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = Math.Min(finalSize.Width / EyeMark.Width, finalSize.Height / EyeMark.Height);
        var ox = (finalSize.Width - (EyeMark.Width * s)) / 2;
        var oy = (finalSize.Height - (EyeMark.Height * s)) / 2;
        _canvas.Arrange(new Rect(finalSize));
        _orb.Arrange(new Rect(ox + ((EyeMark.WindowLeft - 5) * s), oy + ((EyeMark.WindowTop - 5) * s), (EyeMark.WindowWidth + 10) * s, (EyeMark.WindowHeight + 10) * s));
        return finalSize;
    }

    private void Draw(DrawingContext context)
    {
        var (s, ox, oy) = Fit();
        if (s <= 0)
        {
            return;
        }

        var t = 1 - Math.Pow(1 - _fade, 3);
        var frame = Mix(_frameFrom, FrameColor(), t);
        var pupil = Mix(_pupilFrom, PupilColor(), t);
        var pupilAlpha = _state == EyeState.Seeking ? 1 - t : _frameFrom == SeekFrame ? t : 1;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox + Eye.DriftX, oy + Eye.DriftY)))
        {
            context.DrawGeometry(new SolidColorBrush(frame), null, Frame);
            if (pupilAlpha <= 0.01)
            {
                return;
            }

            var (x, y) = EyeMark.PupilAt(Eye.LookX, Eye.LookY);
            var h = EyeMark.PupilSize * Eye.Openness;
            var rect = new Rect(x, y + ((EyeMark.PupilSize - h) / 2), EyeMark.PupilSize, h);
            if (_state == EyeState.Awake)
            {
                // Soft glow (two wider, fainter squares: cheaper than a blur effect).
                context.DrawRectangle(new SolidColorBrush(Blue, 0.10 * t * pupilAlpha), null, rect.Inflate(7), 6, 6);
                context.DrawRectangle(new SolidColorBrush(Blue, 0.18 * t * pupilAlpha), null, rect.Inflate(3.5), 4, 4);
            }

            context.DrawRectangle(new SolidColorBrush(pupil, pupilAlpha), null, rect, 1.5, 1.5);
        }
    }

    /// <summary>Draws the frame and the pupil (a panel cannot draw itself).</summary>
    private sealed class EyeCanvas(EyeMarkView owner) : Control
    {
        public override void Render(DrawingContext context) => owner.Draw(context);
    }
}
