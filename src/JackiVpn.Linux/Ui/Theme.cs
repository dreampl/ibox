using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// The token set shared with the Windows app (Themes/Colors.Dark.xaml and Colors.Light.xaml): background, surfaces,
/// border, text, accent, status, chart, shadows. Dark: deep navy-slate matte with slightly raised clay cards. Light:
/// cool grey-white with white clay cards, pastel mint orb and soft blue accents.
/// </summary>
public sealed record Palette
{
    public required bool IsLight { get; init; }
    public required Color Window { get; init; }
    public required Color Window2 { get; init; }
    public required Color Sidebar { get; init; }
    public required Color Card { get; init; }
    public required Color Card2 { get; init; }
    public required Color CardHover { get; init; }
    public required Color Tile { get; init; }
    public required Color Input { get; init; }
    public required Color Border { get; init; }
    public required Color Highlight { get; init; }
    public required Color Shadow { get; init; }
    public required Color ShadowLight { get; init; }
    public required Color Text { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextMuted { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color Accent2 { get; init; }
    public required Color Success { get; init; }
    public required Color Success2 { get; init; }
    public required Color Warning { get; init; }
    public required Color Danger { get; init; }
    public required Color Idle { get; init; }
    public required Color Idle2 { get; init; }
    public required Color Chart1 { get; init; }
    public required Color Chart2 { get; init; }
    public required Color MapDot { get; init; }
    public required Color OrbOn1 { get; init; }
    public required Color OrbOn2 { get; init; }
    public required Color OrbOn3 { get; init; }
    public required double ShadowOpacity { get; init; }
    public required double ShadowLightOpacity { get; init; }

    public static Palette Dark { get; } = new()
    {
        IsLight = false,
        Window = Color.Parse("#0D1322"),
        Window2 = Color.Parse("#111A2C"),
        Sidebar = Color.Parse("#111930"),
        Card = Color.Parse("#161F33"),
        Card2 = Color.Parse("#131B2D"),
        CardHover = Color.Parse("#1C2740"),
        Tile = Color.Parse("#1D2840"),
        Input = Color.Parse("#19233A"),
        Border = Color.Parse("#263350"),
        Highlight = Color.Parse("#1CFFFFFF"),
        Shadow = Color.Parse("#02050C"),
        ShadowLight = Color.Parse("#0AFFFFFF"),
        Text = Color.Parse("#F1F5F9"),
        TextSecondary = Color.Parse("#94A3B8"),
        TextMuted = Color.Parse("#64748B"),
        Accent = Color.Parse("#3B82F6"),
        AccentHover = Color.Parse("#5B97F8"),
        Accent2 = Color.Parse("#3B82F6"),
        Success = Color.Parse("#22C55E"),
        Success2 = Color.Parse("#10B981"),
        Warning = Color.Parse("#F59E0B"),
        Danger = Color.Parse("#EF4444"),
        Idle = Color.Parse("#2A3550"),
        Idle2 = Color.Parse("#1B2438"),
        Chart1 = Color.Parse("#22C55E"),
        Chart2 = Color.Parse("#3B82F6"),
        MapDot = Color.Parse("#2B3A57"),
        OrbOn1 = Color.Parse("#34D399"),
        OrbOn2 = Color.Parse("#0E9F6E"),
        OrbOn3 = Color.Parse("#06654A"),
        ShadowOpacity = 0.55,
        ShadowLightOpacity = 0.6,
    };

    public static Palette Light { get; } = new()
    {
        IsLight = true,
        Window = Color.Parse("#EDF1F7"),
        Window2 = Color.Parse("#F5F7FB"),
        Sidebar = Color.Parse("#F6F8FC"),
        Card = Color.Parse("#FFFFFF"),
        Card2 = Color.Parse("#FAFBFE"),
        CardHover = Color.Parse("#F3F6FB"),
        Tile = Color.Parse("#EFF3F9"),
        Input = Color.Parse("#F1F4F9"),
        Border = Color.Parse("#E2E8F0"),
        Highlight = Color.Parse("#FFFFFF"),
        Shadow = Color.Parse("#7C8DB0"),
        ShadowLight = Color.Parse("#FFFFFF"),
        Text = Color.Parse("#1E293B"),
        TextSecondary = Color.Parse("#64748B"),
        TextMuted = Color.Parse("#94A3B8"),
        Accent = Color.Parse("#3B82F6"),
        AccentHover = Color.Parse("#2F74E8"),
        Accent2 = Color.Parse("#3B82F6"),
        Success = Color.Parse("#22C55E"),
        Success2 = Color.Parse("#34D399"),
        Warning = Color.Parse("#F59E0B"),
        Danger = Color.Parse("#EF4444"),
        Idle = Color.Parse("#E3E9F2"),
        Idle2 = Color.Parse("#CDD6E3"),
        Chart1 = Color.Parse("#4ADE80"),
        Chart2 = Color.Parse("#60A5FA"),
        MapDot = Color.Parse("#D4DEEB"),
        OrbOn1 = Color.Parse("#D1FAE5"),
        OrbOn2 = Color.Parse("#86EFAC"),
        OrbOn3 = Color.Parse("#34D399"),
        ShadowOpacity = 0.22,
        ShadowLightOpacity = 0.9,
    };
}

/// <summary>System / Light / Dark, applied live: the palette changes and the window rebuilds its pages.</summary>
public static class Look
{
    private static bool _listening;

    /// <summary>The palette every page is drawn with.</summary>
    public static Palette P { get; private set; } = Palette.Dark;

    /// <summary>The user's choice (may be System).</summary>
    public static AppTheme Choice { get; private set; } = AppTheme.System;

    /// <summary>Raised after the palette changed (the window rebuilds; custom-drawn views repaint).</summary>
    public static event EventHandler? Changed;

    public static void Apply(AppTheme choice)
    {
        Choice = choice;
        var app = Avalonia.Application.Current;
        var platform = app?.PlatformSettings;
        if (platform is not null && !_listening)
        {
            _listening = true;
            platform.ColorValuesChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Choice == AppTheme.System)
                {
                    Apply(AppTheme.System);
                }
            });
        }

        var light = choice switch
        {
            AppTheme.Light => true,
            AppTheme.Dark => false,
            _ => platform?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light,
        };
        var next = light ? Palette.Light : Palette.Dark;
        if (app is not null)
        {
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            app.Resources["SystemAccentColor"] = next.Accent;
            app.Resources["SystemAccentColorLight1"] = next.AccentHover;
            app.Resources["SystemAccentColorDark1"] = Color.Parse("#2F6FD6");

            // Inputs and drop-downs on the token set (Fluent's defaults are grey-black in light mode).
            var input = new SolidColorBrush(next.Input);
            var border = new SolidColorBrush(next.Border);
            var text = new SolidColorBrush(next.Text);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "NumericUpDownBackground" })
            {
                app.Resources[key] = input;
            }

            foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver" })
            {
                app.Resources[key] = border;
            }

            app.Resources["TextControlForeground"] = text;
            app.Resources["ComboBoxForeground"] = text;
            app.Resources["FlyoutPresenterBackground"] = new SolidColorBrush(next.Card);
            app.Resources["FlyoutBorderThemeBrush"] = border;
            app.Resources["MenuFlyoutPresenterBackground"] = new SolidColorBrush(next.Card);
            app.Resources["ControlCornerRadius"] = new Avalonia.CornerRadius(12);
            app.Resources["OverlayCornerRadius"] = new Avalonia.CornerRadius(14);
        }

        if (next != P)
        {
            P = next;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
