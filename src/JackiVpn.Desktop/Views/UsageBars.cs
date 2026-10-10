using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace JackiVpn.Desktop.Views;

/// <summary>Column chart of download (bottom, blue) and upload (stacked, orange); columns ≤ 24 px with round tops.</summary>
public sealed class UsageBars : FrameworkElement
{
    public static readonly DependencyProperty DownProperty = DependencyProperty.Register(
        nameof(Down), typeof(IReadOnlyList<double>), typeof(UsageBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpProperty = DependencyProperty.Register(
        nameof(Up), typeof(IReadOnlyList<double>), typeof(UsageBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(
        nameof(DownBrush), typeof(Brush), typeof(UsageBars), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(
        nameof(UpBrush), typeof(Brush), typeof(UsageBars), new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(UsageBars), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Down { get => (IReadOnlyList<double>?)GetValue(DownProperty); set => SetValue(DownProperty, value); }

    public IReadOnlyList<double>? Up { get => (IReadOnlyList<double>?)GetValue(UpProperty); set => SetValue(UpProperty, value); }

    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }

    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var down = Down ?? [];
        var up = Up ?? [];
        var n = Math.Max(down.Count, up.Count);
        var w = ActualWidth;
        var h = ActualHeight - 14;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (n == 0 || w < 20 || h < 10)
        {
            return;
        }

        var max = Enumerable.Range(0, n).Max(i => (i < down.Count ? down[i] : 0) + (i < up.Count ? up[i] : 0));
        if (max <= 0)
        {
            max = 1;
        }

        var slot = w / n;
        var bar = Math.Min(24, Math.Max(2, slot - 2));
        for (var i = 0; i < n; i++)
        {
            var d = i < down.Count ? down[i] : 0;
            var u = i < up.Count ? up[i] : 0;
            var x = (i * slot) + ((slot - bar) / 2);
            var dh = d / max * h;
            var uh = u / max * h;
            var r = Math.Min(bar / 2, 3);
            if (uh > 0.5)
            {
                dc.DrawRoundedRectangle(UpBrush, null, new Rect(x, h - dh - uh, bar, uh + Math.Min(r, dh)), r, r);
            }

            if (dh > 0.5)
            {
                dc.DrawRoundedRectangle(DownBrush, null, new Rect(x, h - dh, bar, dh), uh > 0.5 ? 0 : r, uh > 0.5 ? 0 : r);
            }
        }

        var label = new FormattedText(Max(max), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(0, ActualHeight - 13));
    }

    private static string Max(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (bytes >= 1024 && u < units.Length - 1)
        {
            bytes /= 1024;
            u++;
        }

        return "max " + bytes.ToString(bytes < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }
}
