using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace JackiVpn.Desktop.Views;

/// <summary>What an AI assistant is doing (the circle beside the island).</summary>
public enum AgentActivity
{
    None = 0,
    Working,
    Waiting,
    Done,
    Error,
}

/// <summary>A monitor in physical pixels (where one IBAR notch lives).</summary>
public readonly record struct MonitorArea(int Left, int Top, int Width, int Height, string Key);

/// <summary>
/// IBAR's island: fixed at the top centre of its own monitor (it stretches when pulled, it does not move), lower
/// corners that grow rounder as it grows (continuous squircle corners), liquid glass, the Claude / ChatGPT circles
/// that come out of its right side, and the start-up writing of "IBAR".
/// </summary>
public sealed partial class NotchWindow
{
    /// <summary>Stretches when pulled instead of sliding along the edge (IBAR).</summary>
    public bool Fixed { get; set; }

    /// <summary>The monitor this island belongs to (null = the main one, positioned by <see cref="NotchSettings.Position"/>).</summary>
    public MonitorArea? Monitor { get; set; }

    /// <summary>The pointer is on this island's monitor: the eye may run at full rate there, slower elsewhere.</summary>
    private bool CursorHere(Point p) => Monitor is not { } m || (p.X >= m.Left && p.X < m.Left + m.Width && p.Y >= m.Top && p.Y < m.Top + m.Height);

    private double ScreenScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    /// <summary>Lower corners: ~14 px small, growing to ~32 px when the island is large.</summary>
    private static double CornerRadiusFor(double height) => Math.Min(height / 2, Math.Clamp(14 + ((height - CollapsedHeight) * 18 / 120), 14, 32));

    private void PlaceOnMonitor()
    {
        if (Monitor is not { } m)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        // Centred on its monitor's top edge, in that monitor's own pixels (monitors may differ in scale).
        var dpi = 96u;
        var monitor = MonitorFromPoint(new NativePoint { X = m.Left + (m.Width / 2), Y = m.Top + 1 }, 2);
        if (GetDpiForMonitor(monitor, 0, out var dx, out _) == 0)
        {
            dpi = dx;
        }

        var w = (int)Math.Round(WindowWidth * dpi / 96);
        var h = (int)Math.Round(WindowHeight * dpi / 96);
        SetWindowPos(hwnd, 0, m.Left + ((m.Width - w) / 2), m.Top, w, h, 0x0010 /* NOACTIVATE */ | 0x0004 /* NOZORDER */);
    }

    // ------------------------------------------------------------------ IBAR, written at start-up

    private readonly Canvas _intro = new() { Opacity = 0, IsHitTestVisible = false };

    /// <summary>
    /// The start-up entrance: the island stretches down from the top edge like a drop and springs back into place,
    /// then writes I-B-A-R (the I is the brand's hatched mark), each letter fading and rising in, and hands over to
    /// the eye.
    /// </summary>
    public void PlayIntro()
    {
        if (Motion.Reduced)
        {
            return;
        }

        if (!IsLoaded)
        {
            // Before the first frame: the island never shows its normal self first.
            _introPending = true;
            _main.Opacity = 0;
            return;
        }

        StartIntro();
    }

    private bool _introPending;

    private void StartIntro()
    {

        _intro.Children.Clear();
        var letters = new List<FrameworkElement> { HatchMark() };
        foreach (var c in "BAR")
        {
            letters.Add(new TextBlock { Text = c.ToString(), FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        }

        var x = 0.0;
        foreach (var letter in letters)
        {
            letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(letter, x);
            Canvas.SetTop(letter, 0);
            letter.Opacity = 0;
            letter.RenderTransform = new TranslateTransform(0, 9);
            _intro.Children.Add(letter);
            x += letter.DesiredSize.Width + 5;
        }

        _intro.Width = x - 5;
        if (!_surface.Children.Contains(_intro))
        {
            _surface.Children.Add(_intro);
        }

        _main.Opacity = 0;
        _intro.Opacity = 1;
        Canvas.SetTop(_intro, (CollapsedHeight - 24) / 2);

        // The drop: from a sliver at the top edge, stretched far down, swinging back up.
        _width.To(70);
        _width.Snap();
        _height.To(18);
        _height.Snap();
        _body.Membrane.Pull(0.5, 150, 0.32, 260);
        Animate();
        After(520, () =>
        {
            _body.Membrane.Release();
            _width.To(150, 120);
            _height.To(CollapsedHeight, 60);
            Animate();
        });

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            var begin = TimeSpan.FromMilliseconds(820 + (i * 110));
            letter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
            ((TranslateTransform)letter.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(360)) { BeginTime = begin, EasingFunction = ease });
        }

        After(2050, () =>
        {
            _intro.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
            _main.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(140) });
            Render();
        });
    }

    /// <summary>The brand's "I": four hatch bars of falling width.</summary>
    private static FrameworkElement HatchMark()
    {
        var path = new Path
        {
            Fill = Brushes.White,
            Data = Geometry.Parse("M1,0H13V127H1Z M19,0H29V127H19Z M38,0H45V127H38Z M62,0H68V127H62Z"),
            Stretch = Stretch.Uniform,
            Height = 17,
            Margin = new Thickness(0, 4, 0, 0),
        };
        return path;
    }

    private void PlaceIntro(double width) => Canvas.SetLeft(_intro, (width - _intro.Width) / 2);

    // ------------------------------------------------------------------ Windows plumbing

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out WinRect rect);
}
