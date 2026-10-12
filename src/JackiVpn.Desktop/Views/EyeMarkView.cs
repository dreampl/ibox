using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>Where the mouse pointer is on screen (physical pixels), wherever it is.</summary>
internal static partial class ScreenCursor
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    public static bool TryGet(out Point point)
    {
        if (GetCursorPos(out var p))
        {
            point = new Point(p.X, p.Y);
            return true;
        }

        point = default;
        return false;
    }
}

/// <summary>
/// The IBOX "O" as a living eye (see <see cref="EyeMotion"/>). Asleep: grey, breathing by half a pixel. Seeking: a
/// "connecting" thinking orb inside the O while it tries to reach the server. Awake: the frame lights up and the
/// blue pupil looks around, blinks and — when given a point — follows it. Only transforms change per frame; the
/// clock runs while loaded and visible, slowing down when nothing moves.
/// </summary>
public sealed class EyeMarkView : Viewbox
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(EyeState), typeof(EyeMarkView), new PropertyMetadata(EyeState.Asleep, (d, _) => ((EyeMarkView)d).ApplyState(animate: true)));

    public static readonly DependencyProperty AwakeFrameProperty = DependencyProperty.Register(
        nameof(AwakeFrame), typeof(Color), typeof(EyeMarkView), new PropertyMetadata(Colors.White, (d, _) => ((EyeMarkView)d).ApplyState(animate: true)));

    private static readonly Color SleepFrame = Color.FromRgb(0x5E, 0x64, 0x72);
    private static readonly Color SleepPupil = Color.FromRgb(0x4A, 0x55, 0x68);
    private static readonly Color Blue = Color.FromRgb(0x3D, 0x9B, 0xFF);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Path _frame;
    private readonly Rectangle _pupil;
    private readonly TranslateTransform _pupilMove = new();
    private readonly ScaleTransform _pupilBlink = new(1, 1, EyeMark.PupilSize / 2, EyeMark.PupilSize / 2);
    private readonly TranslateTransform _drift = new();
    private readonly SolidColorBrush _frameBrush = new(SleepFrame);
    private readonly SolidColorBrush _pupilBrush = new(SleepPupil);
    private readonly DropShadowEffect _glow = new() { Color = Blue, BlurRadius = 14, ShadowDepth = 0, Opacity = 0 };
    private readonly OrbView _orb;
    private double _last;
    private double _lastDraw;

    public EyeMarkView()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        _frame = new Path { Data = Geometry.Parse("F1 " + EyeMark.FramePath), Fill = _frameBrush };
        _pupil = new Rectangle
        {
            Width = EyeMark.PupilSize,
            Height = EyeMark.PupilSize,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = _pupilBrush,
            Effect = _glow,
            RenderTransform = new TransformGroup { Children = { _pupilBlink, _pupilMove } },
        };
        _orb = new OrbView
        {
            State = OrbState.Connecting,
            Tint = Blue,
            Width = EyeMark.WindowWidth + 10,
            Height = EyeMark.WindowHeight + 10,
            Opacity = 0,
            IsRunning = false,
        };
        Canvas.SetLeft(_orb, EyeMark.WindowLeft - 5);
        Canvas.SetTop(_orb, EyeMark.WindowTop - 5);
        Child = new Canvas
        {
            Width = EyeMark.Width,
            Height = EyeMark.Height,
            RenderTransform = _drift,
            Children = { _frame, _pupil, _orb },
        };
        Eye = new EyeMotion();
        _clock = new ViewClock(this, IdleInterval, Tick);
        Loaded += (_, _) =>
        {
            ApplyState(animate: false);
            _last = Clock.Elapsed.TotalSeconds;
            Draw();
        };
    }

    /// <summary>
    /// Tick spacing while nothing moves: the sleeping breath (half a pixel over seconds) and noticing the pointer or
    /// the next blink need no more; real motion (glances, blinks, following the pointer) is drawn every frame.
    /// </summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(125);

    private readonly ViewClock _clock;

    public EyeMotion Eye { get; }

    public EyeState State { get => (EyeState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>Colour of the lit frame (white by default; tinted on coloured buttons).</summary>
    public Color AwakeFrame { get => (Color)GetValue(AwakeFrameProperty); set => SetValue(AwakeFrameProperty, value); }

    /// <summary>Optional: called every frame before the motion steps (e.g. to feed the pointer position).</summary>
    public Action<EyeMarkView>? BeforeFrame { get; set; }

    /// <summary>Turns a point on screen (physical pixels) into a look direction and looks there.</summary>
    public void LookAtScreen(Point screen, double reachX = 320, double reachY = 220)
    {
        if (PresentationSource.FromVisual(this) is null)
        {
            return;
        }

        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        Eye.LookAt((screen.X - center.X) / reachX, (screen.Y - center.Y) / reachY);
    }

    private void ApplyState(bool animate)
    {
        Eye.State = State;
        var awake = State == EyeState.Awake;
        var seeking = State == EyeState.Seeking;
        var frame = awake ? AwakeFrame : seeking ? Color.FromRgb(0xC9, 0xCE, 0xD8) : SleepFrame;
        var pupil = awake ? Blue : SleepPupil;
        var duration = animate && !Motion.Reduced ? TimeSpan.FromMilliseconds(420) : TimeSpan.Zero;
        _frameBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(frame, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        _pupilBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(pupil, duration));
        _glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(awake ? 0.9 : 0, duration));
        _pupil.BeginAnimation(OpacityProperty, new DoubleAnimation(seeking ? 0 : 1, duration));
        _orb.IsRunning = seeking;
        _orb.BeginAnimation(OpacityProperty, new DoubleAnimation(seeking ? 1 : 0, duration));
        if (awake && animate)
        {
            Eye.Poke();
            _clock.Wake();
        }
    }

    /// <summary>The motion moved (a poke from outside): draw every frame until it settles.</summary>
    public void Wake() => _clock.Wake();

    /// <summary>One step; returns whether the eye is visibly moving.</summary>
    private bool Tick()
    {
        var now = Clock.Elapsed.TotalSeconds;
        if (Eye.IsMoving && now - _lastDraw < 1 / 62.0)
        {
            return true; // high-refresh monitors: at most ~60 steps per second
        }

        BeforeFrame?.Invoke(this);

        // Real time in small steps (the springs need them); after a long pause, continue instead of jumping.
        var dt = Math.Min(now - _last, 0.5);
        while (dt > 0)
        {
            var step = Math.Min(dt, 0.05);
            Eye.Step(step);
            dt -= step;
        }

        _last = now;
        _lastDraw = now;
        Draw();

        // Idle tick rate: asleep or resting, the breath and the rare blink need 4 ticks a second; awake, 8 (to
        // notice the pointer and the next glance in time).
        _clock.IdleInterval = State != EyeState.Awake || Eye.Resting ? SlowIdleInterval : IdleInterval;
        return Eye.IsMoving;
    }

    private static readonly TimeSpan SlowIdleInterval = TimeSpan.FromMilliseconds(250);

    private void Draw()
    {
        var (x, y) = EyeMark.PupilAt(Eye.LookX, Eye.LookY);
        _pupilMove.X = x;
        _pupilMove.Y = y;
        _pupilBlink.ScaleY = Eye.Openness;
        // The drift is in screen pixels: convert to mark units so it stays half a pixel at any size.
        var scale = ActualWidth > 0 ? ActualWidth / EyeMark.Width : 1;
        _drift.X = Eye.DriftX / scale;
        _drift.Y = Eye.DriftY / scale;
    }
}
