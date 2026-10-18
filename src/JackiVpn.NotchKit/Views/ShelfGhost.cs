using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// An item pulled off the notch's shelf, travelling with the pointer while it is dragged (Windows shows no picture
/// for it otherwise). Click-through, never focused; when the drag ends it fades where it was dropped, or flies back
/// to the notch when it was not.
/// </summary>
internal sealed partial class ShelfGhost : Window
{
    private const double Size = 64;
    private readonly Matrix _fromDevice;
    private readonly ScaleTransform _scale = new(0.8, 0.8);

    public ShelfGhost(UIElement picture, Matrix fromDevice)
    {
        _fromDevice = fromDevice;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = Size;
        Height = Size;
        Content = new Border
        {
            Width = 50,
            Height = 50,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x11, 0x18, 0x27)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = picture,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, -20);
            SetWindowLongPtr(hwnd, -20, (nint)(style | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */));
        };
        Loaded += (_, _) =>
        {
            var grow = new DoubleAnimation(1, Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(180)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }

    /// <summary>Just below and right of the pointer, like Explorer's own drag pictures.</summary>
    public void Follow()
    {
        if (ScreenCursor.TryGet(out var p))
        {
            var at = _fromDevice.Transform(p);
            Left = at.X - (Size / 2) + 14;
            Top = at.Y - (Size / 2) + 18;
        }
    }

    /// <summary>Fades out where it was dropped, or flies back to <paramref name="home"/> (screen, device-independent).</summary>
    public void Finish(bool dropped, Point? home)
    {
        var reduced = Motion.Reduced;
        if (reduced)
        {
            Close();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(dropped ? 200 : 340);
        var fade = new DoubleAnimation(0, duration) { BeginTime = dropped ? TimeSpan.Zero : TimeSpan.FromMilliseconds(220) };
        fade.Completed += (_, _) => Close();
        if (dropped)
        {
            var shrink = new DoubleAnimation(0.5, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
        else if (home is { } h)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            BeginAnimation(LeftProperty, new DoubleAnimation(h.X - (Size / 2), duration) { EasingFunction = ease });
            BeginAnimation(TopProperty, new DoubleAnimation(h.Y - (Size / 2), duration) { EasingFunction = ease });
        }

        BeginAnimation(OpacityProperty, fade);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
