using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Small, consistent motion vocabulary (durations 180–360 ms, ease-out). Everything is skipped when
/// <see cref="Reduced"/> is set (user setting or Windows "Animation effects" off), so no element ever depends
/// on an animation to become visible.
/// </summary>
public static class Motion
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(360);

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>Global switch; updated from settings and the system animation preference.</summary>
    public static bool Reduced { get; set; }

    // ------------------------------------------------------------------ staggered entrance

    /// <summary>Children of the panel fade and rise in one after another when it is loaded.</summary>
    public static readonly DependencyProperty StaggerChildrenProperty = DependencyProperty.RegisterAttached(
        "StaggerChildren", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnStaggerChanged));

    public static bool GetStaggerChildren(DependencyObject d) => (bool)d.GetValue(StaggerChildrenProperty);

    public static void SetStaggerChildren(DependencyObject d, bool value) => d.SetValue(StaggerChildrenProperty, value);

    private static void OnStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Panel panel && e.NewValue is true)
        {
            panel.Loaded += (_, _) => Stagger(panel);
        }
    }

    public static void Stagger(Panel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (Reduced)
        {
            return;
        }

        var i = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible)
            {
                continue;
            }

            Rise(child, TimeSpan.FromMilliseconds(45 * Math.Min(i++, 8)), 14);
        }
    }

    /// <summary>Fade in while moving up by <paramref name="distance"/> pixels.</summary>
    public static void Rise(UIElement element, TimeSpan delay, double distance = 10)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Reduced)
        {
            return;
        }

        var t = new TranslateTransform(0, distance);
        element.RenderTransform = t;
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Slow) { BeginTime = delay, EasingFunction = EaseOut });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, Slow) { BeginTime = delay, EasingFunction = EaseOut });
    }

    // ------------------------------------------------------------------ text cross-fade

    /// <summary>A TextBlock whose text changes fades the new text in with a small lift.</summary>
    public static readonly DependencyProperty FadeOnChangeProperty = DependencyProperty.RegisterAttached(
        "FadeOnChange", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeOnChangeChanged));

    public static bool GetFadeOnChange(DependencyObject d) => (bool)d.GetValue(FadeOnChangeProperty);

    public static void SetFadeOnChange(DependencyObject d, bool value) => d.SetValue(FadeOnChangeProperty, value);

    private static void OnFadeOnChangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb || e.NewValue is not true)
        {
            return;
        }

        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        descriptor.AddValueChanged(tb, (_, _) =>
        {
            if (Reduced || !tb.IsLoaded)
            {
                return;
            }

            var t = tb.RenderTransform as TranslateTransform ?? new TranslateTransform();
            tb.RenderTransform = t;
            tb.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, 1, Normal) { EasingFunction = EaseOut });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Normal) { EasingFunction = EaseOut });
        });
    }

    // ------------------------------------------------------------------ slide-in

    /// <summary>Slides the element in from the right when it is loaded (toasts).</summary>
    public static readonly DependencyProperty SlideInProperty = DependencyProperty.RegisterAttached(
        "SlideIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSlideInChanged));

    public static bool GetSlideIn(DependencyObject d) => (bool)d.GetValue(SlideInProperty);

    public static void SetSlideIn(DependencyObject d, bool value) => d.SetValue(SlideInProperty, value);

    private static void OnSlideInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true)
        {
            return;
        }

        fe.Loaded += (_, _) =>
        {
            if (Reduced)
            {
                return;
            }

            var t = new TranslateTransform(36, 0);
            fe.RenderTransform = t;
            fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { EasingFunction = EaseOut });
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36, 0, Slow) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } });
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Endless gentle opacity pulse (status dots, halos). Stops when reduced motion is on.</summary>
    public static void Breathe(UIElement element, double from, double to, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Reduced)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return;
        }

        // A slow pulse needs no more than 15 frames per second.
        Endless(element, UIElement.OpacityProperty, new DoubleAnimation(from, to, new Duration(period))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = EaseInOut,
        }, framesPerSecond: 15);
    }

    // ------------------------------------------------------------------ endless animations

    private static readonly List<WeakReference<AnimationClock>> EndlessClocks = [];
    private static bool _paused;

    /// <summary>
    /// Starts an endless decoration (pulse, spinner, ripple) at a capped frame rate. Every endless clock pauses while
    /// the main window is not on screen (<see cref="SetMainWindowShown"/>): nobody sees it, and a running animation
    /// keeps WPF rendering frames.
    /// </summary>
    public static void Endless(IAnimatable target, DependencyProperty property, AnimationTimeline animation, int framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(animation);
        Timeline.SetDesiredFrameRate(animation, framesPerSecond);
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        if (_paused)
        {
            clock.Controller?.Pause();
        }

        lock (EndlessClocks)
        {
            EndlessClocks.RemoveAll(w => !w.TryGetTarget(out var c) || c.CurrentState == ClockState.Stopped || c.Controller is null);
            EndlessClocks.Add(new WeakReference<AnimationClock>(clock));
        }
    }

    /// <summary>Called by the main window when it is minimized, hidden to the tray, or shown again.</summary>
    public static void SetMainWindowShown(bool shown)
    {
        if (_paused == !shown)
        {
            return;
        }

        _paused = !shown;
        lock (EndlessClocks)
        {
            foreach (var weak in EndlessClocks)
            {
                if (weak.TryGetTarget(out var clock) && clock.Controller is { } controller)
                {
                    if (_paused)
                    {
                        controller.Pause();
                    }
                    else
                    {
                        controller.Resume();
                    }
                }
            }
        }
    }

    public static void FadeTo(UIElement element, double to, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Reduced)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, duration ?? Normal) { EasingFunction = EaseOut });
    }

    private static T Freeze<T>(T f)
        where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
