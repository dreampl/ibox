namespace JackiVpn.Domain.Visuals;

/// <summary>What the eye mark is doing: asleep (not connected), seeking (connecting) or awake (connected).</summary>
public enum EyeState
{
    Asleep = 0,
    Seeking,
    Awake,
}

/// <summary>
/// The "O" of the IBOX wordmark as a living eye: a square frame with an open top-left corner and the blue square as
/// its pupil. Geometry in the mark's own units (126 × 127), taken from assets/brand/ibox-wordmark.svg.
/// </summary>
public static class EyeMark
{
    /// <summary>The frame (the O); fill rule non-zero.</summary>
    public const string FramePath = "M30 0H126V127H8Q0 127 0 119V30H27V100H99V27H30Z";

    public const double Width = 126;
    public const double Height = 127;

    /// <summary>The window inside the frame the pupil moves in.</summary>
    public const double WindowLeft = 27;
    public const double WindowTop = 27;
    public const double WindowWidth = 72;
    public const double WindowHeight = 73;

    public const double PupilSize = 22;

    /// <summary>The logo's blue (the pupil when awake).</summary>
    public const uint PupilBlue = 0xFF3D9BFF;

    /// <summary>Where the pupil sits in the logo (looking slightly right), as a look direction.</summary>
    public const double RestLookX = 0.68;
    public const double RestLookY = 0.02;

    private const double CenterX = WindowLeft + (WindowWidth / 2);
    private const double CenterY = WindowTop + (WindowHeight / 2);
    private const double RangeX = (WindowWidth / 2) - (PupilSize / 2) - 3;
    private const double RangeY = (WindowHeight / 2) - (PupilSize / 2) - 3;

    /// <summary>Top-left of the pupil for a look direction (-1…1 on each axis).</summary>
    public static (double X, double Y) PupilAt(double lookX, double lookY) =>
        (CenterX + (Math.Clamp(lookX, -1.2, 1.2) * RangeX) - (PupilSize / 2), CenterY + (Math.Clamp(lookY, -1.2, 1.2) * RangeY) - (PupilSize / 2));
}

/// <summary>
/// Motion of the eye, shared by every renderer: the pupil travels on springs (quick saccades with a hint of
/// overshoot), blinks every few seconds (sometimes twice), wanders when nothing holds its attention, follows a
/// pointer when given one, and while asleep only breathes by about half a pixel. Call <see cref="Step"/> each frame.
/// </summary>
public sealed class EyeMotion
{
    private const double Stiffness = 240;
    private const double Damping = 2 * 15.5 * 0.62; // ≈ damping ratio 0.62 at this stiffness
    private readonly Random _random;
    private double _x = EyeMark.RestLookX;
    private double _y = EyeMark.RestLookY;
    private double _vx;
    private double _vy;
    private double _tx = EyeMark.RestLookX;
    private double _ty = EyeMark.RestLookY;
    private double _time;
    private double _lastLook = double.NegativeInfinity;
    private double _nextWander;
    private double _nextBlink;
    private double _blinkStart = double.NegativeInfinity;
    private bool _doubleBlink;
    private EyeState _state = EyeState.Asleep;

    public EyeMotion(int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
        _nextBlink = 2 + (_random.NextDouble() * 3);
    }

