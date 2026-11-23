using System.Windows;
using System.Windows.Media;

namespace JackiVpn.Desktop.Services;

public static class BrandIcons
{
    public static DrawingImage Telegram { get; } = MakeTelegram();

    private static DrawingImage MakeTelegram()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(42, 171, 238)), null, new EllipseGeometry(new Point(12, 12), 12, 12)));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, Geometry.Parse("M4,11 L20,5 L17,20 L11,15 L8,18 L9,13 L17,8 L10,13 Z")));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
