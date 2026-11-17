using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Views;

public sealed class NexBadge : Border
{
    private readonly LinearGradientBrush _gradient;

    public NexBadge()
    {
        StrokeShape = new RoundRectangle { CornerRadius = 8 };
        StrokeThickness = 0;
        Padding = new Thickness(8, 2);
        VerticalOptions = LayoutOptions.Center;
        _gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb("#243E618A"), 0),
                new GradientStop(Color.FromArgb("#854C99D8"), 0.48f),
                new GradientStop(Color.FromArgb("#685C4C9B"), 0.55f),
                new GradientStop(Color.FromArgb("#243E618A"), 1),
            },
        };
        Background = _gradient;
        Content = new Label { Text = "Nex", FontSize = 11, FontFamily = "OpenSansSemibold", TextColor = Color.FromArgb("#E8E8FF") };
        SemanticProperties.SetDescription(this, "Nex");
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => this.AbortAnimation("NexGlow");
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsVisible)) UpdateAnimation();
        };
    }

    private void UpdateAnimation()
    {
        this.AbortAnimation("NexGlow");
        if (!IsLoaded || !IsVisible || Controls.Motion.Reduced) return;
        new Animation(progress =>
        {
            var angle = progress * 2 * Math.PI;
            var x = Math.Cos(angle) * 0.5;
            var y = Math.Sin(angle) * 0.5;
            _gradient.StartPoint = new Point(0.5 - x, 0.5 - y);
            _gradient.EndPoint = new Point(0.5 + x, 0.5 + y);
        }).Commit(this, "NexGlow", 64, 24000, Easing.Linear, repeat: () => IsLoaded && IsVisible && !Controls.Motion.Reduced);
    }
}
