using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>A round country flag (vector, from <see cref="Flags"/>); an initials badge for countries without one.</summary>
public sealed class FlagView : FrameworkElement
{
    public static readonly DependencyProperty CountryCodeProperty = DependencyProperty.Register(
        nameof(CountryCode), typeof(string), typeof(FlagView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Dictionary<uint, SolidColorBrush> Brushes = [];

    public FlagView()
    {
        Loaded += (_, _) => ThemeManager.Changed += OnTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => InvalidateVisual();

    public string? CountryCode { get => (string?)GetValue(CountryCodeProperty); set => SetValue(CountryCodeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4)
        {
            return;
        }

        var ox = (ActualWidth - size) / 2;
        var oy = (ActualHeight - size) / 2;
        var circle = new EllipseGeometry(new Point(ox + (size / 2), oy + (size / 2)), size / 2, size / 2);
        var shapes = Flags.For(CountryCode);
        if (shapes is null)
        {
            // No drawn flag: the country code on the accent gradient.
            dc.DrawGeometry(TryFindResource("B.AccentGradient") as Brush ?? System.Windows.Media.Brushes.SteelBlue, null, circle);
            var text = new FormattedText(Flags.Initials(CountryCode), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.36, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(ox + ((size - text.Width) / 2), oy + ((size - text.Height) / 2)));
            return;
        }

        dc.PushClip(circle);
        foreach (var s in shapes)
        {
            var brush = Brush(s.Color);
            var p = s.Points;
            switch (s.Kind)
            {
                case FlagShapeKind.Rect:
                    // A hair of overlap so adjacent stripes never show a seam.
                    dc.DrawRectangle(brush, null, new Rect(ox + (p[0] * size) - 0.3, oy + (p[1] * size) - 0.3, (p[2] * size) + 0.6, (p[3] * size) + 0.6));
                    break;
                case FlagShapeKind.Circle:
                    dc.DrawEllipse(brush, null, new Point(ox + (p[0] * size), oy + (p[1] * size)), p[2] * size, p[2] * size);
                    break;
                case FlagShapeKind.Polygon:
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(new Point(ox + (p[0] * size), oy + (p[1] * size)), isFilled: true, isClosed: true);
                        for (var i = 2; i + 1 < p.Length; i += 2)
                        {
                            ctx.LineTo(new Point(ox + (p[i] * size), oy + (p[i + 1] * size)), isStroked: false, isSmoothJoin: false);
                        }
                    }

                    geometry.Freeze();
                    dc.DrawGeometry(brush, null, geometry);
                    break;
            }
        }

        dc.Pop();

        // A soft rim and a top sheen make the flat flag sit like a small clay button.
        dc.DrawGeometry(Sheen, new Pen(Rim, 1), circle);
    }

    private static readonly Brush Sheen = Frozen(new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), new Point(0.5, 0), new Point(0.5, 0.6)));
    private static readonly Brush Rim = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)));

    private static SolidColorBrush Brush(uint argb)
    {
        if (!Brushes.TryGetValue(argb, out var brush))
        {
            brush = Frozen(new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)));
            Brushes[argb] = brush;
        }

        return brush;
    }

    private static T Frozen<T>(T f)
        where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
