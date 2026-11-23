using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace JackiVpn.Desktop.Views;

internal sealed class StartupSplash : Window
{
    private readonly StackPanel _mark;
    private readonly long _shownAt = Environment.TickCount64;

    public StartupSplash()
    {
        Title = "IBOX starting";
        Width = 240;
        Height = 160;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        _mark = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (var width in new double[] { 6, 5, 3.5, 2.5 })
        {
            _mark.Children.Add(new Rectangle
            {
                Width = width, Height = 66, Margin = new Thickness(5, 0, 5, 0),
                Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(125, 128, 138)), StrokeThickness = 0.5,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, Motion.Reduced ? 1 : 0),
                Opacity = Motion.Reduced ? 1 : 0,
            });
        }
        Content = _mark;
        Loaded += (_, _) =>
        {
            if (Motion.Reduced) { return; }
            for (var i = 0; i < _mark.Children.Count; i++)
            {
                var line = (Rectangle)_mark.Children[i];
                var duration = TimeSpan.FromMilliseconds(200);
                var delay = TimeSpan.FromMilliseconds(i * 55);
                line.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = delay });
                ((ScaleTransform)line.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        };
    }

    public async Task FinishAsync()
    {
        if (!Motion.Reduced)
        {
            var remaining = 480 - (Environment.TickCount64 - _shownAt);
            if (remaining > 0) { await Task.Delay((int)remaining); }
            _mark.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)));
            await Task.Delay(140);
        }
        Close();
    }
}
