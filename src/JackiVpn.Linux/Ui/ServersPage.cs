using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>Every server, grouped by subscription, with search, tests, favourites and the same row actions as on Windows.</summary>
public sealed class ServersPage(Shell shell) : Page(shell)
{
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBox _search = new() { PlaceholderText = "Search servers", Width = 320 };
    private readonly TextBlock _count = Caption(string.Empty);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private Button? _testButton;
    private readonly OrbView _testOrb = new() { State = Domain.Visuals.OrbState.Searching, Tint = Accent, Width = 30, Height = 30, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? _testCts;
    private List<ProxyProfile> _all = [];
    private Dictionary<string, (string Name, bool Official)> _subs = [];
    private bool _renderQueued;

    public override string Title => "Servers";

    public override string Glyph => Icons.Servers;

    protected override Control Build()
    {
        _testButton = Ghost("Test visible", () => _ = TestVisibleAsync());
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                Ghost("Paste / Import…", () => _ = ImportClipboardAsync()),
                Ghost("From file…", () => _ = ImportFileAsync()),
                _testOrb,
                _testButton,
            },
        };
        _search.TextChanged += (_, _) => Render();
        EventHandler<Application.Health.ProbeProgress> progress = (_, _) => QueueRender();
        S.Health.Progress += progress;
        Track(() => S.Health.Progress -= progress);
        EventHandler<ConnectionStatus> status = (_, st) =>
        {
            if (st.State is ConnectionState.Connected or ConnectionState.Disconnected)
            {
                QueueRender();
            }
        };
        S.Orchestrator.StatusChanged += status;
        Track(() => S.Orchestrator.StatusChanged -= status);
        EventHandler<Application.Subscriptions.SubscriptionUpdateReport> updated = (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        S.SubscriptionService.Updated += updated;
        Track(() => S.SubscriptionService.Updated -= updated);
        return new StackPanel
        {
            Children =
            {
                Header("Servers", "Subscriptions and imported configs. Double-click a server to connect.", actions),
                new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false, Children = { Right(_count), _search } },
                _list,
            },
        };
    }

    public override void OnShown() => _ = LoadAsync();

    public async Task LoadAsync() => await Guard(async () =>
    {
        _all = (await S.ServersAsync(Shell.Lifetime.Token)).ToList();
        _subs = (await S.Subscriptions.GetAllAsync(Shell.Lifetime.Token)).ToDictionary(s => s.Id, s => (s.Name, s.IsOfficial), StringComparer.Ordinal);
        Render();
    });

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _renderQueued = false;
            Render();
        }, TimeSpan.FromMilliseconds(400));
    }

    private List<ProxyProfile> Visible()
    {
        var q = _search.Text?.Trim();
        return string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Protocol.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Render()
    {
        _list.Children.Clear();
        var visible = Visible();
        _count.Text = $"{visible.Count} servers";
        if (_all.Count == 0)
        {
            _list.Children.Add(Card(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    T("No servers yet", 15, FontWeight.SemiBold),
                    Caption("Add a subscription, or paste a config (vless://, vmess://, trojan://, ss://, hysteria2://, tuic://, a sing-box / Xray / Clash file)."),
                },
            }));
            return;
        }

        var st = S.Orchestrator.Status;
        var selected = S.Settings.Current.Connection.SelectedProfileId;
        foreach (var group in visible.GroupBy(p => p.Origin.SubscriptionId ?? string.Empty))
        {
            var (name, official) = group.Key.Length > 0 && _subs.TryGetValue(group.Key, out var sub) ? sub : ("Manual", false);
            var collapsed = _collapsed.Contains(group.Key);
            _list.Children.Add(GroupHeader(group.Key, name, official, group.Count(), collapsed));
            if (!collapsed)
            {
                foreach (var p in group)
                {
                    _list.Children.Add(ServerRow(p, official, p.Id == selected, st.State == ConnectionState.Connected && st.ProfileId == p.Id));
                }
            }
        }
    }

    private Border GroupHeader(string key, string name, bool official, int count, bool collapsed)
    {
        var chevron = Icon(Icons.Chevron, 16, Text2);
        chevron.RenderTransform = new RotateTransform(collapsed ? -90 : 0);
        var title = new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = B(Text2), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(chevron);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        if (official)
        {
            var badge = Badge("✓ Official", Accent);
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        var countText = Hint($"{count}");
        countText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(countText, 3);
        grid.Children.Add(countText);
        var header = new Border { Padding = new Thickness(6, 14, 8, 6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = grid };
        header.PointerPressed += (_, _) =>
        {
            if (!_collapsed.Remove(key))
            {
                _collapsed.Add(key);
            }

            Render();
        };
        return header;
    }

    private Border ServerRow(ProxyProfile p, bool official, bool isDefault, bool active)
    {
        var h = S.Health.Snapshot(p.Id);
        var testing = _testCts is not null && h.LastTestedUtc is null;
        var latency = h.Label switch
        {
            HealthLabel.Unknown => testing ? "Testing…" : "—",
            HealthLabel.Failed => "Failed",
            _ => h.MedianLatencyMs is { } ms ? $"{ms:0} ms" : "OK",
        };
        var latencyColor = h.Label switch
        {
            HealthLabel.Unknown => Muted,
            HealthLabel.Failed => Danger,
            _ when h.MedianLatencyMs is < 800 => Success,
            _ when h.MedianLatencyMs is < 1800 => Warning,
            _ => Color.Parse("#F97316"),
        };

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (active)
        {
            badges.Children.Add(Badge("Active", Success));
        }

        if (isDefault)
        {
            badges.Children.Add(Badge("Default", Accent));
        }

        if (ConfigTraits.IsRelay(p))
        {
            badges.Children.Add(Badge("Relay", Accent2));
        }

        if (h.Label is HealthLabel.Excellent && h.MedianLatencyMs is < 400)
        {
            badges.Children.Add(Badge("⚡ Fast", Warning));
        }

        if (p.SyncState == ProfileSyncState.RemovedByProvider)
        {
            badges.Children.Add(Badge("Removed by provider", Danger));
        }

        var name = new TextBlock { Text = p.DisplayName, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        if (p.User.IsFavorite)
        {
            nameRow.Children.Add(Icon(Icons.Star, 14, Warning));
        }

        Grid.SetColumn(name, 1);
        nameRow.Children.Add(name);
        Grid.SetColumn(badges, 2);
        nameRow.Children.Add(badges);

        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameRow, Hint($"{p.Protocol} · {p.Transport.Type}" + (p.Tls.IsReality ? " · REALITY" : p.Tls.Enabled ? " · TLS" : string.Empty)) },
        };
        var latencyText = new TextBlock { Text = latency, Foreground = B(latencyColor), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Right };
        var connect = active ? Ghost("Disconnect", () => _ = Guard(() => S.Orchestrator.DisconnectAsync())) : Primary("Connect", () => _ = ConnectAsync(p));
        var more = new Button { Content = Icon(Icons.More, 18, Text2, 3), Background = Brushes.Transparent, Padding = new Thickness(6), ContextMenu = Menu(p, isDefault) };
        more.Click += (_, _) => more.ContextMenu?.Open(more);

        // Round flag of the exit IBOX last saw for this server (initials badge until it has connected once).
        var flag = new FlagView { Width = 38, Height = 38, CountryCode = S.Insights.Get(p.Fingerprint)?.Exit?.CountryCode ?? Domain.Visuals.CountryGuess.FromName(p.DisplayName), VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(flag);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        Grid.SetColumn(latencyText, 2);
        grid.Children.Add(latencyText);
        Grid.SetColumn(connect, 3);
        grid.Children.Add(connect);
        Grid.SetColumn(more, 4);
        grid.Children.Add(more);

        IBrush Rest() => active ? B(Success, Look.P.IsLight ? 0.07 : 0.1) : CardFill;
        var row = new Border
        {
            Background = Rest(),
            BorderBrush = B(active ? Success : isDefault ? Accent : Ui.Border, active || isDefault ? 0.7 : (Look.P.IsLight ? 0.9 : 0.55)),
            BorderThickness = new Thickness(active || isDefault ? 1.5 : 1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 0, 4),
            BoxShadow = ClayShadow(),
            Child = grid,
            ContextMenu = Menu(p, isDefault),
        };
        row.PointerEntered += (_, _) => row.Background = B(CardHover);
        row.PointerExited += (_, _) => row.Background = Rest();
        row.DoubleTapped += (_, _) => _ = ConnectAsync(p);
        return row;
    }

    private ContextMenu Menu(ProxyProfile p, bool isDefault)
    {
        MenuItem Item(string header, Func<Task> action)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => _ = Guard(action);
            return m;
        }

        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Item("Connect", () => ConnectAsync(p)),
                Item(isDefault ? "Default server ✓" : "Set as default", () =>
                {
                    S.Settings.Update(x => x with { Connection = x.Connection with { SelectedProfileId = p.Id, SmartMode = false } });
                    Render();
                    return Task.CompletedTask;
                }),
                Item("Test", async () =>
                {
                    await S.Health.TestAsync([p], new ProbeRequestOptions { Force = true }, Shell.Lifetime.Token);
                    Render();
                }),
                Item(p.User.IsFavorite ? "Remove from favorites" : "Favorite", async () =>
                {
                    await S.ProfileService.SetFavoriteAsync(p.Id, !p.User.IsFavorite, Shell.Lifetime.Token);
                    await LoadAsync();
                }),
                new Separator(),
                Item("Rename…", async () =>
                {
                    if (await Shell.PromptAsync("Rename", "Name shown for this server:", p.DisplayName) is { Length: > 0 } n)
                    {
                        await S.ProfileService.RenameAsync(p.Id, n.Trim(), Shell.Lifetime.Token);
                        await LoadAsync();
                    }
                }),
                Item("Duplicate", async () =>
                {
                    await S.ProfileService.DuplicateAsync(p.Id, Shell.Lifetime.Token);
                    await LoadAsync();
                }),
                Item("Delete", async () =>
                {
                    if (await Shell.ConfirmAsync("Delete server", $"Delete \"{p.DisplayName}\"? Servers from a subscription come back on its next update.", "Delete"))
                    {
                        await S.ProfileService.DeleteAsync(p.Id, Shell.Lifetime.Token);
                        await LoadAsync();
                    }
                }),
            },
        };
    }

    private Task ConnectAsync(ProxyProfile p) => Guard(async () =>
    {
        S.Settings.Update(x => x with { Connection = x.Connection with { SelectedProfileId = p.Id, SmartMode = false } });
        if (S.Orchestrator.Status.State == ConnectionState.Connected)
        {
            await S.Orchestrator.SwitchToAsync(p.Id, Shell.Lifetime.Token);
        }
        else if (ConnectionStateRules.IsIdle(S.Orchestrator.State))
        {
            Shell.Navigate(Shell.Dashboard);
            await S.Orchestrator.ConnectAsync(new ConnectRequest { ProfileId = p.Id, Smart = false }, Shell.Lifetime.Token);
        }
    });

    private async Task TestVisibleAsync()
    {
        if (_testCts is not null)
        {
            await _testCts.CancelAsync();
            return;
        }

        var visible = Visible();
        if (visible.Count == 0)
        {
            return;
        }

        _testCts = CancellationTokenSource.CreateLinkedTokenSource(Shell.Lifetime.Token);
        _testButton!.Content = "Cancel";
        _testOrb.IsVisible = true;
        try
        {
            await Task.Run(() => S.Health.TestAsync(visible, new ProbeRequestOptions { Force = true }, _testCts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Shell.Toast("Test failed", Security.SecretRedactor.Redact(ex.Message), warning: true);
        }
        finally
        {
            _testCts.Dispose();
            _testCts = null;
            _testButton.Content = "Test visible";
            _testOrb.IsVisible = false;
            Render();
        }
    }

    private Task ImportClipboardAsync() => Guard(async () =>
    {
        var text = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = await Shell.PromptAsync("Import", "Paste config links (one per line) or a subscription link:", null);
        }

        await ImportTextAsync(text);
    });

    public async Task ImportTextAsync(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n', StringComparison.Ordinal))
        {
            var sub = await S.SubscriptionService.AddAsync(text, null, null, Shell.Lifetime.Token);
            var report = await S.SubscriptionService.RefreshAsync(sub.Id, Shell.Lifetime.Token);
            Shell.Toast("Subscription added", $"{sub.Name}: {report.Added} servers.");
        }
        else
        {
            var r = await S.ProfileService.ImportTextAsync(text, Shell.Lifetime.Token);
            Shell.Toast(r.Imported.Count > 0 ? "Imported" : "Nothing imported", r.Imported.Count > 0 ? $"{r.Imported.Count} servers added." : "No supported config found in the text.", warning: r.Imported.Count == 0);
        }

        await LoadAsync();
    }

    private Task ImportFileAsync() => Guard(async () =>
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import configs", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            var r = await S.ProfileService.ImportFileAsync(path, Shell.Lifetime.Token);
            Shell.Toast("Imported", $"{r.Imported.Count} servers added.");
            await LoadAsync();
        }
    });

    private static Control Right(Control c)
    {
        DockPanel.SetDock(c, Dock.Right);
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }
}
