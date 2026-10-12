using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Domain.Subscriptions;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>Add, update and manage subscriptions; usage, expiry, device limit and errors as on Windows.</summary>
public sealed class SubscriptionsPage(Shell shell) : Page(shell)
{
    private readonly StackPanel _list = new() { Spacing = 12 };
    private readonly TextBox _url = new() { PlaceholderText = "https://panel.example.com/sub/…" };
    private readonly TextBox _name = new() { PlaceholderText = "Name (optional)", Width = 220 };
    private readonly HashSet<string> _updating = new(StringComparer.Ordinal);
    private Button? _add;

    public override string Title => "Subscriptions";

    public override string Glyph => Icons.Subscriptions;

    protected override Control Build()
    {
        _add = Primary("Add", () => _ = AddAsync());
        var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        addRow.Children.Add(_url);
        Grid.SetColumn(_name, 1);
        addRow.Children.Add(_name);
        Grid.SetColumn(_add, 2);
        addRow.Children.Add(_add);
        var addCard = Card(new StackPanel
        {
            Spacing = 10,
            Children = { T("Add subscription", 15, FontWeight.SemiBold), addRow, Hint("Paste the subscription link from your panel (https). Name is optional.") },
        });
        EventHandler<Application.Subscriptions.SubscriptionUpdateReport> updated = (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        S.SubscriptionService.Updated += updated;
        Track(() => S.SubscriptionService.Updated -= updated);
        EventHandler<string> checkedHandler = (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        S.SubscriptionService.AccountChecked += checkedHandler;
        Track(() => S.SubscriptionService.AccountChecked -= checkedHandler);
        return new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Header("Subscriptions", "PasarGuard panels are detected automatically. Subscription links are stored encrypted and never shown or logged in full.", Ghost("Update all", () => _ = UpdateAllAsync())),
                addCard,
                _list,
            },
        };
    }

    public override void OnShown() => _ = LoadAsync();

    private Task LoadAsync() => Guard(async () =>
    {
        var subs = await S.Subscriptions.GetAllAsync(Shell.Lifetime.Token);
        _list.Children.Clear();
        if (subs.Count == 0)
        {
            _list.Children.Add(Card(Caption("No subscriptions yet.")));
            return;
        }

        foreach (var s in subs)
        {
            _list.Children.Add(SubscriptionCard(s));
        }
    });

