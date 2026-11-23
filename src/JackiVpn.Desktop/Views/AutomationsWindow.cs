using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Application.Automations;
using JackiVpn.Desktop.Composition;

namespace JackiVpn.Desktop.Views;

/// <summary>Settings › Automations: an on/off switch per automation, their options and a log of what they did.</summary>
internal static class AutomationsWindow
{
    public static void Show(AppServices s)
    {
        var window = DialogService.CreateWindow("Automations", 560);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Automations", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(Hint("Small helpers that act for you. Each one is off until you turn it on, and everything they do is listed below."));

        foreach (var a in s.Automations.Automations)
        {
            var id = a.Id;
            var box = new CheckBox
            {
                Content = AutomationTexts.Title(id),
                IsChecked = s.Settings.Current.Automations.IsEnabled(id),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 2),
            };
            box.Click += (_, _) => s.Settings.Update(x => x with { Automations = x.Automations.With(id, box.IsChecked == true) });
            root.Children.Add(box);
            root.Children.Add(Hint(AutomationTexts.Hint(id)));
            if (id == TrustedNetworksAutomation.AutomationId)
            {
                var list = new TextBox { Text = string.Join(", ", s.Settings.Current.Automations.TrustedNetworks), Margin = new Thickness(0, 4, 0, 0) };
                list.LostFocus += (_, _) => s.Settings.Update(x => x with
                {
                    Automations = x.Automations with { TrustedNetworks = list.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList() },
                });
                var add = new Button { Content = "Add this Wi-Fi", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                add.Click += (_, _) =>
                {
                    if (NetworkIdentity.Current() is { Kind: NetworkKind.Wifi, Name: { } ssid })
                    {
                        list.Text = string.Join(", ", list.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(ssid).Distinct(StringComparer.Ordinal));
                        list.Focus();
                        add.Focus();
                    }
                    else
                    {
                        add.Content = "Not on Wi-Fi";
                    }
                };
                root.Children.Add(Hint("Trusted Wi-Fi names, separated by commas:"));
                root.Children.Add(list);
                root.Children.Add(add);
            }
            else if (id == NightlyRefreshAutomation.AutomationId)
            {
                var remove = new CheckBox { Content = "Also remove servers that failed for 3 days", IsChecked = s.Settings.Current.Automations.RemoveDeadAfterDays > 0, Margin = new Thickness(0, 4, 0, 0) };
                remove.Click += (_, _) => s.Settings.Update(x => x with { Automations = x.Automations with { RemoveDeadAfterDays = remove.IsChecked == true ? 3 : 0 } });
                root.Children.Add(remove);
            }
        }

        root.Children.Add(new TextBlock { Text = "What they did", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4) });
        var log = DialogService.Styled(new TextBox { IsReadOnly = true, Height = 150, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "TextBox.Mono");
        void Refresh() => log.Text = s.Automations.Log.Entries.Count == 0 ? "Nothing yet." : string.Join(Environment.NewLine, s.Automations.Log.Entries.Reverse().Select(AutomationTexts.Format));
        Refresh();
        void OnAdded(object? sender, AutomationLogEntry e) => log.Dispatcher.BeginInvoke(Refresh);
        s.Automations.Log.Added += OnAdded;
        window.Closed += (_, _) => s.Automations.Log.Added -= OnAdded;
        root.Children.Add(log);

        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(close);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
    }

    private static TextBlock Hint(string text) => DialogService.Styled(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, "Text.Hint");
}
