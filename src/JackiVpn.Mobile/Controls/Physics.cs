namespace JackiVpn.Mobile.Controls;

/// <summary>
/// One shared 60 fps clock for everything that moves by physics (springs, orbs). It runs only while something
/// needs frames and stops itself when the last subscriber leaves — no timer, no wake-ups, when the UI is still.
/// </summary>
public static class FrameTicker
{
    private static readonly List<Func<double, bool>> Subscribers = [];
    private static IDispatcherTimer? _timer;
    private static long _last;

    /// <summary>Adds a per-frame callback (argument: seconds since the last frame); it is removed when it returns false.</summary>
    public static void Add(Func<double, bool> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        Subscribers.Add(onFrame);
        if (_timer is null)
        {
            var dispatcher = Microsoft.Maui.Controls.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            _timer = dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
        }

        if (!_timer.IsRunning)
        {
            _last = Environment.TickCount64;
            _timer.Start();
        }
    }

    public static void Remove(Func<double, bool> onFrame) => Subscribers.Remove(onFrame);

    private static void Tick()
    {
        var now = Environment.TickCount64;
        // A long pause (app in background) must not make springs jump: cap the step.
        var dt = Math.Clamp((now - _last) / 1000.0, 0.001, 0.05);
        _last = now;
        for (var i = Subscribers.Count - 1; i >= 0; i--)
        {
            bool keep;
            try
            {
                keep = Subscribers[i](dt);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                keep = false;
            }

            if (!keep && i < Subscribers.Count)
            {
                Subscribers.RemoveAt(i);
            }
        }

        if (Subscribers.Count == 0)
        {
            _timer?.Stop();
        }
    }
}

/// <summary>
/// A damped spring (Hooke's law + viscous damping, unit mass) integrated every frame. Retargeting keeps the
/// current velocity, so a flick or an interrupted move carries its momentum into the next one, like a real object.
/// </summary>
public sealed class Spring
{
    private readonly Action<double> _apply;
    private bool _running;

    public Spring(Action<double> apply, double value = 0, double stiffness = 380, double damping = 26)
    {
        _apply = apply;
        Value = value;
        Target = value;
        Stiffness = stiffness;
        Damping = damping;
    }

    public double Value { get; private set; }
    public double Velocity { get; set; }
    public double Target { get; private set; }

    /// <summary>k (N/m for a 1 kg mass): higher = faster.</summary>
    public double Stiffness { get; set; }

    /// <summary>c: critical damping is 2·√k; below it the spring overshoots and wobbles.</summary>
    public double Damping { get; set; }

    public bool IsMoving => _running;

    /// <summary>Damping for a damping ratio ζ (1 = no overshoot, 0.5 = one visible wobble).</summary>
    public static double DampingFor(double stiffness, double ratio) => 2 * Math.Sqrt(stiffness) * ratio;

    public void To(double target, double? velocity = null)
    {
        Target = target;
        if (velocity is { } v)
        {
            Velocity = v;
        }

        if (Motion.Reduced)
        {
            Snap(target);
            return;
        }

        if (!_running)
        {
            _running = true;
            FrameTicker.Add(Step);
        }
    }

    /// <summary>Jumps without animation.</summary>
    public void Snap(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
        _apply(value);
    }

    /// <summary>Kick: adds velocity (taps, bounces) and lets the spring bring it back.</summary>
    public void Impulse(double velocity)
    {
        Velocity += velocity;
        To(Target);
    }

    private bool Step(double dt)
    {
        // Semi-implicit Euler in small sub-steps: stable for stiff springs at 60 fps.
        const int steps = 4;
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var force = (-Stiffness * (Value - Target)) - (Damping * Velocity);
            Velocity += force * h;
            Value += Velocity * h;
        }

        var settled = Math.Abs(Velocity) < 0.01 && Math.Abs(Value - Target) < 0.001 * Math.Max(1, Math.Abs(Target));
        if (settled)
        {
            Value = Target;
            Velocity = 0;
        }

        _apply(Value);
        _running = !settled;
        return _running;
    }
}

/// <summary>Physical press feedback: squash on touch, a springy release with a little overshoot.</summary>
public static class Squish
{
    /// <summary>Attaches press/release springs to a view (scale) and returns them for extra kicks.</summary>
    public static Spring Attach(View view, double pressed = 0.9)
    {
        ArgumentNullException.ThrowIfNull(view);
        var scale = new Spring(v => view.Scale = v, 1, stiffness: 520, damping: Spring.DampingFor(520, 0.42));
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, _) => scale.To(pressed);
        pointer.PointerReleased += (_, _) => scale.To(1, velocity: 3.5);
        pointer.PointerExited += (_, _) => scale.To(1);
        view.GestureRecognizers.Add(pointer);
        return scale;
    }
}
