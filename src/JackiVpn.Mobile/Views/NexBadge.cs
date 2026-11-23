using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Views;

/// <summary>Static gradient pill. No looping animation: it appears once per server row, and a per-frame loop each was costly on older phones.</summary>
public sealed class NexBadge : Border
{
    private static readonly Brush Gradient = new LinearGradientBrush
    {
        StartPoint = new Point(0, 0.5),
        EndPoint = new Point(1, 0.5),
        GradientStops =
        {
            new GradientStop(Color.FromArgb("#663E618A"), 0),
            new GradientStop(Color.FromArgb("#C04C99D8"), 0.5f),
            new GradientStop(Color.FromArgb("#A05C4C9B"), 1),
        },
    };

    public NexBadge()
    {
        StrokeShape = new RoundRectangle { CornerRadius = 8 };
        StrokeThickness = 0;
        Padding = new Thickness(8, 2);
        VerticalOptions = LayoutOptions.Center;
        Background = Gradient;
        Content = new Label { Text = "Nex", FontSize = 11, FontFamily = "OpenSansSemibold", TextColor = Color.FromArgb("#F2F2FF") };
        SemanticProperties.SetDescription(this, "Nex");
    }
}
