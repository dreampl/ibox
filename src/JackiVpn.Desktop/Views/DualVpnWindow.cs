using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    private static TextBlock Hint(string text) => DialogService.Styled(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, "Text.Hint");

    /// <summary>
    /// The standalone second VPN: what it is (WireGuard, IPsec, Kerio, another VPN app or a saved server) and which
    /// company names and subnets take it. Saving runs <paramref name="save"/>; the window stays open on a problem.
    /// </summary>
    public static bool EditStandalone(SecondVpnSettings current, IReadOnlyList<ProxyProfile> servers, bool ipsec, Func<SecondVpnDraft, Task<string?>> save, Func<Task>? remove)
    {
        var window = DialogService.CreateWindow("Dual VPN", 520);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Dual VPN — a second VPN", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(Hint("A second VPN to your company or another server, with its own on/off. On its own it carries all your traffic; with IBOX on too, only the company's part goes through it and everything else keeps IBOX."));

        var name = new TextBox { Text = current.Name };
        root.Children.Add(Label("Name"));
        root.Children.Add(name);

        var kinds = new[] { "WireGuard — a .conf file", "IPsec — IKEv2 or L2TP", "Kerio Control — Kerio VPN Client", "Another VPN app — OpenVPN, Cisco, Forti…", "A saved IBOX server" };
        var kind = new ComboBox { ItemsSource = kinds, SelectedIndex = (int)current.Kind };
        root.Children.Add(Label("Type"));
        root.Children.Add(kind);

        // WireGuard
        var wgText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 120, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var wgOpen = new Button { Content = "Open .conf file…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        wgOpen.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*", CheckFileExists = true };
            if (dlg.ShowDialog(window) == true && new System.IO.FileInfo(dlg.FileName).Length < 64 * 1024)
            {
                wgText.Text = System.IO.File.ReadAllText(dlg.FileName);
            }
        };
        var wgPanel = new StackPanel { Children = { Label("WireGuard configuration"), wgText, wgOpen } };
        wgPanel.Children.Add(Hint(current.Kind == SecondVpnKind.WireGuard && current.ServerId is not null
            ? "A configuration is saved. Paste a new one only to replace it."
            : "Paste the file the company or server gave you. Its subnets and DNS fill in below by themselves."));

        // IPsec
        var host = new TextBox { Text = current.Host ?? string.Empty };
        var ipsecType = new ComboBox { ItemsSource = new[] { "IKEv2 (user name + password)", "L2TP/IPsec (user name + password + pre-shared key)" }, SelectedIndex = (int)current.IpsecType };
        var user = new TextBox { Text = current.Username ?? string.Empty };
        var password = new PasswordBox();
        var psk = new PasswordBox();
        var pskPanel = new StackPanel { Children = { Label("Pre-shared key (PSK)"), psk } };
        var hasSecret = current.Kind == SecondVpnKind.Ipsec && current.Secret is not null;
        var ipsecPanel = new StackPanel
        {
            Children = { Label("VPN gateway"), host, Label("Protocol"), ipsecType, Label("User name"), user, Label(hasSecret ? "Password (empty = keep the saved one)" : "Password"), password, pskPanel },
        };
        ipsecPanel.Children.Add(Hint(ipsec
            ? "Windows' own VPN client is used. IBOX sets it up as a split tunnel: only the subnets below go through it."
            : "IPsec is not available here."));

        // Kerio / another app: the adapter.
        var adapters = DualVpn.Adapters().ToList();
        if (current.Interface is { } chosen && !adapters.Contains(chosen))
        {
            adapters.Insert(0, chosen);
        }

        var kerioFound = SecondVpnService.FindKerioAdapter();
        var adapter = new ComboBox { ItemsSource = adapters, IsEditable = true, Text = current.Interface ?? adapters.FirstOrDefault() ?? string.Empty };
        var adapterHint = Hint(string.Empty);
        var adapterPanel = new StackPanel { Children = { Label("Its network adapter"), adapter, adapterHint } };

        // A saved server.
        var sorted = servers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var server = new ComboBox { ItemsSource = sorted.Select(s => s.DisplayName).ToList(), SelectedIndex = sorted.FindIndex(s => s.Id == current.ServerId) };
        var serverPanel = new StackPanel { Children = { Label("Server"), server, Hint("Company traffic goes through this server inside IBOX's own connection.") } };

        root.Children.Add(wgPanel);
        root.Children.Add(ipsecPanel);
        root.Children.Add(adapterPanel);
        root.Children.Add(serverPanel);

        void Show()
        {
            var k = (SecondVpnKind)Math.Max(0, kind.SelectedIndex);
            wgPanel.Visibility = k == SecondVpnKind.WireGuard ? Visibility.Visible : Visibility.Collapsed;
            ipsecPanel.Visibility = k == SecondVpnKind.Ipsec ? Visibility.Visible : Visibility.Collapsed;
            adapterPanel.Visibility = k is SecondVpnKind.Kerio or SecondVpnKind.App ? Visibility.Visible : Visibility.Collapsed;
            serverPanel.Visibility = k == SecondVpnKind.Server ? Visibility.Visible : Visibility.Collapsed;
            pskPanel.Visibility = ipsecType.SelectedIndex == (int)IpsecType.L2tp ? Visibility.Visible : Visibility.Collapsed;
            if (k == SecondVpnKind.Kerio)
            {
                adapterHint.Text = kerioFound is { } found
                    ? $"Found the Kerio VPN Client's adapter ({found}). Keep the Kerio client connected; IBOX sends the company traffic through it."
                    : "Install the Kerio Control VPN Client from your company and connect it once — IBOX then finds its adapter here.";
                if (kerioFound is not null && string.IsNullOrWhiteSpace(adapter.Text))
                {
                    adapter.Text = kerioFound;
                }
            }
            else
            {
                adapterHint.Text = "Connect the company's VPN app first; its adapter shows up in this list. The app keeps reaching its own server directly.";
            }
        }

        kind.SelectionChanged += (_, _) => Show();
        ipsecType.SelectionChanged += (_, _) => Show();
        Show();

        // With IBOX on too: what takes the second VPN.
        var scope = new ComboBox
        {
            ItemsSource = new[] { "Smart — IBOX decides (recommended)", "Only the names and subnets below" },
            SelectedIndex = (int)current.Scope,
        };
        root.Children.Add(Label("With IBOX on too, send through it"));
        root.Children.Add(scope);
        var scopeHint = Hint(string.Empty);
        root.Children.Add(scopeHint);

        TextBox Box(IReadOnlyList<string> values) => new() { Text = string.Join(", ", values), TextWrapping = TextWrapping.Wrap };
        var domains = Box(current.Domains);
        var cidrs = Box(current.Cidrs);
        var dns = Box(current.Dns);
        var domainsLabel = Label(string.Empty);
        var cidrsLabel = Label(string.Empty);
        root.Children.Add(domainsLabel);
        root.Children.Add(domains);
        root.Children.Add(cidrsLabel);
        root.Children.Add(cidrs);
        root.Children.Add(Label("Company DNS (optional)"));
        root.Children.Add(dns);
        root.Children.Add(Hint("Separate entries with commas. A name covers its subdomains."));
        void ShowScope()
        {
            var smart = scope.SelectedIndex != (int)SecondVpnScope.Custom;
            scopeHint.Text = smart
                ? "The company's private addresses, and what the second VPN announces (its network, DNS and domain). Your own home or office network always stays direct."
                : "Only what you list below; everything else keeps IBOX.";
            domainsLabel.Text = smart ? "Also these names (optional)" : "Company names (corp.example.com, intranet.local …)";
            cidrsLabel.Text = smart ? "Also these subnets (optional)" : "Company subnets (10.20.0.0/16, 192.168.50.0/24 …)";
        }

        scope.SelectionChanged += (_, _) => ShowScope();
        ShowScope();

        var error = Hint(string.Empty);
        error.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        root.Children.Add(error);

        var saved = false;
        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        if (remove is not null)
        {
            var del = DialogService.Styled(new Button { Content = "Remove", MinWidth = 90 }, "Button.Outline");
            del.Click += async (_, _) =>
            {
                del.IsEnabled = false;
                await remove();
                saved = true;
                window.DialogResult = true;
            };
            DockPanel.SetDock(del, Dock.Left);
            buttons.Children.Add(del);
        }

        var save2 = DialogService.Styled(new Button { Content = "Save and connect", MinWidth = 130, IsDefault = true }, "Button.Primary");
        DockPanel.SetDock(save2, Dock.Right);
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(cancel, Dock.Right);
        save2.Click += async (_, _) =>
        {
            save2.IsEnabled = false;
            error.Text = "Saving…";
            var k = (SecondVpnKind)Math.Max(0, kind.SelectedIndex);
            var problem = await save(new SecondVpnDraft
            {
                Name = name.Text,
                Kind = k,
                WireGuardConfig = wgText.Text,
                ServerId = server.SelectedIndex >= 0 ? sorted[server.SelectedIndex].Id : null,
                Interface = k is SecondVpnKind.Kerio or SecondVpnKind.App ? adapter.Text : null,
                Host = host.Text,
                IpsecType = (IpsecType)Math.Max(0, ipsecType.SelectedIndex),
                Username = user.Text,
                Password = password.Password.Length > 0 ? password.Password : null,
                PreSharedKey = psk.Password.Length > 0 ? psk.Password : null,
                Domains = domains.Text,
                Cidrs = cidrs.Text,
                Dns = dns.Text,
                Scope = (SecondVpnScope)Math.Max(0, scope.SelectedIndex),
            });
            save2.IsEnabled = true;
            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            saved = true;
            window.DialogResult = true;
        };
        buttons.Children.Add(save2);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
        return saved;
    }
}
