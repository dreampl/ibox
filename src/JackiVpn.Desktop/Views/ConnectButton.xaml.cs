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
        Root.MouseEnter += (_, _) => { Eye.Eye.Poke(); Eye.Wake(); };
        // Awake, the eye follows the pointer anywhere on screen; when the pointer rests it looks around by itself.
        Eye.BeforeFrame = eye =>
        {
            // Only while IBOX is the active window: following every mouse move in other apps would redraw all day.
            if (State == ConnectionState.Connected && Window.GetWindow(this) is { IsActive: true } && ScreenCursor.TryGet(out var p) && p != _lastCursor)
            {
                _lastCursor = p;
                eye.LookAtScreen(p);
            }
        };
        OnDisc.Fill = DiscBrush(VisualMode.Normal);
        Loaded += (_, _) =>
        {
            OnDisc.Fill = DiscBrush(Mode);
            UpdateVisuals(null);
            Services.ThemeManager.Changed += OnThemeChanged;
        };
        Unloaded += (_, _) => Services.ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        OnDisc.Fill = DiscBrush(Mode);
        UpdateVisuals(null);
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

    /// <summary>The connected orb: the theme's green clay orb normally, the mode's colour in Gaming/Streaming/Eco/Work.</summary>
    private Brush DiscBrush(VisualMode mode)
    {
        if (mode == VisualMode.Normal && TryFindResource("B.OrbOn") is Brush orb)
        {
            return orb;
        }

        var (light, main, dark) = ModePalette.Colors(mode);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops = [new GradientStop(Argb(light), 0), new GradientStop(Argb(main), 0.55), new GradientStop(Argb(dark), 1)],
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Text and eye frame on the orb: dark green on the light theme's pastel mint, white otherwise.</summary>
    private Color OnOrbInk(bool connected) =>
        connected && Mode == VisualMode.Normal && Services.ThemeManager.IsLight ? Color.FromRgb(0x06, 0x5F, 0x46)
        : !connected && State is ConnectionState.Disconnected && Services.ThemeManager.IsLight ? Color.FromRgb(0x47, 0x55, 0x69)
        : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);

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
        var ink = OnOrbInk(connected);
        Caption.Foreground = new SolidColorBrush(ink);
        CaptionDot.Fill = new SolidColorBrush(ink);
        CaptionDot.Visibility = connected || State == ConnectionState.Faulted ? Visibility.Visible : Visibility.Collapsed;
        Eye.AwakeFrame = connected && Mode == VisualMode.Normal && Services.ThemeManager.IsLight ? Color.FromRgb(0x06, 0x5F, 0x46) : Colors.White;
        Caption.Text = State switch
        {
            ConnectionState.Connected => ModePalette.Label(Mode),
            ConnectionState.Faulted => "RETRY",
            ConnectionState.Disconnected => "CONNECT",
            ConnectionState.Disconnecting => "STOPPING",
            _ => "CANCEL", // a click now cancels; the status line says what is in progress
        };
        Root.ToolTip = Busy && State != ConnectionState.Disconnecting ? "Cancel connecting" : null;

        // Halo: breathing in the mode colour when connected, faint red when faulted, off otherwise.
        var (light, main, _) = ModePalette.Colors(Mode);
        if (connected)
        {
            var normal = Mode == VisualMode.Normal && TryFindResource("C.Success") is Color success;
            var haloColor = Mode == VisualMode.Normal && TryFindResource("C.Success") is Color s ? s : Argb(main);
            Halo.Fill = Glow(haloColor);
            OnRing.Stroke = normal ? new SolidColorBrush(Color.FromArgb(0x90, haloColor.R, haloColor.G, haloColor.B)) : new LinearGradientBrush(Argb(light), Argb(main), 45);
        }
        else
        {
            Halo.Fill = Glow(TryFindResource("C.Danger") is Color danger ? danger : Colors.IndianRed);
        }

        if (State == ConnectionState.Connected)
        {
            if (Reduced)
            {
                Halo.BeginAnimation(OpacityProperty, null);
                Halo.Opacity = 0.25;
            }
            else
            {
                Motion.Breathe(Halo, 0.16, 0.34, TimeSpan.FromSeconds(2.6));
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
            Motion.Endless(Spin, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }, framesPerSecond: 30);
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

    /// <summary>A blurred disc as a gradient: solid to 60 %, half at the old disc's edge, gone at the rim.</summary>
    private static RadialGradientBrush Glow(Color c)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(c, 0),
                new GradientStop(c, 0.6),
                new GradientStop(Color.FromArgb(0x80, c.R, c.G, c.B), 0.75),
                new GradientStop(Color.FromArgb(0x1C, c.R, c.G, c.B), 0.9),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        brush.Freeze();
        return brush;
    }
    private void StartRipples()
    {
        var i = 0;
        foreach (var ripple in new[] { Ripple1, Ripple2, Ripple3 })
        {
            var begin = TimeSpan.FromMilliseconds(600 * i++);
            var t = (ScaleTransform)ripple.RenderTransform;
            var grow = new DoubleAnimation(1.0, 1.32, TimeSpan.FromSeconds(1.8)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Motion.Endless(t, ScaleTransform.ScaleXProperty, grow, framesPerSecond: 30);
            Motion.Endless(t, ScaleTransform.ScaleYProperty, grow, framesPerSecond: 30);
            Motion.Endless(ripple, OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.8)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever }, framesPerSecond: 30);
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
