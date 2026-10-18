using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JackiVpn.Application.Onboarding;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The first-start tour: a clay card over the main window, one step at a time (picture, title, plain text), with
/// Back / Next, step dots and Skip. Keyboard: arrows or Enter to move on, Esc to skip.
/// </summary>
internal sealed class TourWindow : Window
{
    private readonly Border _picture = new() { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 8) };
    private readonly TextBlock _body = new() { FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
    private readonly Button _back = new() { Content = "Back", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { MinWidth = 120, IsDefault = true };
    private readonly StackPanel _content = new();
    private readonly bool _hasServers;
    private int _step;

    private TourWindow(bool hasServers)
    {
        _hasServers = hasServers;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        Owner = System.Windows.Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "UiFont");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        _body.SetResourceReference(TextBlock.ForegroundProperty, "B.TextSecondary");
        _back.SetResourceReference(StyleProperty, "Button.Outline");
        _next.SetResourceReference(StyleProperty, "Button.Primary");
        AutomationProperties.SetName(this, "IBOX tour");

        var skip = new Button { Content = "Skip tour", Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        skip.SetResourceReference(StyleProperty, "Button.Bare");
        skip.SetResourceReference(ForegroundProperty, "B.TextSecondary");
        skip.Click += (_, _) => Close();
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) =>
        {
            if (_step < Tour.Steps.Count - 1)
            {
                Go(_step + 1);
            }
            else
            {
                Finished = true;
                Close();
            }
        };

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 26, 0, 0) };
        DockPanel.SetDock(skip, Dock.Left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { _back, _next } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(skip);
        buttons.Children.Add(right);

        _content.Children.Add(_picture);
        _content.Children.Add(_title);
        _content.Children.Add(_body);
        var card = new ClaySurface { CornerRadius = new CornerRadius(24), Padding = new Thickness(34, 30, 34, 24), Margin = new Thickness(22) };
        SoftShadow.SetAllowed(this, true); // the tour is the one place with a shadow
        card.Content = new StackPanel { Children = { _content, _dots, buttons } };
        Content = card;

        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    break;
                case Key.Right:
                    _next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    break;
                case Key.Left when _step > 0:
                    Go(_step - 1);
                    break;
            }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            // Drag by the card, never by a button (its text or border is the click's source).
            for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase)
                {
                    return;
                }
            }

            DragMove();
        };
        Go(0, animate: false);
    }

    /// <summary>The last step was reached with "Get started" (not skipped).</summary>
    public bool Finished { get; private set; }

    /// <summary>Shows the tour; true when it ended on its last step with nothing set up yet (go add a subscription).</summary>
    public static bool Run(bool hasServers)
    {
        var tour = new TourWindow(hasServers);
        tour.ShowDialog();
        return tour.Finished && !hasServers;
    }

    private void Go(int step, bool animate = true)
    {
        _step = Math.Clamp(step, 0, Tour.Steps.Count - 1);
        var s = Tour.Steps[_step];
        _title.Text = s.Title;
        _body.Text = s.Body;
        _picture.Child = Picture(s.Picture);
        _back.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = _step < Tour.Steps.Count - 1 ? "Next" : _hasServers ? "Get started" : "Add my first subscription";
        _dots.Children.Clear();
        for (var i = 0; i < Tour.Steps.Count; i++)
        {
            var dot = new Rectangle { Height = 7, Width = i == _step ? 22 : 7, RadiusX = 3.5, RadiusY = 3.5, Margin = new Thickness(3, 0, 3, 0) };
            dot.SetResourceReference(Shape.FillProperty, i == _step ? "B.Accent" : "B.Divider");
            _dots.Children.Add(dot);
        }

        if (animate)
        {
            Motion.FadeTo(_content, 0, TimeSpan.Zero);
            Motion.FadeTo(_content, 1, Motion.Normal);
        }
    }

    private static UIElement Picture(TourPicture picture)
    {
        if (picture is TourPicture.Eye or TourPicture.Power)
        {
            // The IBOX eye, awake: the same living mark as on the connect button.
            // Welcome: the eye on a plain tile (its blue pupil stands out); the button step: the connected green orb.
            var disc = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(48), BorderThickness = new Thickness(picture == TourPicture.Power ? 0 : 1) };
            disc.SetResourceReference(Border.BackgroundProperty, picture == TourPicture.Power ? "B.OrbOn" : "B.TileFill");
            disc.SetResourceReference(Border.BorderBrushProperty, "B.Border");
            var eye = new EyeMarkView { Width = 44, Height = 45, State = EyeState.Awake, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (picture != TourPicture.Power)
            {
                eye.SetResourceReference(EyeMarkView.AwakeFrameProperty, "C.Text");
            }

            disc.Child = eye;
            return disc;
        }

        var glyph = picture switch
        {
            TourPicture.Subscriptions => "",
            TourPicture.Mode => "",
            _ => "",
        };
        var tile = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(28) };
        tile.SetResourceReference(Border.BackgroundProperty, "B.TileFill");
        tile.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        tile.BorderThickness = new Thickness(1);
        var icon = new TextBlock { Text = glyph, FontSize = 38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(StyleProperty, "Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent");
        icon.FontSize = 38;
        tile.Child = icon;
        return tile;
    }
}
