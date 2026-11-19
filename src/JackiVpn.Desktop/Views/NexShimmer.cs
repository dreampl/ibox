using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The slow turning shimmer on the Nex badge. A XAML storyboard ran at 60 fps from Loaded on, even on collapsed
/// badges (every server row has one) and while the window was minimised, so WPF never stopped rendering. This runs
/// only while the badge is visible, at 15 fps, through <see cref="Motion.Endless"/> (paused off screen).
/// </summary>
public static class NexShimmer
{
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(NexShimmer), new PropertyMetadata(false, OnSpinChanged));

    public static bool GetSpin(DependencyObject element) => (bool)element.GetValue(SpinProperty);

    public static void SetSpin(DependencyObject element, bool value) => element.SetValue(SpinProperty, value);

    private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
        {
            return;
        }

        border.IsVisibleChanged -= OnVisibleChanged;
        if ((bool)e.NewValue)
        {
            border.IsVisibleChanged += OnVisibleChanged;
        }

        Update(border);
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((Border)sender);

    private static void Update(Border border)
    {
        if (border.Background?.RelativeTransform is not RotateTransform rotate || rotate.IsFrozen)
        {
            return;
        }

        if (GetSpin(border) && border.IsVisible && !Motion.Reduced)
        {
            Motion.Endless(rotate, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(24)) { RepeatBehavior = RepeatBehavior.Forever }, framesPerSecond: 15);
        }
        else
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
