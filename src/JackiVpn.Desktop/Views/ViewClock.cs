using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Drives a view's own animation without keeping WPF busy: a slow timer while nothing moves (a breathing eye,
/// waiting for the next blink), every display frame only while something visibly moves, and nothing at all while
/// the view is not on screen (unloaded, collapsed, or its window minimized or hidden). Subscribing to
/// <see cref="CompositionTarget.Rendering"/> forces a full render pass at the monitor rate even when the handler
/// returns early, so it is used only for real motion.
/// </summary>
internal sealed class ViewClock
{
    private readonly FrameworkElement _owner;
    private readonly Func<bool> _tick;
    private readonly DispatcherTimer _idle;
    private Window? _window;
    private bool _fast;
    private bool _enabled = true;

    /// <param name="owner">The view; the clock follows its lifetime and visibility.</param>
    /// <param name="idleInterval">Tick spacing while nothing moves.</param>
    /// <param name="tick">One animation step; returns whether something is moving (then every frame is drawn).</param>
    public ViewClock(FrameworkElement owner, TimeSpan idleInterval, Func<bool> tick)
    {
        _owner = owner;
        _tick = tick;
        _idle = new DispatcherTimer(DispatcherPriority.Render) { Interval = idleInterval };
        _idle.Tick += (_, _) => Step();
        owner.Loaded += (_, _) =>
        {
            Attach(Window.GetWindow(owner));
            Update();
        };
        owner.Unloaded += (_, _) =>
        {
            Attach(null);
            Update();
        };
        owner.IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>Extra condition of the view (e.g. "is running"); false stops the clock.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                Update();
            }
        }
    }

    public bool IsRunning => _idle.IsEnabled || _fast;

    /// <summary>Tick spacing while nothing moves (may change at any time).</summary>
    public TimeSpan IdleInterval
    {
        get => _idle.Interval;
        set
        {
            if (_idle.Interval != value)
            {
                _idle.Interval = value;
            }
        }
    }

    /// <summary>Something started moving (a poke, a new target): draw every frame from now on.</summary>
    public void Wake()
    {
        if (OnScreen && !_fast)
        {
            GoFast();
        }
    }

    private bool OnScreen =>
        _enabled && !Motion.Reduced && _owner.IsLoaded && _owner.IsVisible
        && _window is not { WindowState: WindowState.Minimized } && _window is not { IsVisible: false };

    private void Attach(Window? window)
    {
        if (ReferenceEquals(window, _window))
        {
            return;
        }

        if (_window is not null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.IsVisibleChanged -= OnWindowVisibleChanged;
        }

        _window = window;
        if (_window is not null)
        {
            _window.StateChanged += OnWindowChanged;
            _window.IsVisibleChanged += OnWindowVisibleChanged;
        }
    }

    private void OnWindowChanged(object? sender, EventArgs e) => Update();

    private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();

    /// <summary>Starts or stops the clock to match whether the view is on screen.</summary>
    public void Update()
    {
        if (!OnScreen)
        {
            _idle.Stop();
            if (_fast)
            {
                _fast = false;
                CompositionTarget.Rendering -= OnRendering;
            }

            return;
        }

        if (!_idle.IsEnabled && !_fast)
        {
            Step();
            if (!_fast)
            {
                _idle.Start();
            }
        }
    }

    private void OnRendering(object? sender, EventArgs e) => Step();

    private void Step()
    {
        var moving = _tick();
        if (moving && !_fast)
        {
            GoFast();
        }
        else if (!moving && _fast)
        {
            _fast = false;
            CompositionTarget.Rendering -= OnRendering;
            _idle.Start();
        }
    }

    private void GoFast()
    {
        _idle.Stop();
        _fast = true;
        CompositionTarget.Rendering += OnRendering;
    }
}
