using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Desktop.Composition;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Security;

namespace JackiVpn.Desktop.Views;

internal sealed class EasySetupView : StackPanel
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly TextBlock _step = new() { Margin = new Thickness(0,0,0,16) };
    private readonly StackPanel _body = new();
    private bool _busy;

    private EasySetupView(AppServices services, MainViewModel main)
    {
        _services = services; _main = main;
        Margin = new Thickness(8);
        _step.SetResourceReference(TextBlock.ForegroundProperty, "B.TextSecondary");
        Children.Add(_step); Children.Add(_body);
        if (services.Ibox.SignedIn) { _ = ImportAccountAsync(); }
        else { Account(); }
    }

    public static void Show(AppServices services, MainViewModel main)
    {
        var view = new EasySetupView(services, main);
        MainWindow.Current?.OpenDrawer("Easy setup", new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, () =>
        {
            services.Settings.Update(s => s with { Ui = s.Ui with { TourSeen = true } });
            services.Ibox.MarkSignInOffered();
        }, width: 680);
    }

    private void Page(int step, string title, string detail)
    {
        _step.Text = $"{step} / 3   ·   Account → Servers → Ready";
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _body.Children.Add(Hint(detail));
    }
    private void Account()
    {
        Page(1, "Get connected in a few steps", "Sign in with Telegram to bring in the subscriptions on your IBOX account, or continue with your own links. You can sign in later.");
        var login = Button("Sign in with Telegram", () =>
        {
            Page(1, "Connect your Telegram account", "Approve the sign-in in Telegram. IBOX will continue here automatically.");
            var form = AccountWindow.SignIn(_services.Ibox.Account.Login, "Your subscriptions will be added after sign-in.", result =>
            {
                _services.Ibox.SignIn(result);
                _ = ImportAccountAsync();
            }, null);
            _body.Children.Add(form);
            _body.Children.Add(Button("Continue without Telegram", () => Sources(), primary: false));
        });
        ButtonIcons.Apply(login, "telegram", "Sign in with Telegram");
        _body.Children.Add(login);
        _body.Children.Add(Button("Continue without Telegram", () => Sources(), primary: false));
        _body.Children.Add(Button("Set up later", () => MainWindow.Current?.CloseDrawer(), primary: false));
    }

    private async Task ImportAccountAsync()
    {
        if (_busy) { return; }
        _busy = true;
        Page(2, "Adding your subscriptions", "Reading the subscriptions from your IBOX account…");
        try
        {
            await _services.AccountImport.ImportAsync(null, _services.Lifetime);
            var count = await ReloadAsync();
            if (count > 0) { Ready(count, "Your account's subscriptions are available in IBOX."); }
            else { Sources("No servers were returned by this account yet. You can add a link or sync another device."); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Sources("Could not read your subscriptions. " + SecretRedactor.Redact(ex.Message));
        }
        finally { _busy = false; }
    }

    private void Sources(string? note = null)
    {
        Page(2, "Add your servers", note ?? "Paste a subscription link or a config link (VLESS, VMess, Trojan and other supported formats). Or bring your servers from another device.");
        var link = new TextBox { AcceptsReturn = true, Height = 120, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0,12,0,0), Tag = "Subscription URL or config links" };
        System.Windows.Automation.AutomationProperties.SetName(link, "Subscription or config links");
        _body.Children.Add(link);
        var status = Hint("");
        var add = Button("Add and continue", () => { });
        add.Click += async (_, _) =>
        {
            if (_busy) { return; }
            var text = link.Text.Trim();
            if (text.Length == 0) { status.Text = "Paste a subscription or config link first."; link.Focus(); return; }
            _busy = true; add.IsEnabled = link.IsEnabled = false; status.Text = "Adding servers…";
            try
            {
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                {
                    var sub = await _services.SubscriptionService.AddAsync(text, null, null, _services.Lifetime);
                    if (sub.ProfileCount == 0) { status.Text = sub.LastError ?? "This subscription returned no servers. Check the link and try again."; return; }
                }
                else
                {
                    var imported = await _services.ProfileService.ImportTextAsync(text, _services.Lifetime);
                    if (imported.Imported.Count == 0) { status.Text = "No new configs were found. Check the link, or choose existing servers below."; return; }
                }
                var count = await ReloadAsync();
                Ready(count, "Your servers were added successfully.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = SecretRedactor.Redact(ex.Message); }
            finally { _busy = false; add.IsEnabled = link.IsEnabled = true; }
        };
        _body.Children.Add(add); _body.Children.Add(status);
        _body.Children.Add(Button("Sync with another device", async () =>
        {
            if (_busy) { return; }
            EcosystemView.ShowDialog(_main.Ecosystem);
            var count = await ReloadAsync();
            if (count > 0) { Ready(count, "Your servers are ready after device sync."); }
            else { status.Text = "No servers received yet. Pair with your other device to continue."; }
        }, primary: false));
        _body.Children.Add(Button("Use existing servers", async () =>
        {
            var count = await ReloadAsync();
            if (count > 0) { Ready(count, "Your saved servers are ready."); }
            else { status.Text = "There are no saved servers yet."; }
        }, primary: false));
        if (!_services.Ibox.SignedIn) { _body.Children.Add(Button("Back to Telegram sign-in", Account, primary: false)); }
        _body.Children.Add(Button("Set up later", () => MainWindow.Current?.CloseDrawer(), primary: false));
    }

    private async Task<int> ReloadAsync()
    {
        await _main.Servers.LoadAsync();
        await _main.Subscriptions.LoadAsync();
        return (await _services.Profiles.GetAllAsync(_services.Lifetime)).Count;
    }
    private void Ready(int count, string detail)
    {
        Page(3, $"{count} servers ready", detail + " Choose a server yourself, or enable Smart Connect so IBOX picks one when you connect.");
        _main.ShowToast("Setup", $"{count} servers available. Choose a server or enable Smart Connect.");
        _body.Children.Add(Button("Use Smart Connect", () =>
        {
            _main.Dashboard.SmartMode = true;
            MainWindow.Current?.CloseDrawer();
            _main.Navigate(_main.Dashboard);
            _main.ShowToast("Smart Connect ready", "IBOX will pick a working server when you connect.");
        }));
        _body.Children.Add(Button("Choose a server", () => { MainWindow.Current?.CloseDrawer(); _main.Navigate(_main.Servers); }, primary: false));
        _body.Children.Add(Button("Add another link", () => Sources(), primary: false));
    }
    private static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,12), LineHeight = 21 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "B.TextSecondary");
        return hint;
    }
    private static Button Button(string label, Action action, bool primary = true)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0,10,0,0), Padding = new Thickness(16,12,16,12) };
        button.SetResourceReference(StyleProperty, primary ? "Button.Primary" : "Button.Outline");
        button.Click += (_, _) => action();
        return button;
    }
}
