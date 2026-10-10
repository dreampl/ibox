using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The hero connect button. State changes cross-fade per-state discs (280 ms); connecting shows a rotating
/// gradient arc and ripples; connected gets a gentle breathing halo and a short "pop". Hover/press scale in
/// 180/120 ms. With reduced motion every transition is instant, spinner and ripples become a static ring.
/// </summary>
public partial class ConnectButton : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ConnectionState), typeof(ConnectButton), new PropertyMetadata(ConnectionState.Disconnected, (d, e) => ((ConnectButton)d).UpdateVisuals((ConnectionState)e.OldValue)));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(ConnectButton), new PropertyMetadata(false, (d, _) => ((ConnectButton)d).UpdateVisuals(null)));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(VisualMode), typeof(ConnectButton), new PropertyMetadata(VisualMode.Normal, (d, e) => ((ConnectButton)d).OnModeChanged()));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(ConnectButton), new PropertyMetadata(null, (d, e) => ((ConnectButton)d).Root.Command = (ICommand?)e.NewValue));

    private static readonly TimeSpan StateDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(120);

    public ConnectButton()
    {
        InitializeComponent();
        Root.MouseEnter += (_, _) => ScaleTo(1.03, HoverDuration);
        Root.MouseLeave += (_, _) => ScaleTo(1.0, HoverDuration);
        Root.PreviewMouseLeftButtonDown += (_, _) => ScaleTo(0.96, PressDuration);
        Root.PreviewMouseLeftButtonUp += (_, _) => ScaleTo(Root.IsMouseOver ? 1.03 : 1.0, PressDuration);
        Root.MouseEnter += (_, _) => Eye.Eye.Poke();
        // Awake, the eye follows the pointer anywhere on screen; when the pointer rests it looks around by itself.
        Eye.BeforeFrame = eye =>
        {
            if (State == ConnectionState.Connected && ScreenCursor.TryGet(out var p) && p != _lastCursor)
            {
                _lastCursor = p;
                eye.LookAtScreen(p);
            }
        };
        OnDisc.Fill = DiscBrush(VisualMode.Normal);
        Loaded += (_, _) => UpdateVisuals(null);
    }

    private Point _lastCursor;
    private VisualMode _shownMode = VisualMode.Normal;

    public ConnectionState State
    {
        get => (ConnectionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Colour of the connected button: teal, gaming magenta-red, streaming blue, eco light green, work medium blue.</summary>
    public VisualMode Mode
    {
        get => (VisualMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static LinearGradientBrush DiscBrush(VisualMode mode)
    {
        var (light, main, dark) = ModePalette.Colors(mode);
        var brush = new LinearGradientBrush(new GradientStopCollection { new(Argb(light), 0), new(Argb(main), 0.5), new(Argb(dark), 1) }, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Argb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private void OnModeChanged()
    {
        if (Mode != _shownMode)
        {
            _shownMode = Mode;
            if (State == ConnectionState.Connected && !Reduced)
            {
                // Cross-fade the disc into the new colour, then hand it over to the main disc.
                OnDiscNext.Fill = DiscBrush(Mode);
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                fade.Completed += (_, _) =>
                {
                    OnDisc.Fill = OnDiscNext.Fill;
                    OnDiscNext.BeginAnimation(OpacityProperty, null);
                    OnDiscNext.Opacity = 0;
                };
                OnDiscNext.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                OnDisc.Fill = DiscBrush(Mode);
            }
        }

        UpdateVisuals(State);
        if (State == ConnectionState.Connected && !Reduced)
        {
            // Stretch, then settle: the colour change is noticed.
            var sx = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), new ElasticEase { Oscillations = 2, Springiness = 6, EasingMode = EasingMode.EaseOut }));
            var sy = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), new ElasticEase { Oscillations = 2, Springiness = 6, EasingMode = EasingMode.EaseOut }));
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private bool Reduced => ReduceMotion || Motion.Reduced;

    private bool Busy => State is not (ConnectionState.Disconnected or ConnectionState.Connected or ConnectionState.Faulted);

    private void UpdateVisuals(ConnectionState? previous)
    {
        var connected = State == ConnectionState.Connected;
        var target = State switch
        {
            ConnectionState.Connected => OnDisc,
            ConnectionState.Faulted => FailDisc,
            ConnectionState.Disconnected => IdleDisc,
            _ => BusyDisc,
        };
        foreach (var disc in new[] { IdleDisc, BusyDisc, OnDisc, FailDisc })
        {
            Fade(disc, ReferenceEquals(disc, target) ? 1 : 0);
        }

        if (!connected)
        {
            OnDiscNext.BeginAnimation(OpacityProperty, null);
            OnDiscNext.Opacity = 0;
        }

        Eye.State = connected ? EyeState.Awake : Busy ? EyeState.Seeking : EyeState.Asleep;
        Caption.Text = State switch
        {
            ConnectionState.Connected => ModePalette.Label(Mode),
            ConnectionState.Faulted => "RETRY",
            ConnectionState.Disconnected => "CONNECT",
            ConnectionState.Disconnecting => "STOPPING",
            _ => "CONNECTING",
        };

        // Halo: breathing in the mode colour when connected, faint red when faulted, off otherwise.
        var (light, main, _) = ModePalette.Colors(Mode);
        if (connected)
        {
            Halo.Fill = new SolidColorBrush(Argb(main));
            OnRing.Stroke = new LinearGradientBrush(Argb(light), Argb(main), 45);
        }
        else
        {
            Halo.SetResourceReference(Shape.FillProperty, "B.Danger");
        }

        if (State == ConnectionState.Connected)
        {
            if (Reduced)
            {
                Halo.BeginAnimation(OpacityProperty, null);
                Halo.Opacity = 0.4;
            }
            else
            {
                Motion.Breathe(Halo, 0.28, 0.55, TimeSpan.FromSeconds(2.4));
            }
        }
        else
        {
            Fade(Halo, State == ConnectionState.Faulted ? 0.3 : 0);
        }

        Fade(OnRing, connected ? 1 : 0);

        if (Busy && !Reduced)
        {
            Fade(Spinner, 1);
            Fade(StaticRing, 0);
            Spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            StartRipples();
        }
        else
        {
            Spin.BeginAnimation(RotateTransform.AngleProperty, null);
            Fade(Spinner, 0);
            Fade(StaticRing, Busy ? 1 : 0);
            StopRipples();
        }

        if (State == ConnectionState.Connected && previous is not null && previous != ConnectionState.Connected && !Reduced)
        {
            // Satisfying confirmation: a quick overshoot pop.
            var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.07, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        AutomationProperties.SetHelpText(Root, State.ToString());
    }

    private void StartRipples()
    {
        var i = 0;
        foreach (var ripple in new[] { Ripple1, Ripple2, Ripple3 })
        {
            var begin = TimeSpan.FromMilliseconds(600 * i++);
            var t = (ScaleTransform)ripple.RenderTransform;
            var grow = new DoubleAnimation(1.0, 1.32, TimeSpan.FromSeconds(1.8)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            t.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ripple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.8)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    private void StopRipples()
    {
        foreach (var ripple in new[] { Ripple1, Ripple2, Ripple3 })
        {
            var t = (ScaleTransform)ripple.RenderTransform;
            t.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ripple.BeginAnimation(OpacityProperty, null);
            ripple.Opacity = 0;
        }
    }

    private void Fade(UIElement element, double to)
    {
        if (Reduced)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = to;
            return;
        }

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, StateDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void ScaleTo(double value, TimeSpan duration)
    {
        if (Reduced)
        {
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Scale.ScaleX = Scale.ScaleY = 1;
            return;
        }

        var a = new DoubleAnimation(value, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
