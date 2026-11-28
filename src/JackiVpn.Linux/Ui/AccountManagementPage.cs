using Avalonia.Input.Platform;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Application.Account;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

public sealed class AccountManagementPage(Shell shell) : Page(shell)
{
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _status = Hint("");
    private bool _family;
    public override string Title => "Devices & Nex Family";
    public override string Glyph => Icons.Servers;
    public static void Open(Shell shell)
    {
        var page = new AccountManagementPage(shell);
        page.Show(); shell.ShowDrawer(page.Title, page, () => page.Reset());
    }
    protected override Control Build()
    {
        var allow = new CheckBox { Content = "Allow remote control of this device", IsChecked = S.Ibox.Control?.AllowRemote == true };
        allow.IsCheckedChanged += (_, _) => { if (S.Ibox.Control is { } control) { control.AllowRemote = allow.IsChecked == true; } };
        EventHandler changed = (_, _) => Dispatcher.UIThread.Post(() => { if (!_family) { RenderDevices(); } });
        if (S.Ibox.Control is { } service) { service.Changed += changed; Track(() => service.Changed -= changed); }
        return new StackPanel { Spacing = 12, Children =
        {
            Hint("Your account service receives device names, connection status and config names, without config credentials. Only devices signed in to this Telegram account are listed."), allow,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                Ghost("Devices", () => { _family = false; _ = RefreshAsync(); }),
                Ghost("Nex Family", () => { _family = true; _ = RefreshAsync(); }),
                Ghost("Refresh", () => _ = RefreshAsync()),
            } }, _status, _body,
        } };
    }
    public override void OnShown() => _ = RefreshAsync();
    private Task RefreshAsync() => Guard(async () =>
    {
        if (!S.Ibox.SignedIn || S.Ibox.Control is not { } control) { _status.Text = "Sign in with Telegram first."; return; }
        if (_family) { var result = await control.FamilyAsync(); Require(result); RenderFamily(result); }
        else { await control.RefreshAsync(); RenderDevices(); }
    });
    private void RenderDevices()
    {
        _body.Children.Clear();
        if (S.Ibox.Control is not { } control) { return; }
        _status.Text = control.Problem ?? "";
        if (control.Devices.Count == 0) { _body.Children.Add(Hint("No account devices reported yet.")); }
        foreach (var device in control.Devices)
        {
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(T(device.Name + (device.Me ? " · This device" : ""), 16, FontWeight.SemiBold));
            row.Children.Add(Hint(device.Tooltip));
            if (!device.Me)
            {
                var config = new ComboBox { ItemsSource = device.Configs.Select(c => c.Name).ToArray(), SelectedIndex = Math.Max(0, device.Configs.ToList().FindIndex(c => c.Id == device.ProfileId)), HorizontalAlignment = HorizontalAlignment.Stretch };
                row.Children.Add(config);
                var connect = Ghost("Connect config", () => _ = Guard(async () => { if (config.SelectedIndex >= 0 && config.SelectedIndex < device.Configs.Count) { Require(await control.CommandAsync(device.Sid, "connect", device.Configs[config.SelectedIndex].Id)); _status.Text = "Command queued; waiting for device status."; } }));
                var disconnect = Ghost("Disconnect", () => _ = Guard(async () => { Require(await control.CommandAsync(device.Sid, "disconnect")); _status.Text = "Command queued; waiting for device status."; }));
                connect.IsEnabled = disconnect.IsEnabled = device.Online && device.RemoteAllowed;
                var actions = new WrapPanel(); actions.Children.Add(connect); actions.Children.Add(disconnect);
                actions.Children.Add(Ghost("Sign out device", () => _ = Guard(async () => { if (await Shell.ConfirmAsync("Sign out device", "Remove " + device.Name + " from your account?", "Sign out")) { Require(await S.Ibox.Account.SessionsAsync(device.Sid)); await RefreshAsync(); } })));
                row.Children.Add(actions);
                if (!device.RemoteAllowed) { row.Children.Add(Hint("Enable remote control on the target device first.")); }
            }
            _body.Children.Add(Card(row));
        }
    }
    private void RenderFamily(ApiResult result)
    {
        _body.Children.Clear();
        var families = (result.Body?["families"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        if (families.Length == 0) { _body.Children.Add(Hint("No Nex Family is linked to this account.")); }
        foreach (var family in families)
        {
            var id = family["id"]?.ToString() ?? "";
            var permissions = family["permissions"] as JsonObject ?? new JsonObject();
            var manage = permissions["manage"]?.GetValue<bool>() == true;
            var row = new StackPanel { Spacing = 10 };
            row.Children.Add(T(family["name"]?.ToString() ?? "Family", 18, FontWeight.SemiBold));
            row.Children.Add(Hint($"{Volume(family["used"])} used / {Volume(family["total"], true)} · Head: {family["owner"]}"));
            if (manage) { row.Children.Add(Ghost("Rename family", () => _ = Guard(async () => { if (await Shell.PromptAsync("Family name", "Name:", family["name"]?.ToString()) is { Length: > 0 } name) { await FamilyAsync(new { action = "rename", id, name }); } }))); }
            foreach (var member in (family["members"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var key = member["key"]?.ToString() ?? "";
                var person = new StackPanel { Spacing = 6 };
                person.Children.Add(T(member["username"]?.ToString() ?? key, 14, FontWeight.SemiBold));
                person.Children.Add(Hint($"{Volume(member["used"])} used · Cap: {Volume(member["cap"], true)}"));
                if (manage)
                {
                    person.Children.Add(Ghost("Copy subscription link", () => _ = Guard(() => FamilyAsync(new { action = "member_link", id, username = key }))));
                    person.Children.Add(Ghost("Set cap", () => _ = Guard(async () => { var input = await Shell.PromptAsync("Member cap", "GB (0 = no member cap):", null); if (input is not null) { var cap = AccountInput.Gigabytes(input, allowZero: true); await FamilyAsync(new { action = "cap", id, username = key, cap_gb = cap }); } })));
                    if (member["owner"]?.GetValue<bool>() != true) { person.Children.Add(Ghost("Remove member", () => _ = Guard(async () => { if (await Shell.ConfirmAsync("Remove family member", "Remove " + key + "?", "Remove")) { await FamilyAsync(new { action = "remove", id, username = key }); } }))); }
                }
                row.Children.Add(Card(person));
            }
            if (manage)
            {
                row.Children.Add(Ghost("Add my Nex account", () => _ = Guard(async () => { if (await Shell.PromptAsync("Add member", "Nex username linked to your Telegram account:", null) is { Length: > 0 } username) { await FamilyAsync(new { action = "add", id, username }); } })));
                if (permissions["create_account"]?.GetValue<bool>() == true)
                {
                    var backend = new ComboBox { ItemsSource = (permissions["backends"] as JsonArray ?? []).Select(x => x?.ToString()).ToArray(), SelectedIndex = 0 };
                    row.Children.Add(backend);
                    row.Children.Add(Ghost("Create family account", () => _ = Guard(async () => { var input = await Shell.PromptAsync("Create account", "Volume in GB (within your provider's grant):", null); if (input is not null) { var cap = AccountInput.Gigabytes(input, allowZero: false); await FamilyAsync(new { action = "create_account", id, backend = backend.SelectedItem?.ToString(), cap_gb = cap }); } })));
                }
                else { row.Children.Add(Hint("New account creation requires your provider's permission.")); }
            }
            _body.Children.Add(Card(row));
        }
    }
    private async Task FamilyAsync(object request) { var result = await S.Ibox.Control!.FamilyAsync(request); Require(result); if (result.Body?["link"]?.ToString() is { Length: > 0 } link) { if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(link); } _status.Text = "Subscription link copied."; } else { RenderFamily(result); _status.Text = "Family updated."; } }
    private static void Require(ApiResult result) { if (!result.Ok) { throw new InvalidOperationException(result.Error); } }
    private static string Volume(JsonNode? value, bool unlimited = false) => long.TryParse(value?.ToString(), out var bytes) ? bytes == 0 && unlimited ? "∞" : $"{bytes / 1073741824d:0.##} GB" : "Not reported";
}
