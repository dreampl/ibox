using Avalonia;
using Avalonia.Controls;

namespace JackiVpn.Linux.Ui;

internal static class SectionTabs
{
    public static Control Create(Control body)
    {
        if (body is not StackPanel stack || stack.Children.Count(c => c.Tag is string) < 2) { return new ScrollViewer { Content = body }; }
        var tabs = new TabControl { Margin = new Thickness(20, 0, 24, 20) };
        StackPanel? current = null;
        foreach (var child in stack.Children.ToArray())
        {
            stack.Children.Remove(child);
            if (current is null || child.Tag is string)
            {
                current = new StackPanel { Spacing = 16, Margin = new Thickness(0, 16, 0, 16) };
                tabs.Items.Add(new TabItem { Header = child.Tag as string ?? "Overview", Content = new ScrollViewer { Content = current } });
            }
            current.Children.Add(child);
        }
        return tabs;
    }
}