    private Border SubscriptionCard(Subscription s)
    {
        var m = s.Metadata;
        var now = DateTimeOffset.UtcNow;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T(s.Name, 16, FontWeight.SemiBold) } };
        if (s.IsOfficial)
        {
            title.Children.Add(Badge("✓ Official", Accent));
        }

        if (_updating.Contains(s.Id))
        {
            title.Children.Add(new OrbView { State = Domain.Visuals.OrbState.Weaving, Tint = Accent, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(Hint("updating…"));
        }

        var update = Ghost("Update", () => _ = UpdateAsync(s.Id));
        var menu = new Button { Content = Icon(Icons.More, 18, Text2, 3), Background = Brushes.Transparent, Padding = new Thickness(6) };
        menu.ContextMenu = new ContextMenu { ItemsSource = new[] { MenuItem("Rename…", () => RenameAsync(s)), MenuItem("Delete", () => DeleteAsync(s)) } };
        menu.Click += (_, _) => menu.ContextMenu.Open(menu);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        head.Children.Add(new StackPanel { Spacing = 2, Children = { title, Hint(s.MaskedUrl) } });
        Grid.SetColumn(update, 1);
        head.Children.Add(update);
        Grid.SetColumn(menu, 2);
        head.Children.Add(menu);

        var panel = new StackPanel { Spacing = 12, Children = { head } };

        // Traffic: bar + used of total.
        if (m.UsedBytes is not null || m.TotalBytes is not null)
        {
            var percent = m.UsedPercent ?? 0;
            var bar = new ProgressBar { Maximum = 100, Value = Math.Clamp(percent, 0, 100), Height = 6, Foreground = B(percent >= 90 ? Danger : percent >= 75 ? Warning : Accent) };
            var total = m.IsUnlimitedTraffic == true ? "∞" : Bytes(m.TotalBytes);
            panel.Children.Add(new StackPanel { Spacing = 6, Children = { bar, Caption($"{Bytes(m.UsedBytes)} used of {total}" + (m.RemainingBytes is { } r ? $" · {Bytes(r)} remaining" : string.Empty)) } });
        }

        var facts = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fact(string label, string value)
        {
            facts.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 28, 8), Children = { Hint(label), T(value, 13, FontWeight.SemiBold) } });
        }

        if (m.UploadBytes is not null || m.DownloadBytes is not null)
        {
            Fact("Upload / Download", $"{Bytes(m.UploadBytes)} / {Bytes(m.DownloadBytes)}");
        }

        if (m.IsUnlimitedExpiry == true)
        {
            Fact("Expires", "Never");
        }
        else if (m.ExpireUtc is { } exp)
        {
            var left = m.TimeRemaining(now);
            Fact("Expires", exp.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + (left is { } l ? $" ({Math.Max(0, (int)l.TotalDays)} days left)" : string.Empty));
        }

        Fact("Servers", s.ProfileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Fact("Last update", When(s.LastUpdateUtc));
        if (m.Username is { Length: > 0 } user)
        {
            Fact("User", user);
        }

        if (m.DeviceLimit is { } limit)
        {
            Fact("Devices", limit > 0 ? $"up to {limit}" : "unlimited");
        }

        if (m.OnlineAtUtc is { } online)
        {
            Fact("Last online", When(online));
        }

        Fact("Source", s.Host);
        panel.Children.Add(facts);

        if (m.Announcement is { Length: > 0 } ann)
        {
            panel.Children.Add(new Border { Background = B(Accent, 0.1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = T(ann, 12.5) });
        }

        if (s.LastError is { Length: > 0 } err)
        {
            panel.Children.Add(new Border { Background = B(Warning, 0.12), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = T("Last update failed (servers kept): " + err, 12.5, color: Warning) });
        }

        var auto = Combo(
            [
                (AutoUpdateInterval.ProviderDefault, "Provider default"), (AutoUpdateInterval.Off, "Off"), (AutoUpdateInterval.Minutes30, "every 30 min"),
                (AutoUpdateInterval.Hour1, "every hour"), (AutoUpdateInterval.Hours6, "every 6 hours"), (AutoUpdateInterval.Hours12, "every 12 hours"), (AutoUpdateInterval.Hours24, "every day"),
            ],
            s.AutoUpdate,
            v => _ = Guard(() => S.SubscriptionService.SetAutoUpdateAsync(s.Id, v, Shell.Lifetime.Token)));
        var sendId = Toggle(s.SendDeviceId ?? s.IsOfficial, v => _ = Guard(() => S.SubscriptionService.SetSendDeviceIdAsync(s.Id, v, Shell.Lifetime.Token)));
        var options = new WrapPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 24, 0), Children = { Center(Caption("Auto-update")), auto } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Center(Caption("Send device ID")), sendId } },
            },
        };
        panel.Children.Add(options);

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (m.SupportUrl is { Length: > 0 } support)
        {
            links.Children.Add(Ghost("Support", () => Open(support)));
        }

        if (m.WebPageUrl is { Length: > 0 } web)
        {
            links.Children.Add(Ghost("Web page", () => Open(web)));
        }

        if (links.Children.Count > 0)
        {
            panel.Children.Add(links);
        }

        return Card(panel);
    }

    private MenuItem MenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _ = Guard(action);
        return item;
    }

    private async Task AddAsync()
    {
        var url = _url.Text?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        _add!.IsEnabled = false;
        await Guard(async () =>
        {
            var name = string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();
            var sub = await S.SubscriptionService.AddAsync(url, name, null, Shell.Lifetime.Token);
            _url.Text = _name.Text = string.Empty;
            await LoadAsync();
            await UpdateAsync(sub.Id);
        });
        _add.IsEnabled = true;
    }

    private async Task UpdateAsync(string id)
    {
        _updating.Add(id);
        await LoadAsync();
        await Guard(async () =>
        {
            var r = await S.SubscriptionService.RefreshAsync(id, Shell.Lifetime.Token);
            if (r.Error is { } e)
            {
                Shell.Toast("Update failed", e, warning: true);
            }
            else
            {
                Shell.Toast("Subscription updated", $"{r.Added} new, {r.Changed} changed, {r.Removed} removed.");
            }
        });
        _updating.Remove(id);
        await LoadAsync();
        await Shell.Servers.LoadAsync();
    }

    private async Task UpdateAllAsync()
    {
        foreach (var s in await S.Subscriptions.GetAllAsync(Shell.Lifetime.Token))
        {
            await UpdateAsync(s.Id);
        }
    }

    private async Task RenameAsync(Subscription s)
    {
        if (await Shell.PromptAsync("Rename subscription", "Name:", s.Name) is { Length: > 0 } n)
        {
            await S.SubscriptionService.RenameAsync(s.Id, n.Trim(), Shell.Lifetime.Token);
            await LoadAsync();
        }
    }

    private async Task DeleteAsync(Subscription s)
    {
        if (await Shell.ConfirmAsync("Delete subscription", $"Delete \"{s.Name}\" and its {s.ProfileCount} servers?", "Delete"))
        {
            await S.SubscriptionService.DeleteAsync(s.Id, Shell.Lifetime.Token);
            await LoadAsync();
            await Shell.Servers.LoadAsync();
        }
    }

    private static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" or "tg")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", u.AbsoluteUri) { UseShellExecute = false })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static TextBlock Center(TextBlock t)
    {
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }
}
