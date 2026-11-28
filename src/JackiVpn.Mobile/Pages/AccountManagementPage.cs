using System.Globalization;
using System.Text.Json.Nodes;
using JackiVpn.Application.Account;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Pages;

public sealed class AccountManagementPage : ContentPage
{
    private readonly MobileServices _services;
    private readonly VerticalStackLayout _body = new() { Spacing = 12 };
    private readonly Label _status = new() { LineBreakMode = LineBreakMode.WordWrap };
    private bool _family;
    private bool _appeared;
    private AccountControl? _control;
    private readonly Switch _allow = new();
    public AccountManagementPage(MobileServices services)
    {
        _services = services;
        Title = "Devices & Nex Family";
        var allow = _allow;
        allow.IsToggled = services.Ibox.Control?.AllowRemote == true;
        allow.Toggled += (_, e) => { if (services.Ibox.Control is { } control) { control.AllowRemote = e.Value; } };
        var root = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        root.Add(Text("Device names, connection status and config names are shared with your account service. Config passwords and keys are not uploaded."));
        root.Add(Text("Allow remote control of this device")); root.Add(allow);
        var tabs = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 6 };
        tabs.Add(Action("Devices", async () => { _family = false; await Refresh(); }), 0);
        tabs.Add(Action("Nex Family", async () => { _family = true; await Refresh(); }), 1);
        tabs.Add(Action("Refresh", Refresh), 2); root.Add(tabs);
        root.Add(_status); root.Add(_body); Content = new ScrollView { Content = root };
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _appeared = true;
        _services.Ibox.ControlAttached += ControlAttached;
        AttachControl();
        await Refresh();
    }
    protected override void OnDisappearing()
    {
        _appeared = false;
        _services.Ibox.ControlAttached -= ControlAttached;
        if (_control is not null) { _control.Changed -= Changed; _control = null; }
        base.OnDisappearing();
    }
    private void ControlAttached(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() => { if (_appeared) { AttachControl(); _ = Refresh(); } });
    private void AttachControl()
    {
        if (_control is not null) { _control.Changed -= Changed; }
        _control = _services.Ibox.Control;
        if (_control is not null) { _control.Changed += Changed; _allow.IsToggled = _control.AllowRemote; }
    }
    private void Changed(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() => { if (!_family) { Devices(); } });
    private async Task Refresh()
    {
        await Run(async () =>
        {
            if (!_services.Ibox.SignedIn || _services.Ibox.Control is not { } control) { _status.Text = "Sign in with Telegram first."; return; }
            if (_family) { var result = await control.FamilyAsync(); Require(result); Families(result); }
            else { await control.RefreshAsync(); Devices(); }
        });
    }
    private void Devices()
    {
        _body.Clear();
        if (_services.Ibox.Control is not { } control) { return; }
        _status.Text = control.Problem ?? "";
        if (control.Devices.Count == 0) { _body.Add(Text("No account devices reported yet.")); }
        foreach (var device in control.Devices)
        {
            var card = new VerticalStackLayout { Spacing = 8, Padding = 14 };
            card.Add(Text(device.Name + (device.Me ? " · This device" : ""), true)); card.Add(Text(device.Tooltip));
            if (!device.Me)
            {
                var picker = new Picker { Title = "Config on device", ItemsSource = device.Configs.Select(c => c.Name).ToArray(), SelectedIndex = Math.Max(0, device.Configs.ToList().FindIndex(c => c.Id == device.ProfileId)) };
                card.Add(picker);
                var connect = Action("Connect config", async () => { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < device.Configs.Count) { Require(await control.CommandAsync(device.Sid, "connect", device.Configs[picker.SelectedIndex].Id)); _status.Text = "Command queued. Waiting for device status."; } });
                var disconnect = Action("Disconnect", async () => { Require(await control.CommandAsync(device.Sid, "disconnect")); _status.Text = "Command queued. Waiting for device status."; });
                connect.IsEnabled = disconnect.IsEnabled = device.Online && device.RemoteAllowed;
                card.Add(connect); card.Add(disconnect);
                card.Add(Action("Sign out device", async () => { if (await DisplayAlertAsync("Sign out device", "Remove " + device.Name + " from this account?", "Sign out", "Cancel")) { Require(await _services.Ibox.Account.SessionsAsync(device.Sid)); await control.RefreshAsync(); } }));
                if (!device.RemoteAllowed) { card.Add(Text("Enable remote control on that device first.")); }
            }
            _body.Add(Card(card));
        }
    }
    private void Families(ApiResult response)
    {
        _body.Clear();
        var families = (response.Body?["families"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        if (families.Length == 0) { _body.Add(Text("No Nex Family is linked to this account.")); }
        foreach (var family in families)
        {
            var id = family["id"]?.ToString() ?? "";
            var permissions = family["permissions"] as JsonObject ?? new JsonObject();
            var manage = permissions["manage"]?.GetValue<bool>() == true;
            var card = new VerticalStackLayout { Spacing = 10, Padding = 14 };
            card.Add(Text(family["name"]?.ToString() ?? "Family", true));
            card.Add(Text($"{Volume(family["used"])} used / {Volume(family["total"], true)} · Head: {family["owner"]}"));
            if (manage) { card.Add(Action("Rename family", async () => { var name = await DisplayPromptAsync("Family name", "Name:", initialValue: family["name"]?.ToString()); if (!string.IsNullOrWhiteSpace(name)) { await Family(new { action = "rename", id, name }); } })); }
            foreach (var member in (family["members"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var key = member["key"]?.ToString() ?? "";
                card.Add(Text(member["username"]?.ToString() ?? key, true));
                card.Add(Text($"{Volume(member["used"])} used · Cap: {Volume(member["cap"], true)}"));
                if (manage)
                {
                    card.Add(Action("Copy subscription link", () => Family(new { action = "member_link", id, username = key })));
                    card.Add(Action("Set cap", async () => { var value = await DisplayPromptAsync("Member cap", "GB (0 = no member cap):", keyboard: Keyboard.Numeric); if (value is not null) { var cap = AccountInput.Gigabytes(value, allowZero: true); await Family(new { action = "cap", id, username = key, cap_gb = cap }); } }));
                    if (member["owner"]?.GetValue<bool>() != true) { card.Add(Action("Remove member", async () => { if (await DisplayAlertAsync("Remove member", "Remove " + key + " from this family?", "Remove", "Cancel")) { await Family(new { action = "remove", id, username = key }); } })); }
                }
            }
            if (manage)
            {
                card.Add(Action("Add my Nex account", async () => { var username = await DisplayPromptAsync("Add member", "Nex username linked to your Telegram account:"); if (!string.IsNullOrWhiteSpace(username)) { await Family(new { action = "add", id, username }); } }));
                if (permissions["create_account"]?.GetValue<bool>() == true)
                {
                    var backend = new Picker { Title = "Country / backend", ItemsSource = (permissions["backends"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToArray(), SelectedIndex = 0 }; card.Add(backend);
                    card.Add(Action("Create family account", async () => { var value = await DisplayPromptAsync("Create account", "Volume in GB (within your provider's grant):", keyboard: Keyboard.Numeric); if (value is not null) { var cap = AccountInput.Gigabytes(value, allowZero: false); await Family(new { action = "create_account", id, backend = backend.SelectedItem?.ToString(), cap_gb = cap }); } }));
                }
                else { card.Add(Text("New account creation requires your provider's permission.")); }
            }
            _body.Add(Card(card));
        }
    }
    private async Task Family(object request) { var result = await _services.Ibox.Control!.FamilyAsync(request); Require(result); if (result.Body?["link"]?.ToString() is { Length: > 0 } link) { await Clipboard.Default.SetTextAsync(link); _status.Text = "Subscription link copied."; } else { Families(result); _status.Text = "Family updated."; } }
    private async Task Run(Func<Task> work) { try { await work(); } catch (Exception e) when (e is not OutOfMemoryException) { _status.Text = JackiVpn.Security.SecretRedactor.Redact(e.Message); } }
    private Button Action(string label, Func<Task> work)
    {
        var button = new Button { Text = label };
        button.Clicked += async (_, _) => { button.IsEnabled = false; try { await Run(work); } finally { button.IsEnabled = true; } }; return button;
    }
    private static Label Text(string value, bool heading = false) => new() { Text = value, LineBreakMode = LineBreakMode.WordWrap, FontAttributes = heading ? FontAttributes.Bold : FontAttributes.None, FontSize = heading ? 17 : 13 };
    private static Border Card(View body)
    {
        var card = new Border { Content = body, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 }, StrokeThickness = 1 };
        card.SetDynamicResource(StyleProperty, "Card"); return card;
    }
    private static void Require(ApiResult result) { if (!result.Ok) { throw new InvalidOperationException(result.Error); } }
    private static string Volume(JsonNode? value, bool unlimited = false) => long.TryParse(value?.ToString(), out var bytes) ? bytes == 0 && unlimited ? "∞" : $"{bytes / 1073741824d:0.##} GB" : "Not reported";
}
