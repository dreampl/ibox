using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JackiVpn.Application.Onboarding;
using JackiVpn.Domain.Visuals;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// The first-start tour, as on Windows: one step at a time (picture, title, plain text) with Back / Next, step dots
/// and Skip. Keyboard: arrows or Enter to move on, Esc to skip.
/// </summary>
public static class TourDialog
{
    /// <summary>Shows the tour; true when it ended on its last step with nothing set up yet (go add a subscription).</summary>
    public static async Task<bool> RunAsync(Shell shell, bool hasServers)
    {
        var step = 0;
        var finished = false;
        var picture = new Border { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
        var title = T(string.Empty, 22, FontWeight.SemiBold);
        title.TextAlignment = TextAlignment.Center;
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness(0, 18, 0, 8);
        var body = T(string.Empty, 14, color: Text2);
        body.TextAlignment = TextAlignment.Center;
        body.TextWrapping = TextWrapping.Wrap;
        body.LineHeight = 21;
        var content = new StackPanel { Children = { picture, title, body } };
        content.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }];
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6, Margin = new Thickness(0, 22, 0, 0) };
        var dialog = new Window
        {
            Title = "IBOX tour",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = B(CardBg),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button? back = null;
        Button? next = null;

        void Go(int to)
        {
            step = Math.Clamp(to, 0, Tour.Steps.Count - 1);
            var s = Tour.Steps[step];
            content.Opacity = 0;
            title.Text = s.Title;
            body.Text = s.Body;
            picture.Child = Picture(s.Picture);
            back!.IsVisible = step > 0;
            next!.Content = step < Tour.Steps.Count - 1 ? "Next" : hasServers ? "Get started" : "Add my first subscription";
            dots.Children.Clear();
            for (var i = 0; i < Tour.Steps.Count; i++)
            {
                dots.Children.Add(new Rectangle { Height = 7, Width = i == step ? 22 : 7, RadiusX = 3.5, RadiusY = 3.5, Fill = B(i == step ? Accent : Ui.Border) });
            }

            content.Opacity = 1;
        }

        void Forward()
        {
            if (step < Tour.Steps.Count - 1)
            {
                Go(step + 1);
            }
            else
            {
                finished = true;
                dialog.Close();
            }
        }

        back = Ghost("Back", () => Go(step - 1));
        next = Primary("Next", Forward);
        var skip = new Button { Content = "Skip tour", Background = Brushes.Transparent, Foreground = B(Text2), Cursor = new Cursor(StandardCursorType.Hand) };
        skip.Click += (_, _) => dialog.Close();
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 26, 0, 0) };
        DockPanel.SetDock(skip, Dock.Left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { back, next } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(skip);
        buttons.Children.Add(right);
        dialog.Content = new Border { Padding = new Thickness(34, 30, 34, 24), Child = new StackPanel { Children = { content, dots, buttons } } };
        dialog.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    dialog.Close();
                    break;
                case Key.Right or Key.Enter:
                    Forward();
                    break;
                case Key.Left when step > 0:
                    Go(step - 1);
                    break;
            }
        };
        Go(0);
        await dialog.ShowDialog(shell);
        return finished && !hasServers;
    }

    private static Control Picture(TourPicture picture)
    {
        if (picture is TourPicture.Eye or TourPicture.Power)
        {
            // The IBOX eye, awake: the same living mark as on the connect button.
            var p = Look.P;
            var eye = new EyeMarkView { Width = 44, Height = 45, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            eye.AwakeFrame = picture == TourPicture.Power ? Colors.White : p.Text;
            eye.State = EyeState.Awake;
            return new Border
            {
                Width = 96,
                Height = 96,
                CornerRadius = new CornerRadius(48),
                Background = picture == TourPicture.Power
                    ? new RadialGradientBrush { GradientStops = { new GradientStop(p.OrbOn1, 0), new GradientStop(p.OrbOn2, 0.6), new GradientStop(p.OrbOn3, 1) } }
                    : B(TileBg),
                BorderBrush = B(Ui.Border),
                BorderThickness = new Thickness(picture == TourPicture.Power ? 0 : 1),
                Child = eye,
            };
        }

        var glyph = picture switch
        {
            TourPicture.Subscriptions => Icons.Subscriptions,
            TourPicture.Mode => Icons.Globe,
            _ => Icons.About,
        };
        return new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(28),
            Background = B(TileBg),
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            Child = Icon(glyph, 38, Accent, 1.8),
        };
    }
}
