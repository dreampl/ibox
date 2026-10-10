using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Swaps the active color dictionary (Dark / Black / Blue) at runtime. Brushes are derived from the theme's
/// colors in code so every theme defines only its palette; controls use DynamicResource and restyle live.
/// </summary>
public static partial class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private static ResourceDictionary? _current;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/JackiVpn;component/Themes/Colors.{theme}.xaml", UriKind.Absolute) };
        var dict = new ResourceDictionary();
        foreach (DictionaryEntry entry in palette)
        {
            dict[entry.Key] = entry.Value;
            if (entry.Key is string key && key.StartsWith("C.", StringComparison.Ordinal) && entry.Value is Color color)
            {
                dict["B." + key[2..]] = Frozen(new SolidColorBrush(color));
            }
        }

        Color C(string key) => (Color)palette[key];
        dict["B.AccentGradient"] = Gradient(C("C.Accent"), C("C.Accent2"));
        dict["B.SuccessGradient"] = Gradient(C("C.Success"), C("C.Success2"));
        dict["B.IdleGradient"] = Gradient(C("C.Idle"), C("C.Idle2"));
        dict["B.DangerGradient"] = Gradient(C("C.Danger"), Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C));
        dict["B.AccentGradientH"] = Frozen(new LinearGradientBrush(C("C.Accent"), C("C.Accent2"), new Point(0, 0.5), new Point(1, 0.5)));
        dict["B.WindowGlow"] = Glow(C("C.Accent"), 0x22, new Point(0.85, 0.0), 0.9);
        dict["B.HeroGlowIdle"] = Glow(C("C.Accent"), 0x14, new Point(0.25, 0.5), 0.75);
        dict["B.HeroGlowBusy"] = Glow(C("C.Accent"), 0x38, new Point(0.25, 0.5), 0.75);
        dict["B.HeroGlowOn"] = Glow(C("C.Success"), 0x34, new Point(0.25, 0.5), 0.75);
        dict["B.HeroGlowFail"] = Glow(C("C.Danger"), 0x30, new Point(0.25, 0.5), 0.75);
        dict["B.SparkDown"] = Frozen(new LinearGradientBrush(WithAlpha(C("C.Success"), 0x66), WithAlpha(C("C.Success"), 0x00), 90));
        dict["B.SparkUp"] = Frozen(new LinearGradientBrush(WithAlpha(C("C.Accent"), 0x55), WithAlpha(C("C.Accent"), 0x00), 90));
        dict["B.HoverOverlay"] = Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
        dict["B.PressOverlay"] = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)));
        dict["B.AccentSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Accent"), 0x2E)));
        dict["B.SuccessSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Success"), 0x26)));
        dict["B.WarningSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Warning"), 0x22)));
        dict["B.DangerSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Danger"), 0x22)));

        var merged = app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }

        merged.Insert(0, dict);
        _current = dict;
        Current = theme;
        foreach (Window w in app.Windows)
        {
            ApplyTitleBar(w);
        }
    }

    /// <summary>Dark native title bar (Windows 10 20H1+) and, on Windows 11, a caption matching the theme.</summary>
    public static void ApplyTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || _current is null)
        {
            return;
        }

        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        if (_current["C.Window"] is Color c)
        {
            var colorRef = c.R | (c.G << 8) | (c.B << 16);
            _ = DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref colorRef, sizeof(int));
        }
    }

    private static Brush Glow(Color c, byte alpha, Point center, double radius) => Frozen(new RadialGradientBrush
    {
        GradientOrigin = center,
        Center = center,
        RadiusX = radius,
        RadiusY = radius,
        GradientStops = [new GradientStop(WithAlpha(c, alpha), 0), new GradientStop(WithAlpha(c, 0), 1)],
    });

    private static Brush Gradient(Color a, Color b) =>
        Frozen(new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1)));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
