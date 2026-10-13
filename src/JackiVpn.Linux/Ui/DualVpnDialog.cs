using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Usage;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// The standalone second VPN (Dual VPN), as on Windows: WireGuard (a .conf), IPsec (IKEv2 / L2TP through
/// NetworkManager), Kerio (the Kerio VPN Client's adapter), another VPN app or a saved server; only the company's names
/// and subnets take it. The dialog stays open with the problem when saving fails.
/// </summary>
public static class DualVpnDialog
{
    private static readonly string[] Kinds =
        ["WireGuard — a .conf file", "IPsec — IKEv2 or L2TP", "Kerio Control — Kerio VPN Client", "Another VPN app — OpenVPN, Cisco, Forti…", "A saved IBOX server"];

    public static async Task<bool> EditAsync(Shell shell, SecondVpnService service, IReadOnlyList<ProxyProfile> servers)
    {
        var current = service.Current;
        var dialog = new Window
        {
            Title = "Dual VPN",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 820,
            CanResize = false,
            Background = B(CardBg),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var name = new TextBox { Text = current.Name };
        var kind = new ComboBox { ItemsSource = Kinds, SelectedIndex = (int)current.Kind, HorizontalAlignment = HorizontalAlignment.Stretch };

        var wgText = new TextBox { AcceptsReturn = true, Height = 120, FontFamily = new FontFamily("monospace"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        var wgOpen = Ghost("Open .conf file…", () => _ = OpenConfAsync(dialog, wgText));
        var wgPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Label("WireGuard configuration"), wgText, wgOpen,
                Hint(current.Kind == SecondVpnKind.WireGuard && current.ServerId is not null
                    ? "A configuration is saved. Paste a new one only to replace it."
                    : "Paste the file the company or server gave you. Its subnets and DNS fill in below by themselves."),
            },
        };

        var host = new TextBox { Text = current.Host ?? string.Empty };
        var ipsecType = new ComboBox { ItemsSource = new[] { "IKEv2 (user name + password)", "L2TP/IPsec (user name + password + pre-shared key)" }, SelectedIndex = (int)current.IpsecType, HorizontalAlignment = HorizontalAlignment.Stretch };
        var user = new TextBox { Text = current.Username ?? string.Empty };
        var password = new TextBox { PasswordChar = '•' };
        var psk = new TextBox { PasswordChar = '•' };
        var pskPanel = new StackPanel { Spacing = 6, Children = { Label("Pre-shared key (PSK)"), psk } };
        var saved = current.Kind == SecondVpnKind.Ipsec && current.Secret is not null;
        var ipsecPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Label("VPN gateway"), host, Label("Protocol"), ipsecType, Label("User name"), user,
                Label(saved ? "Password (empty = keep the saved one)" : "Password"), password, pskPanel,
                Hint(service.SupportsIpsec
                    ? "NetworkManager's own VPN client is used (never the default route: only the subnets below go through it)."
                    : "IPsec is not available here."),
            },
        };

        var adapters = DualVpn.Adapters().ToList();
        if (current.Interface is { } chosen && !adapters.Contains(chosen))
        {
            adapters.Insert(0, chosen);
        }

        var kerio = SecondVpnService.FindKerioAdapter();
        var adapter = new AutoCompleteBox { ItemsSource = adapters, Text = current.Interface ?? adapters.FirstOrDefault() ?? string.Empty, FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0 };
        var adapterHint = Hint(string.Empty);
        adapterHint.TextWrapping = TextWrapping.Wrap;
        var adapterPanel = new StackPanel { Spacing = 6, Children = { Label("Its network adapter"), adapter, adapterHint } };

        var sorted = servers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var server = new ComboBox { ItemsSource = sorted.Select(s => s.DisplayName).ToList(), SelectedIndex = sorted.FindIndex(s => s.Id == current.ServerId), HorizontalAlignment = HorizontalAlignment.Stretch };
        var serverPanel = new StackPanel { Spacing = 6, Children = { Label("Server"), server, Hint("Company traffic goes through this server inside IBOX's own connection.") } };

