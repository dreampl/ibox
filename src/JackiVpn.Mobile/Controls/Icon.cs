using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// Line icons on a 24×24 grid (round caps and joins, Lucide-style geometry drawn for this app). One control so
/// every icon shares stroke weight and can animate its colour and fill.
/// </summary>
public sealed class Icon : ContentView
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["home"] = "M3.5 10.5 12 3.5l8.5 7V20a1 1 0 0 1-1 1H15v-6H9v6H4.5a1 1 0 0 1-1-1z",
        ["servers"] = "M4 4.5h16v6H4z M4 13.5h16v6H4z M8 7.5h.01 M8 16.5h.01",
        ["layers"] = "M12 3 3 7.5 12 12 21 7.5z M3 12 12 16.5 21 12 M3 16.5 12 21 21 16.5",
        ["sliders"] = "M4 6h9 M17 6h3 M15 4v4 M4 12h3 M11 12h9 M9 10v4 M4 18h11 M19 18h1 M17 16v4",
        ["power"] = "M12 2.5v9 M7.05 6.05a7.75 7.75 0 1 0 9.9 0",
        ["globe"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M3 12h18 M12 3c2.4 2.5 3.6 5.5 3.6 9s-1.2 6.5-3.6 9c-2.4-2.5-3.6-5.5-3.6-9S9.6 5.5 12 3z",
        ["bolt"] = "M13 2.5 4.5 13.5H11l-1 8 8.5-11H12z",
        ["shield"] = "M12 3 5 6v6c0 4.4 3 7.6 7 9 4-1.4 7-4.6 7-9V6z",
        ["sync"] = "M4 8h13l-3-3 M20 16H7l3 3",
        ["down"] = "M12 4v12 M7 11l5 5 5-5 M5 20h14",
        ["up"] = "M12 20V8 M7 13l5-5 5 5 M5 4h14",
        ["clock"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 7.5V12l3 2",
        ["star"] = "M12 3.5l2.6 5.3 5.9.9-4.3 4.2 1 5.8L12 17l-5.2 2.7 1-5.8-4.3-4.2 5.9-.9z",
        ["search"] = "M10.5 4a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13z M20 20l-4.8-4.8",
        ["plus"] = "M12 5v14 M5 12h14",
        ["refresh"] = "M20 12a8 8 0 1 1-2.3-5.6 M20 4v5h-5",
        ["chevron"] = "M9 6l6 6-6 6",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["back"] = "M15 6l-6 6 6 6",
        ["more"] = "M5 12h.01 M12 12h.01 M19 12h.01",
        ["language"] = "M4 5h9 M8.5 3v2 M5.5 5c1 3.5 3.5 6 7 7.5 M11.5 5c-1 4-3.5 7-7.5 8.5 M13 20l4-9 4 9 M14.5 17h5",
        ["gamepad"] = "M6.5 8.5h11a4 4 0 0 1 3.9 4.9l-.6 2.8a2.4 2.4 0 0 1-4.2 1L15 15H9l-1.6 2.2a2.4 2.4 0 0 1-4.2-1l-.6-2.8a4 4 0 0 1 3.9-4.9z M8 10.5v3 M6.5 12h3 M15.5 11.5h.01 M17.5 13h.01",
        ["tv"] = "M3.5 6h17v11h-17z M8 21h8 M12 17v4",
        ["apps"] = "M4.5 4.5h6v6h-6z M13.5 4.5h6v6h-6z M4.5 13.5h6v6h-6z M13.5 13.5h6v6h-6z",
        ["lock"] = "M6 11h12v9.5H6z M8.5 11V8a3.5 3.5 0 0 1 7 0v3",
        ["info"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 11v5.5 M12 7.5h.01",
        ["update"] = "M12 12.5v8 M8.5 17l3.5 3.5 3.5-3.5 M19.5 15.8A4.6 4.6 0 0 0 17.5 7h-1.2A7.5 7.5 0 1 0 4.5 14.8",
        ["file"] = "M6 3h9l4 4v14H6z M14.5 3v4.5H19 M9 13h6 M9 17h6",
        ["widget"] = "M4 4h16v16H4z M4 10h16 M10 10v10",
        ["phone"] = "M7 2.5h10v19H7z M11 18.5h2",
        ["trash"] = "M4 7h16 M9.5 7V4.5h5V7 M6.5 7l1 13h9l1-13",
        ["edit"] = "M4 20h4L19 9l-4-4L4 16z M13.5 6.5l4 4",
        ["copy"] = "M8.5 8.5h11v11h-11z M5 15.5H4.5v-11h11V5",
        ["check"] = "M5 12.5l4.5 4.5L19 7.5",
        ["x"] = "M6 6l12 12 M18 6 6 18",
        ["arrow"] = "M5 12h14 M13 6l6 6-6 6",
        ["route"] = "M6 19.5a2 2 0 1 0 0-4a2 2 0 1 0 0 4z M18 8.5a2 2 0 1 0 0-4a2 2 0 1 0 0 4z M6 15.5V10a4 4 0 0 1 4-4h6 M18 8.5V14a4 4 0 0 1-4 4H8",
        ["fingerprint"] = "M12 11v3.5 M8 7.5A6 6 0 0 1 18 12v2 M6 12a6 6 0 0 1 .6-2.6 M9 21c1-2 1.5-4 1.5-7a1.5 1.5 0 0 1 3 0c0 3.5-.5 5.5-1.5 7.5 M16.5 20c.6-1.6 1-3.5 1-6",
        ["wifi"] = "M2.5 9a14 14 0 0 1 19 0 M5.5 12.5a9.5 9.5 0 0 1 13 0 M8.5 16a5 5 0 0 1 7 0 M12 19.5h.01",
        ["key"] = "M8 15.5a3.5 3.5 0 1 0 0-7a3.5 3.5 0 1 0 0 7z M11.5 12H21 M18 12v3 M15.5 12v2",
        ["sparkle"] = "M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z M19 16l.7 1.8 1.8.7-1.8.7L19 21l-.7-1.8-1.8-.7 1.8-.7z",
        ["devices"] = "M3 5h12v9H3z M1.5 17h15 M18 8h4v11h-4z",
        ["qr"] = "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h2.5v2.5H14z M18.5 14H20 M14 19h2 M18.5 17.5V20H20 M6.8 6.8h.4 M16.8 6.8h.4 M6.8 16.8h.4",
        ["chart"] = "M4 20h16 M6.5 20v-6 M11 20V8 M15.5 20v-9 M20 20V5",
        ["paste"] = "M9 4.5h6v3H9z M7 6H5.5v14.5h13V6H17 M9 12h6 M9 16h4",
        ["camera"] = "M4 8h3l1.8-2.5h6.4L17 8h3v11H4z M12 10.5a3.25 3.25 0 1 0 0 6.5a3.25 3.25 0 1 0 0-6.5z",
        ["flash"] = "M13 2.5 5.5 13H11l-1 8.5 7.5-11H12z",
        ["eco"] = "M5 19c0-8 5-13 14-14-1 9-6 14-14 14z M5 19l7-7",
    };

    /// <summary>Directional icons drawn the other way for right-to-left (Persian) layouts.</summary>
    private static readonly Dictionary<string, string> RtlPaths = new(StringComparer.Ordinal)
    {
        ["chevron"] = "M15 6l-6 6 6 6",
        ["back"] = "M9 6l6 6-6 6",
        ["arrow"] = "M19 12H5 M11 6l-6 6 6 6",
        ["sync"] = "M20 8H7l3-3 M4 16h13l-3 3",
    };

    public static readonly BindableProperty NameProperty = BindableProperty.Create(nameof(Name), typeof(string), typeof(Icon), string.Empty,
        propertyChanged: (b, _, n) => ((Icon)b).Apply((string)n));

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(nameof(Size), typeof(double), typeof(Icon), 22d,
        propertyChanged: (b, _, _) => ((Icon)b).Resize());

    public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(Icon), Colors.White,
        propertyChanged: (b, _, n) => ((Icon)b)._path.Stroke = new SolidColorBrush((Color)n));

    public static readonly BindableProperty StrokeThicknessProperty = BindableProperty.Create(nameof(StrokeThickness), typeof(double), typeof(Icon), 1.8,
        propertyChanged: (b, _, n) => ((Icon)b)._path.StrokeThickness = (double)n);

    public static readonly BindableProperty FillProperty = BindableProperty.Create(nameof(Fill), typeof(Brush), typeof(Icon), null,
        propertyChanged: (b, _, n) => ((Icon)b)._path.Fill = (Brush?)n!);

    private readonly Microsoft.Maui.Controls.Shapes.Path _path = new()
    {
        Aspect = Stretch.None,
        StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Stroke = new SolidColorBrush(Colors.White),
        InputTransparent = true,
    };

    public Icon()
    {
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Center;
        InputTransparent = true;
        FlowDirection = FlowDirection.LeftToRight; // geometry is chosen per language; never mirrored again
        Content = _path;
        Resize();
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public string Name
    {
        get => (string)GetValue(NameProperty);
        set => SetValue(NameProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private void Resize()
    {
        WidthRequest = Size;
        HeightRequest = Size;
        _path.WidthRequest = Size;
        _path.HeightRequest = Size;
        var s = Size / 24;

        _path.RenderTransform = new ScaleTransform(s, s);
    }

    private void Apply(string name)
    {
        if ((Localization.L.IsPersian && RtlPaths.TryGetValue(name, out var data)) || Paths.TryGetValue(name, out data))
        {
            _path.Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(data)!;
        }

        Resize();
    }
}
