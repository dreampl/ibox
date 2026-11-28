using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Application.Account;

namespace JackiVpn.Desktop.Views;

public sealed class AccountManagementView : DockPanel
{
    private readonly IboxServices _ibox;
    private readonly StackPanel _devices = new();
    private readonly StackPanel _families = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8) };
    private readonly CheckBox _allow = new() { Content = "Allow this device to be controlled by my account", Margin = new Thickness(0,8,0,8) };
    public AccountManagementView(IboxServices ibox)
    {
        _ibox = ibox;
        var intro = new StackPanel();
        intro.Children.Add(new TextBlock { Text = "Devices signed in to your Telegram account can be managed over the internet. Device names, connection status and config names are shared with your account service; config secrets are not sent.", TextWrapping = TextWrapping.Wrap });
        _allow.IsChecked = ibox.Control?.AllowRemote == true;
        _allow.Click += (_, _) => { if (ibox.Control is { } control) { control.AllowRemote = _allow.IsChecked == true; } };
        intro.Children.Add(_allow);
        intro.Children.Add(Action("Refresh", Refresh)); intro.Children.Add(_status);
        SetDock(intro, Dock.Top); Children.Add(intro);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Devices", Content = new ScrollViewer { Content = _devices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Nex Family", Content = new ScrollViewer { Content = _families, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Children.Add(tabs);
        Loaded += async (_, _) => { if (ibox.Control is { } control) { control.Changed += OnChanged; } await Refresh(); };
        Unloaded += (_, _) => { if (ibox.Control is { } control) { control.Changed -= OnChanged; } };
    }
    private void OnChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RenderDevices);
    private async Task Refresh()
    {
        if (!_ibox.SignedIn || _ibox.Control is not { } control) { _status.Text = "Sign in with Telegram first."; return; }
        await Run(async () => { await control.RefreshAsync(); RenderDevices(); var result = await control.FamilyAsync(); RenderFamily(result); });
    }
    private void RenderDevices()
    {
        _devices.Children.Clear();
        if (_ibox.Control is not { } control) { return; }
        _status.Text = control.Problem ?? "";
        if (control.Devices.Count == 0) { _devices.Children.Add(Info("No account devices reported yet.")); }
        foreach (var device in control.Devices)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = device.Name + (device.Me ? " · This device" : ""), FontSize = 16, FontWeight = FontWeights.SemiBold });
            body.Children.Add(Info(device.Tooltip));
            if (!device.Me)
            {
                var configs = new ComboBox { ItemsSource = device.Configs, DisplayMemberPath = "Name", SelectedItem = device.Configs.FirstOrDefault(c => c.Id == device.ProfileId) ?? device.Configs.FirstOrDefault(), MinWidth = 160, Margin = new Thickness(0,8,0,8) };
                body.Children.Add(configs);
                var actions = new WrapPanel();
                var connect = Action("Connect config", () => Command(device, "connect", (configs.SelectedItem as DeviceConfig)?.Id));
                connect.IsEnabled = device.Online && device.RemoteAllowed && device.Configs.Count > 0;
                actions.Children.Add(connect);
                var disconnect = Action("Disconnect", () => Command(device, "disconnect", null));
                disconnect.IsEnabled = device.Online && device.RemoteAllowed;
                actions.Children.Add(disconnect);
                actions.Children.Add(Action("Sign out device", async () =>
                {
                    if (!new DialogService().Confirm("Sign out device", "Remove " + device.Name + " from your account?")) { return; }
                    await Run(async () => { var result = await _ibox.Account.SessionsAsync(device.Sid); Require(result); await control.RefreshAsync(); });
                }));
                body.Children.Add(actions);
                if (!device.RemoteAllowed) { body.Children.Add(Info("Remote control is disabled on this device.")); }
            }
            _devices.Children.Add(Card(body));
        }
    }
    private async Task Command(AccountDevice device, string action, string? profile)
    {
        await Run(async () => { var result = await _ibox.Control!.CommandAsync(device.Sid, action, profile); Require(result); _status.Text = "Command queued. The device will report its connection state shortly."; });
    }
    private void RenderFamily(ApiResult response)
    {
        _families.Children.Clear();
        if (!response.Ok) { _families.Children.Add(Info(response.Error)); return; }
        var families = (response.Body?["families"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        if (families.Length == 0) { _families.Children.Add(Info("No Nex Family is linked to this account. Ask your provider to link your family head subscription.")); }
        foreach (var family in families)
        {
            var id = family["id"]?.ToString() ?? "";
            var permissions = family["permissions"] as JsonObject ?? new JsonObject();
            var manage = permissions["manage"]?.GetValue<bool>() == true;
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = family["name"]?.ToString(), FontSize = 18, FontWeight = FontWeights.SemiBold });
            var used = Bytes(family["used"]); var total = Bytes(family["total"], true);
            body.Children.Add(Info($"{used} used / {total} · Head: {family["owner"]}"));
            if (manage) { body.Children.Add(Action("Rename family", async () => { var name = await new DialogService().PromptAsync("Family name", "Name:", family["name"]?.ToString()); if (!string.IsNullOrWhiteSpace(name)) { await Family(new { action = "rename", id, name }); } })); }
            foreach (var member in (family["members"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var key = member["key"]?.ToString() ?? "";
                var row = new StackPanel { Margin = new Thickness(0,12,0,0) };
                row.Children.Add(new TextBlock { Text = member["username"]?.ToString(), FontWeight = FontWeights.SemiBold });
                row.Children.Add(Info($"Used: {Bytes(member["used"])} · Cap: {Bytes(member["cap"], true)}"));
                if (manage)
                {
                    row.Children.Add(Action("Copy subscription link", () => Family(new { action = "member_link", id, username = key })));
                    row.Children.Add(Action("Set member cap", async () =>
                    {
                        var value = await new DialogService().PromptAsync("Member volume", "GB (0 = no member cap):");
                        if (value is not null) { var cap = AccountInput.Gigabytes(value, allowZero: true); await Family(new { action = "cap", id, username = key, cap_gb = cap }); }
                    }));
                    if (member["owner"]?.GetValue<bool>() != true) { row.Children.Add(Action("Remove member", async () => { if (new DialogService().Confirm("Remove family member", "Remove " + key + " from this family?")) { await Family(new { action = "remove", id, username = key }); } })); }
                }
                body.Children.Add(row);
            }
            if (manage)
            {
                body.Children.Add(Action("Add my existing Nex account", async () => { var username = await new DialogService().PromptAsync("Add family member", "Nex username linked to your Telegram account:"); if (!string.IsNullOrWhiteSpace(username)) { await Family(new { action = "add", id, username }); } }));
                if (permissions["create_account"]?.GetValue<bool>() == true)
                {
                    var backend = new ComboBox { ItemsSource = (permissions["backends"] as JsonArray ?? []).Select(x => x?.ToString()).ToArray(), SelectedIndex = 0, Margin = new Thickness(0,10,0,0) };
                    body.Children.Add(backend);
                    body.Children.Add(Action("Create family account", async () => { var value = await new DialogService().PromptAsync("New family account", "Volume in GB (within your provider's grant):"); if (value is not null) { var volume = AccountInput.Gigabytes(value, allowZero: false); await Family(new { action = "create_account", id, backend = backend.SelectedItem?.ToString(), cap_gb = volume }); } }));
                }
                else { body.Children.Add(Info("Creating new accounts requires permission from your provider. Your family's paid total cannot be changed here.")); }
            }
            _families.Children.Add(Card(body));
        }
    }
    private async Task Family(object request) => await Run(async () => { var response = await _ibox.Control!.FamilyAsync(request); Require(response); if (response.Body?["link"]?.ToString() is { Length: > 0 } link) { Clipboard.SetText(link); _status.Text = "Subscription link copied."; } else { RenderFamily(response); _status.Text = "Family updated."; } });
    private async Task Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception e) when (e is not OutOfMemoryException) { _status.Text = JackiVpn.Security.SecretRedactor.Redact(e.Message); }
    }
    private static void Require(ApiResult result) { if (!result.Ok) { throw new InvalidOperationException(result.Error); } }
    private static string Bytes(JsonNode? value, bool unlimited = false) => long.TryParse(value?.ToString(), out var bytes) ? bytes == 0 && unlimited ? "∞" : $"{bytes / 1073741824d:0.##} GB" : "Not reported";
    private static TextBlock Info(string text) { var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,4) }; label.SetResourceReference(TextBlock.ForegroundProperty, "B.TextSecondary"); return label; }
    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, CornerRadius = new CornerRadius(16), Padding = new Thickness(16), BorderThickness = new Thickness(1), Margin = new Thickness(0,8,0,8) };
        border.SetResourceReference(BackgroundProperty, "B.Input"); border.SetResourceReference(Border.BorderBrushProperty, "B.SubscriptionEdge"); return border;
    }
    private static Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0,6,8,0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } finally { button.IsEnabled = true; } }; return button;
    }
}
