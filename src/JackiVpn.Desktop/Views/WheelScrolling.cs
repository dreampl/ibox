using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Calm, proportional wheel scrolling for every ScrollViewer. WPF scrolls three lines for each wheel message
/// whatever its size, so precision touchpads and smooth-scrolling mice (many small messages) threw whole pages at
/// a time. Here the distance follows the wheel delta (one notch ≈ 45 px) and glides there in ~150 ms; a list that
/// is at its end passes the scroll on to the page around it. Item-based lists (combo box pop-ups) keep WPF's rule.
/// </summary>
internal static class WheelScrolling
{
    private const double PixelsPerNotchLine = 15;
    private static readonly ConditionalWeakTable<ScrollViewer, Glide> Glides = new();
    private static readonly List<Glide> Active = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _last;

    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Handled || Keyboard.Modifiers != ModifierKeys.None || IsItemBased(sv))
        {
            return;
        }

        var pixels = -e.Delta / 120.0 * Math.Max(1, SystemParameters.WheelScrollLines) * PixelsPerNotchLine;
        // The innermost viewer that can still move this way takes it (a list at its end hands over to the page).
        for (DependencyObject? d = sv; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer candidate && !IsItemBased(candidate) && CanScroll(candidate, pixels))
            {
                Scroll(candidate, pixels);
                e.Handled = true;
                return;
            }
        }

        e.Handled = true;
    }

    /// <summary>A list that scrolls by whole items (its offsets are item counts, not pixels).</summary>
    private static bool IsItemBased(ScrollViewer sv) =>
        sv.CanContentScroll && (sv.TemplatedParent is not ItemsControl owner || VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Item);

    private static bool CanScroll(ScrollViewer sv, double pixels)
    {
        var target = Glides.TryGetValue(sv, out var g) && g.Running ? g.Target : sv.VerticalOffset;
        return sv.ScrollableHeight > 0 && (pixels > 0 ? target < sv.ScrollableHeight - 0.5 : target > 0.5);
    }

    private static void Scroll(ScrollViewer sv, double pixels)
    {
        var glide = Glides.GetValue(sv, s => new Glide(s));
        var from = glide.Running ? glide.Target : sv.VerticalOffset;
        glide.Target = Math.Clamp(from + pixels, 0, sv.ScrollableHeight);
        if (Motion.Reduced)
        {
            sv.ScrollToVerticalOffset(glide.Target);
            return;
        }

        if (!glide.Running)
        {
            glide.Running = true;
            glide.Position = sv.VerticalOffset;
            Active.Add(glide);
            if (Active.Count == 1)
            {
                _last = Clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += OnFrame;
            }
        }
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var g = Active[i];
            if (!g.Viewer.IsLoaded || Math.Abs(g.Viewer.VerticalOffset - g.Position) > 3)
            {
                // Gone, or the user dragged the scroll bar meanwhile: stop gliding.
                g.Running = false;
                Active.RemoveAt(i);
                continue;
            }

            // Exponential approach: fast start, soft landing (~150 ms to settle).
            g.Position += (g.Target - g.Position) * (1 - Math.Exp(-dt * 22));
            if (Math.Abs(g.Target - g.Position) < 0.5)
            {
                g.Position = g.Target;
                g.Running = false;
                Active.RemoveAt(i);
            }

            g.Viewer.ScrollToVerticalOffset(g.Position);
        }

        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private sealed class Glide(ScrollViewer viewer)
    {
        public ScrollViewer Viewer { get; } = viewer;
        public double Target { get; set; }
        public double Position { get; set; }
        public bool Running { get; set; }
    }
}
