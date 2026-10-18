using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Controls;

public enum PowerVisual
{
    Idle,
    Busy,
    Connected,
}

/// <summary>
/// The connect button. Idle: indigo with slowly breathing halos. Busy: ripples and an orbiting arc. Connected:
/// the core turns into a luminous gradient (teal; magenta while gaming; sky blue while streaming) with a glow.
/// Every state change cross-fades; a tap squashes and springs back.
/// </summary>
public sealed class PowerButton : ContentView
{
    private const string Loop = "power-loop";
    private readonly Ellipse _glow;
    private readonly Ellipse[] _halos = new Ellipse[3];
    private readonly Ellipse[] _ripples = new Ellipse[3];
    private readonly Microsoft.Maui.Controls.Shapes.Path _arc;
    private readonly Grid _core;
    private readonly Ellipse _faceIdle;
    private readonly Ellipse _faceOn;
    private readonly Ellipse _faceNext;
    private readonly EyeMarkView _eye;
    private readonly Spring _squashX;
    private readonly Spring _squashY;
    private PowerVisual _visual = PowerVisual.Idle;
    private Color _accent = Color.FromArgb("#2DD4BF");
    private string _accentKey = "TealGradient";

    public PowerButton()
    {
        const double size = 236;
        const double coreSize = 136;
        WidthRequest = size;
        HeightRequest = size;
        var grid = new Grid { WidthRequest = size, HeightRequest = size };

        _glow = new Ellipse { WidthRequest = size, HeightRequest = size, Opacity = 0.0 };
        grid.Add(_glow);
        for (var i = 0; i < 3; i++)
        {
            var d = coreSize + 32 + i * 30;
            _halos[i] = new Ellipse
            {
                WidthRequest = d,
                HeightRequest = d,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeThickness = 1,
            };
            grid.Add(_halos[i]);
        }

        for (var i = 0; i < 3; i++)
        {
            _ripples[i] = new Ellipse
            {
                WidthRequest = coreSize,
                HeightRequest = coreSize,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeThickness = 2,
                Opacity = 0,
            };
            grid.Add(_ripples[i]);
        }

        _arc = new Microsoft.Maui.Controls.Shapes.Path
        {
            WidthRequest = coreSize + 26,
            HeightRequest = coreSize + 26,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Aspect = Stretch.None,
            StrokeThickness = 3.5,
            StrokeLineCap = PenLineCap.Round,
            Opacity = 0,
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(
                $"M {(coreSize + 26) / 2},2 A {(coreSize + 26) / 2 - 2},{(coreSize + 26) / 2 - 2} 0 0 1 {coreSize + 24},{(coreSize + 26) / 2}")!,
        };
        grid.Add(_arc);

        _faceIdle = new Ellipse { Fill = Brush("PrimaryGradient") };
        _faceOn = new Ellipse { Opacity = 0 };
        _faceNext = new Ellipse { Opacity = 0 };
        // The IBOX "O": grey and asleep when off, a connecting orb inside it while connecting, lit with an active blue eye when on.
        // Off: tones of the indigo button itself (a light frame, a deep pupil) so the O belongs to the button.
        _eye = new EyeMarkView { WidthRequest = 64, HeightRequest = 65, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, AsleepFrame = Color.FromArgb("#C9CFFF"), AsleepPupil = Color.FromArgb("#2E3BC0") };
        _core = new Grid
        {
            WidthRequest = coreSize,
            HeightRequest = coreSize,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                _faceIdle,
                _faceOn,
                _faceNext,
                new Ellipse { Stroke = new SolidColorBrush(Colors.White.WithAlpha(0.28f)), StrokeThickness = 1.2, Margin = new Thickness(5) },
                _eye,
            },
        };
        grid.Add(_core);
        Content = grid;