    public EyeState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            if (value == EyeState.Awake)
            {
                // Waking up: a blink, then a look around.
                _blinkStart = _time;
                _doubleBlink = false;
                _nextWander = _time + 0.6;
            }
            else
            {
                _tx = EyeMark.RestLookX;
                _ty = value == EyeState.Asleep ? 0.35 : EyeMark.RestLookY;
            }
        }
    }

    /// <summary>Look direction of the pupil (-1…1).</summary>
    public double LookX => _x;

    public double LookY => _y;

    /// <summary>Vertical scale of the pupil: 1 open, ~0.1 shut.</summary>
    public double Openness { get; private set; } = 1;

    /// <summary>Sub-pixel breathing offset while asleep (in device-independent pixels, ±0.5).</summary>
    public double DriftX { get; private set; }

    public double DriftY { get; private set; }

    /// <summary>True while something visibly moves (renderers can drop to a slow tick otherwise).</summary>
    public bool IsMoving { get; private set; }

    /// <summary>Look towards a point: direction -1…1 per axis (beyond the edge is clamped).</summary>
    public void LookAt(double x, double y)
    {
        _tx = Math.Clamp(x, -1, 1);
        _ty = Math.Clamp(y, -1, 1);
        _lastLook = _time;
    }

    /// <summary>A playful reaction (a tap, a hover): quick double blink and a little hop of the pupil.</summary>
    public void Poke()
    {
        _blinkStart = _time;
        _doubleBlink = true;
        _vy -= 6;
    }

    /// <summary>Advances the motion by <paramref name="dt"/> seconds.</summary>
    public void Step(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.05);
        _time += dt;
        if (_state == EyeState.Awake)
        {
            if (_time - _lastLook > 2.5 && _time >= _nextWander)
            {
                // Idle: glance somewhere else, sometimes back to the resting gaze.
                if (_random.NextDouble() < 0.3)
                {
                    (_tx, _ty) = (EyeMark.RestLookX, EyeMark.RestLookY);
                }
                else
                {
                    _tx = (_random.NextDouble() * 1.6) - 0.8;
                    _ty = (_random.NextDouble() * 1.4) - 0.7;
                }

                _nextWander = _time + 1.2 + (_random.NextDouble() * 2.6);
            }

            if (_time >= _nextBlink)
            {
                _blinkStart = _time;
                _doubleBlink = _random.NextDouble() < 0.12;
                _nextBlink = _time + 2.6 + (_random.NextDouble() * 3.8);
            }
        }

        // Semi-implicit Euler spring towards the target.
        var ax = (Stiffness * (_tx - _x)) - (Damping * _vx);
        var ay = (Stiffness * (_ty - _y)) - (Damping * _vy);
        _vx += ax * dt;
        _vy += ay * dt;
        _x += _vx * dt;
        _y += _vy * dt;
        var springMoving = Math.Abs(_vx) + Math.Abs(_vy) > 0.004 || Math.Abs(_tx - _x) + Math.Abs(_ty - _y) > 0.004;
        if (!springMoving)
        {
            (_x, _y, _vx, _vy) = (_tx, _ty, 0, 0);
        }

        // Blink: shut in 70 ms, open in 110 ms (a second one right after when doubled).
        var b = _time - _blinkStart;
        var length = _doubleBlink ? 0.42 : 0.18;
        if (b >= 0 && b < length)
        {
            var local = _doubleBlink && b >= 0.24 ? b - 0.24 : b;
            Openness = local < 0.07 ? 1 - (0.9 * (local / 0.07)) : local < 0.18 ? 0.1 + (0.9 * ((local - 0.07) / 0.11)) : 1;
        }
        else
        {
            Openness = 1;
        }

        if (_state == EyeState.Asleep)
        {
            // Barely alive: about half a pixel of slow breathing.
            DriftX = 0.5 * Math.Sin(_time * 0.55);
            DriftY = 0.5 * Math.Sin((_time * 0.41) + 1.3);
        }
        else
        {
            DriftX = DriftY = 0;
        }

        IsMoving = springMoving || Openness < 1;
    }
}

/// <summary>Colour of the connected button per traffic mode (one palette for every platform).</summary>
public enum VisualMode
{
    Normal = 0,
    Gaming,
    Streaming,
    Eco,
    Work,
}

public static class ModePalette
{
    /// <summary>A mode set in Traffic control wins over the detected activity.</summary>
    public static VisualMode Of(Connection.ActivityProfile activity, Usage.TrafficMode traffic) => traffic switch
    {
        Usage.TrafficMode.Eco => VisualMode.Eco,
        Usage.TrafficMode.Work => VisualMode.Work,
        Usage.TrafficMode.Gaming => VisualMode.Gaming,
        Usage.TrafficMode.Streaming => VisualMode.Streaming,
        _ => activity switch
        {
            Connection.ActivityProfile.Gaming => VisualMode.Gaming,
            Connection.ActivityProfile.Streaming => VisualMode.Streaming,
            _ => VisualMode.Normal,
        },
    };

    /// <summary>Light, main and dark tones (ARGB) of a mode: teal; gaming magenta-red; streaming blue; eco light green; work medium blue.</summary>
    public static (uint Light, uint Main, uint Dark) Colors(VisualMode mode) => mode switch
    {
        VisualMode.Gaming => (0xFFFDA4C4, 0xFFF43F7A, 0xFFBE123C),
        VisualMode.Streaming => (0xFFBAE6FD, 0xFF38BDF8, 0xFF0284C7),
        VisualMode.Eco => (0xFFDCFCE7, 0xFF86EFAC, 0xFF22C55E),
        VisualMode.Work => (0xFFBFDBFE, 0xFF3B82F6, 0xFF1D4ED8),
        _ => (0xFF99F6E4, 0xFF2DD4BF, 0xFF0D9488),
    };

    public static string Label(VisualMode mode) => mode switch
    {
        VisualMode.Gaming => "GAMING",
        VisualMode.Streaming => "STREAMING",
        VisualMode.Eco => "ECO",
        VisualMode.Work => "WORK",
        _ => "CONNECTED",
    };
}
