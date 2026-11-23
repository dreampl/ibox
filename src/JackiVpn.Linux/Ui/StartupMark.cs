using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace JackiVpn.Linux.Ui;

internal sealed class StartupMark : UserControl
{
    private readonly long _shownAt = Environment.TickCount64;

    public StartupMark()
    {
        var lines = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var index = 0;
        foreach (var width in new double[] { 6, 5, 3.5, 2.5 })
        {
            var line = new Rectangle
            {
                Width = width, Height = 66, Margin = new Thickness(5, 0), Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.Parse("#7D808A")), StrokeThickness = 0.5, Opacity = 0,
                Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200), Delay = TimeSpan.FromMilliseconds(index++ * 55) }],
            };
            line.AttachedToVisualTree += (_, _) => line.Opacity = 1;
            lines.Children.Add(line);
        }
        Content = lines;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }];
    }

    public async Task FinishAsync(bool reduced)
    {
        if (reduced) { return; }
        var remaining = 480 - (Environment.TickCount64 - _shownAt);
        if (remaining > 0) { await Task.Delay((int)remaining); }
        Opacity = 0;
        await Task.Delay(140);
    }
}
