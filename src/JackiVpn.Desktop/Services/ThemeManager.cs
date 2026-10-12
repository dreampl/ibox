using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using JackiVpn.Domain.Settings;
using Microsoft.Win32;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Swaps the active color dictionary at runtime: System (follows the Windows app theme, live), Light or Dark. Every
/// theme defines the same token set (background, surfaces, border, text, accent, status, chart, shadows); the brushes,
/// gradients and clay shadows are derived here, and every control uses DynamicResource, so the running UI restyles
/// without a restart.
/// </summary>
public static partial class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private static ResourceDictionary? _current;
    private static bool _listening;

    /// <summary>The theme the user chose (may be <see cref="AppTheme.System"/>).</summary>
    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>The palette actually shown.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Raised after the palette changed (custom-drawn views refresh their colours).</summary>
    public static event EventHandler? Changed;

    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        Current = theme;
        if (theme == AppTheme.System && !_listening)
        {
            // Windows switched between light and dark apps: follow at once.
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
                {
                    app.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
                }
            };
        }

        var light = theme switch
        {
            AppTheme.Light => true,
            AppTheme.System => WindowsUsesLightApps(),
            _ => false,
        };
        if (_current is not null && light == IsLight)
        {
            return;
        }

        IsLight = light;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/JackiVpn;component/Themes/Colors.{(light ? "Light" : "Dark")}.xaml", UriKind.Absolute) };
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
        var shadowOpacity = (double)palette["ShadowOpacity"];
        var lightOpacity = (double)palette["ShadowLightOpacity"];
        dict["B.AccentGradient"] = Gradient(C("C.Accent"), C("C.Accent2"));
        dict["B.SuccessGradient"] = Gradient(C("C.Success"), C("C.Success2"));
        dict["B.IdleGradient"] = Frozen(new LinearGradientBrush(C("C.Idle"), C("C.Idle2"), new Point(0.2, 0), new Point(0.8, 1)));
        dict["B.DangerGradient"] = Gradient(C("C.Danger"), Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C));
        dict["B.AccentGradientH"] = Frozen(new LinearGradientBrush(C("C.Accent"), C("C.Accent2"), new Point(0, 0.5), new Point(1, 0.5)));
        dict["B.WindowGlow"] = Glow(C("C.Accent"), light ? (byte)0x10 : (byte)0x16, new Point(0.85, 0.0), 0.9);
        dict["B.WindowFill"] = Frozen(new LinearGradientBrush(C("C.Window"), C("C.Window2"), new Point(0, 0), new Point(1, 1)));
        dict["B.HeroGlowIdle"] = Glow(C("C.Accent"), 0x0C, new Point(0.2, 0.5), 0.7);
        dict["B.HeroGlowBusy"] = Glow(C("C.Accent"), 0x22, new Point(0.2, 0.5), 0.7);
        dict["B.HeroGlowOn"] = Glow(C("C.Success"), light ? (byte)0x1C : (byte)0x20, new Point(0.2, 0.5), 0.7);
        dict["B.HeroGlowFail"] = Glow(C("C.Danger"), 0x20, new Point(0.2, 0.5), 0.7);
        dict["B.SparkDown"] = Frozen(new LinearGradientBrush(WithAlpha(C("C.Chart1"), light ? (byte)0x55 : (byte)0x66), WithAlpha(C("C.Chart1"), 0x00), 90));
        dict["B.SparkUp"] = Frozen(new LinearGradientBrush(WithAlpha(C("C.Chart2"), light ? (byte)0x44 : (byte)0x55), WithAlpha(C("C.Chart2"), 0x00), 90));
        dict["B.HoverOverlay"] = Frozen(new SolidColorBrush(light ? Color.FromArgb(0x0C, 0x1E, 0x29, 0x3B) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
        dict["B.PressOverlay"] = Frozen(new SolidColorBrush(Color.FromArgb(light ? (byte)0x14 : (byte)0x22, 0x00, 0x00, 0x00)));
        dict["B.AccentSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Accent"), light ? (byte)0x1C : (byte)0x2E)));
        dict["B.SuccessSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Success"), light ? (byte)0x1C : (byte)0x26)));
        dict["B.WarningSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Warning"), light ? (byte)0x1C : (byte)0x22)));
        dict["B.DangerSoft"] = Frozen(new SolidColorBrush(WithAlpha(C("C.Danger"), light ? (byte)0x18 : (byte)0x22)));

        // Clay: a matte surface that is a touch lighter at the top, a faint top-edge highlight instead of a border,
        // and two soft shadows (dark below-right, light above-left).
        dict["B.CardFill"] = Frozen(new LinearGradientBrush(C("C.Card"), C("C.Card2"), new Point(0.5, 0), new Point(0.5, 1)));
        dict["B.CardEdge"] = Frozen(new LinearGradientBrush(
            [new GradientStop(C("C.Highlight"), 0), new GradientStop(WithAlpha(C("C.Highlight"), light ? (byte)0x80 : (byte)0x08), 0.35), new GradientStop(WithAlpha(C("C.Border"), light ? (byte)0xB0 : (byte)0x50), 1)],
            new Point(0.5, 0), new Point(0.5, 1)));
        dict["B.TileFill"] = Frozen(new LinearGradientBrush(C("C.CardHover"), C("C.Tile"), new Point(0.5, 0), new Point(0.5, 1)));
        dict["B.OrbOn"] = Frozen(new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops = [new GradientStop(C("C.OrbOn1"), 0), new GradientStop(C("C.OrbOn2"), 0.55), new GradientStop(C("C.OrbOn3"), 1)],
        });
        dict["B.OrbIdle"] = Frozen(new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops = [new GradientStop(light ? Colors.White : C("C.CardHover"), 0), new GradientStop(C("C.Idle"), 0.6), new GradientStop(C("C.Idle2"), 1)],
        });
        dict["E.ClayShadow"] = Frozen(new DropShadowEffect { Color = C("C.Shadow"), BlurRadius = light ? 34 : 30, ShadowDepth = light ? 10 : 8, Direction = 290, Opacity = shadowOpacity, RenderingBias = RenderingBias.Performance });
        dict["E.ClayShadowRaised"] = Frozen(new DropShadowEffect { Color = C("C.Shadow"), BlurRadius = light ? 44 : 40, ShadowDepth = light ? 14 : 12, Direction = 290, Opacity = Math.Min(1, shadowOpacity * 1.15), RenderingBias = RenderingBias.Performance });

        // The white glow only shows on the light theme; on the dark one it would be invisible work for the GPU.
        dict["V.LightOnly"] = light ? Visibility.Visible : Visibility.Collapsed;
        dict["E.ClayLight"] = light ? Frozen(new DropShadowEffect { Color = C("C.ShadowLight"), BlurRadius = 18, ShadowDepth = 6, Direction = 120, Opacity = lightOpacity, RenderingBias = RenderingBias.Performance }) : null;
        dict["E.SoftShadow"] = Frozen(new DropShadowEffect { Color = C("C.Shadow"), BlurRadius = 16, ShadowDepth = 4, Direction = 290, Opacity = shadowOpacity * 0.8, RenderingBias = RenderingBias.Performance });
        dict["E.AccentGlow"] = Frozen(new DropShadowEffect { Color = C("C.Accent"), BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = light ? 0.35 : 0.5, RenderingBias = RenderingBias.Performance });

        var merged = app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }

        merged.Insert(0, dict);
        _current = dict;
        foreach (Window w in app.Windows)
        {
            ApplyTitleBar(w);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Windows' "app mode" (Settings → Personalization → Colors): light unless set to dark.</summary>
    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Native title bar in the theme (dark mode flag on Windows 10 20H1+, caption colour on Windows 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || _current is null)
        {
            return;
        }

        var dark = IsLight ? 0 : 1;
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
