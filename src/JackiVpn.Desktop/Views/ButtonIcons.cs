using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JackiVpn.Desktop.Views;

public static class ButtonIcons
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ButtonIcons),
        new PropertyMetadata(false, (sender, e) =>
        {
            if (sender is not Button button) { return; }
            if ((bool)e.NewValue) { button.Loaded += OnLoaded; }
            else { button.Loaded -= OnLoaded; }
        }));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["previous"] = "\uE76B", ["next"] = "\uE76C", ["add"] = "\uE710", ["import"] = "\uE8B5",
        ["export"] = "\uE8B6", ["refresh"] = "\uE72C", ["edit"] = "\uE70F", ["delete"] = "\uE74D",
        ["close"] = "\uE711", ["copy"] = "\uE8C8", ["save"] = "\uE74E", ["music"] = "\uE8D6",
        ["profile"] = "\uE77B", ["logout"] = "\uE8AC",
        ["connect"] = "\uE768", ["disconnect"] = "\uE71A", ["cancel"] = "\uE711", ["clipboard"] = "\uE77F",
    };

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string label } button && IconFor(label) is { } icon) { Apply(button, icon, label); }
    }

    private static string? IconFor(string text)
    {
        if (text.Contains("Telegram", StringComparison.OrdinalIgnoreCase) || text == "Use the IBOX bot instead") { return "telegram"; }
        if (text.StartsWith("Add", StringComparison.OrdinalIgnoreCase)) { return "add"; }
        if (text.Contains("Import", StringComparison.OrdinalIgnoreCase) || text.StartsWith("From file", StringComparison.OrdinalIgnoreCase)) { return "import"; }
        if (text == "From clipboard") { return "clipboard"; }
        if (text.StartsWith("Export", StringComparison.OrdinalIgnoreCase)) { return "export"; }
        if (text.StartsWith("Update", StringComparison.OrdinalIgnoreCase) || text == "Refresh") { return "refresh"; }
        if (text.StartsWith("Copy", StringComparison.OrdinalIgnoreCase)) { return "copy"; }
        if (text == "Open account") { return "profile"; }
        return Glyphs.ContainsKey(text) ? text.ToLowerInvariant() : null;
    }

    public static void Apply(Button button, string icon, string label)
    {
        button.SetCurrentValue(ContentControl.ContentProperty, Content(icon, label));
        System.Windows.Automation.AutomationProperties.SetName(button, label);
    }

    public static FrameworkElement Content(string icon, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement symbol = icon == "telegram"
            ? new Image { Source = TelegramImage, Width = 18, Height = 18 }
            : new TextBlock { Text = Glyphs.GetValueOrDefault(icon, "\uE946"), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
        symbol.Margin = new Thickness(0, 0, 8, 0);
        symbol.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(symbol);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    public static DrawingImage TelegramImage => Services.BrandIcons.Telegram;
}
