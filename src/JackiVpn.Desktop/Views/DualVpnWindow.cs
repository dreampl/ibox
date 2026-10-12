using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Dual VPN for one workplace: away from the office, in Work mode, its names and subnets take the work VPN (a saved
/// server, or the company VPN app's adapter) while everything else keeps the normal connection.
/// </summary>
internal static class DualVpnWindow
{
    /// <summary>The edited workplace, or null when cancelled.</summary>
    public static WorkplaceProfile? Edit(WorkplaceProfile profile, IReadOnlyList<ProxyProfile> servers)
    {
        var window = DialogService.CreateWindow("Dual VPN", 480);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Dual VPN · " + profile.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(DialogService.Styled(new TextBlock { Text = "Away from the office, in Work mode, the company's names and subnets take the work VPN; everything else keeps this connection.", TextWrapping = TextWrapping.Wrap }, "Text.Hint"));

        var route = new ComboBox { ItemsSource = new[] { "Off — company traffic goes direct", "A saved server (the company's config)", "The company's VPN app (its network adapter)" }, SelectedIndex = (int)profile.DualVpn.Route };
        root.Children.Add(Label("Company traffic goes through"));
        root.Children.Add(route);

        var sorted = servers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var server = new ComboBox { ItemsSource = sorted.Select(s => s.DisplayName).ToList(), SelectedIndex = sorted.FindIndex(s => s.Id == profile.DualVpn.ServerId) };
        var serverPanel = new StackPanel();
        serverPanel.Children.Add(Label("Server"));
        serverPanel.Children.Add(server);
        root.Children.Add(serverPanel);

        var adapters = DualVpn.Adapters().ToList();
        if (profile.DualVpn.Interface is { } chosen && !adapters.Contains(chosen))
        {
            adapters.Insert(0, chosen);
        }

        var adapter = new ComboBox { ItemsSource = adapters, IsEditable = true, Text = profile.DualVpn.Interface ?? adapters.FirstOrDefault() ?? string.Empty };
        var adapterPanel = new StackPanel();
        adapterPanel.Children.Add(Label("Adapter"));
        adapterPanel.Children.Add(adapter);
        adapterPanel.Children.Add(DialogService.Styled(new TextBlock { Text = "Connect the company's VPN app first; its adapter shows up here. That app keeps reaching its own server directly.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, "Text.Hint"));
        root.Children.Add(adapterPanel);

        void Show()
        {
            serverPanel.Visibility = route.SelectedIndex == (int)WorkRoute.Server ? Visibility.Visible : Visibility.Collapsed;
            adapterPanel.Visibility = route.SelectedIndex == (int)WorkRoute.Adapter ? Visibility.Visible : Visibility.Collapsed;
        }

        route.SelectionChanged += (_, _) => Show();
        Show();

        var signed = profile.PolicyKeySha256 is not null;
        TextBox Box(IReadOnlyList<string> values) => new() { Text = string.Join(", ", values), IsEnabled = !signed, TextWrapping = TextWrapping.Wrap };
        var domains = Box(profile.InternalDomains);
        var cidrs = Box(profile.InternalCidrs);
        var dns = Box(profile.InternalDns);
        root.Children.Add(Label("Internal names (corp.example.com, …)"));
        root.Children.Add(domains);
        root.Children.Add(Label("Internal subnets (10.20.0.0/16, …)"));
        root.Children.Add(cidrs);
        root.Children.Add(Label("Company DNS (optional)"));
        root.Children.Add(dns);
        root.Children.Add(DialogService.Styled(new TextBlock { Text = signed ? "Names and subnets come from the signed company policy." : "Separate entries with commas.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, "Text.Hint"));

        var error = DialogService.Styled(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }, "Text.Hint");
        error.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        root.Children.Add(error);

        WorkplaceProfile? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) });
        var save = DialogService.Styled(new Button { Content = "Save", MinWidth = 90, IsDefault = true }, "Button.Primary");
        save.Click += (_, _) =>
        {
            var dual = new DualVpnSettings
            {
                Route = (WorkRoute)Math.Max(0, route.SelectedIndex),
                ServerId = server.SelectedIndex >= 0 ? sorted[server.SelectedIndex].Id : null,
                Interface = adapter.Text,
            };
            var (edited, problem) = DualVpn.Apply(profile, dual, domains.Text, cidrs.Text, dns.Text);
            if (edited is null)
            {
                error.Text = problem;
                return;
            }

            result = edited;
            window.DialogResult = true;
        };
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return window.ShowDialog() == true ? result : null;
    }

    private static TextBlock Label(string text) => DialogService.Styled(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6) }, "Text.Label");
}
