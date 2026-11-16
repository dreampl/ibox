using System;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing.Uris;

namespace JackiVpn.Desktop.Views;

/// <summary>Adds a DNS tunnel (dnstt) server: the form writes a <c>dnstt://</c> link that the normal import reads.</summary>
internal static class DnsTunnelWindow
{
    private static readonly string[] Resolvers =
    [
        DnsttResolver.AutoToken,
        DnsttResolver.SystemToken,
        "8.8.8.8",
        "1.1.1.1",
        "178.22.122.100",
        "https://dns.google/dns-query",
        "185.51.200.2",
    ];

    /// <summary>The link to import, or null when cancelled.</summary>
    public static string? Show()
    {
        var window = DialogService.CreateWindow("DNS tunnel", 500);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Add a DNS tunnel server", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(Hint("Your traffic goes out as DNS questions through a resolver, so it works where almost everything else is blocked. It is slow (tens of KB/s on most networks): for messages and emergencies."));

        var name = new TextBox();
        var domain = new TextBox();
        var key = DialogService.Styled(new TextBox(), "TextBox.Mono");
        var resolver = new ComboBox { ItemsSource = Resolvers, IsEditable = true, Text = Resolvers[0] };
        var user = new TextBox();
        var password = new PasswordBox();

        root.Children.Add(Label("Name"));
        root.Children.Add(name);
        root.Children.Add(Label("Tunnel domain (e.g. t.example.com)"));
        root.Children.Add(domain);
        root.Children.Add(Label("Server public key (64 hex characters)"));
        root.Children.Add(key);
        root.Children.Add(Label("DNS resolver"));
        root.Children.Add(resolver);
        root.Children.Add(Hint("auto tries your carrier's own DNS, 8.8.8.8, 1.1.1.1, Shecan and DNS over HTTPS together and keeps the first in that order that works. You can also enter one address, https://… (DNS over HTTPS), tls://host (DNS over TLS) or several separated by commas."));
        root.Children.Add(Label("User name (optional)"));
        root.Children.Add(user);
        root.Children.Add(Label("Password (optional)"));
        root.Children.Add(password);

        var error = DialogService.Styled(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }, "Text.Hint");
        error.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        root.Children.Add(error);

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) });
        var add = DialogService.Styled(new Button { Content = "Add", MinWidth = 90, IsDefault = true }, "Button.Primary");
        add.Click += (_, _) =>
        {
            var host = domain.Text.Trim().TrimEnd('.');
            var hex = key.Text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!NetText.IsValidHostName(host) || NetText.IsIpLiteral(host))
            {
                error.Text = "Enter the tunnel's domain name (e.g. t.example.com).";
                return;
            }

            if (!DnsttResolver.IsPublicKey(hex))
            {
                error.Text = "The public key must be 64 hex characters.";
                return;
            }

            if (DnsttResolver.ParseList(resolver.Text) is null)
            {
                error.Text = "The resolver must be auto, system, an IP address (1.2.3.4 or 1.2.3.4:53), https://… or tls://host; several are separated by commas.";
                return;
            }

            if (user.Text.Trim().Length == 0 && password.Password.Length > 0)
            {
                error.Text = "A password needs a user name.";
                return;
            }

            result = ShareLinkParser.FormatDnstt(host, hex, resolver.Text, user.Text.Trim(), password.Password, string.IsNullOrWhiteSpace(name.Text) ? host : name.Text);
            window.DialogResult = true;
        };
        buttons.Children.Add(add);
        root.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        domain.Focus();
        return window.ShowDialog() == true ? result : null;
    }

    private static TextBlock Label(string text) => DialogService.Styled(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6) }, "Text.Label");

    private static TextBlock Hint(string text) => DialogService.Styled(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, "Text.Hint");
}