        void Show()
        {
            var k = (SecondVpnKind)Math.Max(0, kind.SelectedIndex);
            wgPanel.IsVisible = k == SecondVpnKind.WireGuard;
            ipsecPanel.IsVisible = k == SecondVpnKind.Ipsec;
            adapterPanel.IsVisible = k is SecondVpnKind.Kerio or SecondVpnKind.App;
            serverPanel.IsVisible = k == SecondVpnKind.Server;
            pskPanel.IsVisible = ipsecType.SelectedIndex == (int)IpsecType.L2tp;
            if (k == SecondVpnKind.Kerio)
            {
                adapterHint.Text = kerio is { } found
                    ? $"Found the Kerio VPN Client's adapter ({found}). Keep the Kerio client connected; IBOX sends the company traffic through it."
                    : "Install the Kerio Control VPN Client (kerio-control-vpnclient) and connect it once — IBOX then finds its adapter (kvnet).";
                if (kerio is not null && string.IsNullOrWhiteSpace(adapter.Text))
                {
                    adapter.Text = kerio;
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

        TextBox Box(IReadOnlyList<string> values) => new() { Text = string.Join(", ", values), TextWrapping = TextWrapping.Wrap };
        var domains = Box(current.Domains);
        var cidrs = Box(current.Cidrs);
        var dns = Box(current.Dns);
        var error = T(string.Empty, 12, color: Danger);
        error.TextWrapping = TextWrapping.Wrap;

        var ok = false;
        Button? saveButton = null;
        saveButton = Primary("Save and connect", () => _ = SaveAsync());
        async Task SaveAsync()
        {
            saveButton!.IsEnabled = false;
            error.Text = "Saving…";
            var k = (SecondVpnKind)Math.Max(0, kind.SelectedIndex);
            string? problem;
            try
            {
                problem = await service.SaveAsync(new SecondVpnDraft
                {
                    Name = name.Text ?? string.Empty,
                    Kind = k,
                    WireGuardConfig = wgText.Text,
                    ServerId = server.SelectedIndex >= 0 ? sorted[server.SelectedIndex].Id : null,
                    Interface = k is SecondVpnKind.Kerio or SecondVpnKind.App ? adapter.Text : null,
                    Host = host.Text,
                    IpsecType = (IpsecType)Math.Max(0, ipsecType.SelectedIndex),
                    Username = user.Text,
                    Password = string.IsNullOrEmpty(password.Text) ? null : password.Text,
                    PreSharedKey = string.IsNullOrEmpty(psk.Text) ? null : psk.Text,
                    Domains = domains.Text,
                    Cidrs = cidrs.Text,
                    Dns = dns.Text,
                }, shell.Lifetime.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problem = Security.SecretRedactor.Redact(ex.Message);
            }

            saveButton.IsEnabled = true;
            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            ok = true;
            dialog.Close();
        }

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        if (current.IsConfigured)
        {
            var remove = Ghost("Remove", () => _ = RemoveAsync());
            async Task RemoveAsync()
            {
                await service.RemoveAsync(shell.Lifetime.Token);
                ok = true;
                dialog.Close();
            }

            DockPanel.SetDock(remove, Dock.Left);
            buttons.Children.Add(remove);
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Ghost("Cancel", () => dialog.Close()), saveButton } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        dialog.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24, 20),
                Spacing = 8,
                Children =
                {
                    T("Dual VPN — a second VPN", 17, FontWeight.SemiBold),
                    Wrap(Hint("Connect to your company (or another server) next to IBOX. Only the company's names and subnets you enter below go through the second VPN; everything else keeps the IBOX connection.")),
                    Label("Name"), name, Label("Type"), kind,
                    wgPanel, ipsecPanel, adapterPanel, serverPanel,
                    Label("Company names (corp.example.com, intranet.local …)"), domains,
                    Label("Company subnets (10.20.0.0/16, 192.168.50.0/24 …)"), cidrs,
                    Label("Company DNS (optional)"), dns,
                    Hint("Separate entries with commas. A name covers its subdomains."),
                    error,
                    buttons,
                },
            },
        };
        await dialog.ShowDialog(shell);
        return ok;
    }

    private static TextBlock Label(string text)
    {
        var t = Caption(text);
        t.Margin = new Thickness(0, 6, 0, 0);
        return t;
    }

    private static TextBlock Wrap(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static async Task OpenConfAsync(Window owner, TextBox target)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "WireGuard configuration",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WireGuard config") { Patterns = ["*.conf"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        if (stream.Length < 64 * 1024)
        {
            using var reader = new StreamReader(stream);
            target.Text = await reader.ReadToEndAsync();
        }
    }
}
