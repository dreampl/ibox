using System.Windows;
using System.Windows.Controls;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// A "clay" card: matte surface, faint top highlight, and two soft shadows (dark below, light above). The shadows are
/// separate layers behind the content with a bitmap cache, so they are rasterized once — content that changes (a
/// live chart, the eye) never re-renders them, and text keeps ClearType (an effect on the card itself would turn
/// its text to grayscale antialiasing). Style: Themes/Controls.xaml.
/// </summary>
public sealed class ClaySurface : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(ClaySurface), new FrameworkPropertyMetadata(new CornerRadius(20)));

    /// <summary>Raised = stronger shadow (the dashboard hero), Flat = no shadow (nested surfaces).</summary>
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.Register(
        nameof(Elevation), typeof(ClayElevation), typeof(ClaySurface), new FrameworkPropertyMetadata(ClayElevation.Normal));

    static ClaySurface() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ClaySurface), new FrameworkPropertyMetadata(typeof(ClaySurface)));

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public ClayElevation Elevation { get => (ClayElevation)GetValue(ElevationProperty); set => SetValue(ElevationProperty, value); }
}

public enum ClayElevation
{
    Normal = 0,
    Flat,
    Raised,
}
