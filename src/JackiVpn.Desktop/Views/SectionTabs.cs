using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace JackiVpn.Desktop.Views;

internal static class SectionTabs
{
    public static void Apply(UserControl page)
    {
        if (page.Content is not ScrollViewer { Content: StackPanel stack } scroll) { return; }
        var children = stack.Children.Cast<UIElement>().ToArray();
        var groups = new List<(string Title, StackPanel Panel)>();
        StackPanel? current = null;
        foreach (var child in children)
        {
            var heading = FindHeading(child);
            if (current is null || heading is not null)
            {
                current = new StackPanel();
                groups.Add((heading ?? "Overview", current));
            }
            stack.Children.Remove(child);
            current.Children.Add(child);
        }
        scroll.Content = null;
        if (groups.Count > 1 && groups[0].Panel.Children.Cast<UIElement>().All(c => c is TextBlock))
        {
            var intro = groups[0].Panel.Children.Cast<UIElement>().ToArray();
            groups[0].Panel.Children.Clear();
            groups.RemoveAt(0);
            for (var i = 0; i < intro.Length; i++) { groups[0].Panel.Children.Insert(i, intro[i]); }
        }
        var tabs = new TabControl { Margin = new Thickness(20, 0, 24, 20) };
        foreach (var (title, panel) in groups)
        {
            panel.Margin = new Thickness(4, 16, 4, 0);
            var label = title switch
            {
                "IBOX account (Telegram)" => "Account",
                "AI agents (MCP) & command line" => "AI & CLI",
                "Smart selection & failover" => "Failover",
                "Jacki infrastructure verification" => "Verification",
                "Speed & handshake" => "Connection",
                "Multiplexing (MUX)" => "MUX",
                "Data cap and alerts" => "Limits & alerts",
                _ => title,
            };
            tabs.Items.Add(new TabItem { Header = label, ToolTip = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        }
        page.Content = tabs;
    }

    private static string? FindHeading(DependencyObject node)
    {
        if (node is TextBlock t && t.Style == System.Windows.Application.Current.TryFindResource("Text.Section") && !string.IsNullOrWhiteSpace(t.Text))
        {
            return t.Text;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            var found = FindHeading(child);
            if (found is not null) { return found; }
        }
        return null;
    }
}