        // Physical press: the core squashes wider than tall under the finger and springs back with a wobble.
        _squashX = new Spring(v => _core.ScaleX = v, 1, stiffness: 380, damping: Spring.DampingFor(380, 0.32));
        _squashY = new Spring(v => _core.ScaleY = v, 1, stiffness: 380, damping: Spring.DampingFor(380, 0.32));
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, _) =>
        {
            if (!Motion.Reduced)
            {
                _squashX.To(1.06);
                _squashY.To(0.9);
            }
        };
        pointer.PointerReleased += (_, _) => Release();
        pointer.PointerExited += (_, _) => Release();
        _core.GestureRecognizers.Add(pointer);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            Press();
            Tapped?.Invoke(this, EventArgs.Empty);
        };
        _core.GestureRecognizers.Add(tap);
        Loaded += (_, _) => Apply(animate: false);
        Unloaded += (_, _) => this.AbortAnimation(Loop);
    }

    public event EventHandler? Tapped;

    /// <summary>Sets the state and the "connected" colour (teal / gaming magenta / streaming sky).</summary>
    public void SetState(PowerVisual visual, string accentGradientKey, Color accent, bool animate = true)
    {
        var accentChanged = accentGradientKey != _accentKey;
        var changed = visual != _visual || accentChanged;
        _visual = visual;
        if (accentChanged)
        {
            _accentKey = accentGradientKey;
            _accent = accent;
            if (animate && visual == PowerVisual.Connected && !Motion.Reduced)
            {
                // Cross-fade the core into the new activity colour.
                _faceNext.Fill = Brush(accentGradientKey);
                _faceNext.Opacity = 0;
                _ = CrossFadeAsync();
            }
            else
            {
                _faceOn.Fill = Brush(accentGradientKey);
            }
        }

        if (changed)
        {
            Apply(animate);
        }
    }

    private async Task CrossFadeAsync()
    {
        await _faceNext.FadeToAsync(1, 450, Easing.CubicInOut);
        _faceOn.Fill = _faceNext.Fill;
        _faceNext.Opacity = 0;
        _ = Motion.StretchAsync(_core);
    }

    private void Apply(bool animate)
    {
        var d = animate ? Motion.Ms(480) : 0u;
        var on = _visual == PowerVisual.Connected;
        var busy = _visual == PowerVisual.Busy;
        var tint = on ? _accent : Color.FromArgb("#5B6CFF");
        _faceOn.Fill ??= Brush(_accentKey);
        _ = _faceOn.FadeToAsync(on ? 1 : 0, d, Easing.CubicInOut);
        _ = _faceIdle.FadeToAsync(on ? 0 : 1, d, Easing.CubicInOut);
        _glow.Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new(tint.WithAlpha(on ? 0.38f : 0.22f), 0f),
            new(tint.WithAlpha(0.08f), 0.55f),
            new(tint.WithAlpha(0f), 1f),
        }, new Point(0.5, 0.5), 0.5);
        _ = _glow.FadeToAsync(1, d);
        for (var i = 0; i < 3; i++)
        {
            _halos[i].Stroke = new SolidColorBrush(tint.WithAlpha(0.16f - i * 0.04f));
            _halos[i].Fill = new SolidColorBrush(tint.WithAlpha(0.05f - i * 0.012f));
            _ripples[i].Stroke = new SolidColorBrush(tint);
        }

        _arc.Stroke = new LinearGradientBrush(new GradientStopCollection { new(tint, 0f), new(tint.WithAlpha(0f), 1f) }, new Point(0, 0), new Point(1, 1));
        _ = _arc.FadeToAsync(busy ? 1 : 0, Motion.Ms(250));
        _eye.State = on ? Domain.Visuals.EyeState.Awake : busy ? Domain.Visuals.EyeState.Seeking : Domain.Visuals.EyeState.Asleep;
        if (on && animate && !Motion.Reduced)
        {
            _ = CelebrateAsync();
        }

        StartLoop();
    }

    private async Task CelebrateAsync()
    {
        await _core.ScaleToAsync(1.1, 160, Easing.CubicOut);
        await _core.ScaleToAsync(1, 700, Motion.Spring);
    }

    private void Press()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
        }

        if (!Motion.Reduced)
        {
            // A tap without a held press (accessibility, fast taps): give the same kick.
            _squashX.Impulse(1.2);
            _squashY.Impulse(-1.6);
        }
    }

    private void Release()
    {
        // Released with velocity: overshoots a little and settles like a rubber ball.
        _squashX.To(1, velocity: -1.4);
        _squashY.To(1, velocity: 2.2);
    }

    /// <summary>One loop per state: ripples + orbit while busy (60 fps), a slow breath otherwise (30 fps).</summary>
    private void StartLoop()
    {
        this.AbortAnimation(Loop);
        foreach (var r in _ripples)
        {
            r.Opacity = 0;
            r.Scale = 1;
        }

        if (Motion.Reduced)
        {
            _arc.Rotation = 0;
            return;
        }

        if (_visual == PowerVisual.Busy)
        {
            var parent = new Animation { { 0, 1, new Animation(v => _arc.Rotation = v, 0, 360) } };
            for (var i = 0; i < _ripples.Length; i++)
            {
                var ripple = _ripples[i];
                var start = i * 0.2;
                parent.Add(start, Math.Min(1, start + 0.6), new Animation(v =>
                {
                    ripple.Scale = 1 + (0.62 * v);
                    ripple.Opacity = 0.6 * (1 - v);
                }, 0, 1, Easing.CubicOut));
            }

            parent.Commit(this, Loop, 16, 1600, Easing.Linear, repeat: () => _visual == PowerVisual.Busy && IsLoaded);
            return;
        }

        var amplitude = _visual == PowerVisual.Connected ? 0.05 : 0.025;
        new Animation(v =>
        {
            var s = Math.Sin(v * Math.PI * 2);
            for (var i = 0; i < _halos.Length; i++)
            {
                _halos[i].Scale = 1 + amplitude * (0.6 + i * 0.3) * (0.5 + 0.5 * Math.Sin((v * Math.PI * 2) - (i * 0.6)));
            }

            _glow.Scale = 1 + amplitude * s;
            _glow.Opacity = 0.85 + 0.15 * s;
        }, 0, 1).Commit(this, Loop, 33, _visual == PowerVisual.Connected ? 3600u : 5200u, Easing.Linear, repeat: () => _visual != PowerVisual.Busy && IsLoaded);
    }

    private static Brush Brush(string key) => (Brush)Microsoft.Maui.Controls.Application.Current!.Resources[key];
}
