using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JackiVpn.Application.Usage;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Usage;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// Data usage and traffic control, as on Windows: the period and its cap, what moves right now, the traffic mode,
/// every app and site with its use (and per-app limits, Eco cut, priority), alerts, workplace networks and the
/// signed company policy. Live numbers refresh every 2 s only while the page is on screen.
/// </summary>
public sealed class UsagePage(Shell shell) : OptionsPage(shell)
{
    private static readonly (TrafficMode Mode, string Label)[] Modes =
        [(TrafficMode.Normal, "Auto"), (TrafficMode.Eco, "Eco"), (TrafficMode.Gaming, "Gaming"), (TrafficMode.Streaming, "Streaming"), (TrafficMode.Work, "Work")];

    private readonly TextBlock _used = T("—", 30, FontWeight.SemiBold);
    private readonly TextBlock _of = Caption(string.Empty);
    private readonly ProgressBar _bar = new() { Maximum = 1, Height = 6 };
    private readonly TextBlock _left = Caption(string.Empty);
    private readonly TextBlock _forecast = Hint(string.Empty);
    private readonly TextBlock _today = T("—", 18, FontWeight.SemiBold);
    private readonly TextBlock _now = T("—", 18, FontWeight.SemiBold);
    private readonly TextBlock _mode = Caption(string.Empty);
    private readonly StackPanel _apps = new() { Spacing = 4 };
    private readonly StackPanel _sites = new() { Spacing = 4 };
    private readonly StackPanel _alerts = new() { Spacing = 4 };
    private readonly StackPanel _workplace = new() { Spacing = 8 };
    private ComboBox? _modeCombo;
    private DispatcherTimer? _timer;
    private int _days;
    private bool _settingMode;

    public override string Title => "Usage";

    public override string Glyph => "M4 20V11 M10 20V4 M16 20v-6 M22 20H2";

    protected override Control Build()
    {
        _modeCombo = Combo(Modes.Select(m => (m.Mode, m.Label)).ToList(), Cur.Traffic.Mode, v =>
        {
            if (!_settingMode)
            {
                _ = SetTrafficAsync(t => t with { Mode = v });
            }
        });
        var range = Combo([(0, "This period"), (1, "Today"), (7, "Last 7 days"), (30, "Last 30 days")], 0, v =>
        {
            _days = v;
            RenderLists();
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { range, Ghost("Export CSV…", () => _ = ExportAsync()) },
        };

        var budget = Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Hint("THIS PERIOD"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _used, Bottom(_of) } },
                _bar,
                _left,
                _forecast,
                new Border { Height = 1, Background = B(Ui.Border), Margin = new Thickness(0, 8) },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    Children = { new StackPanel { Children = { Hint("TODAY"), _today } }, Col(new StackPanel { Children = { Hint("RIGHT NOW"), _now } }, 1) },
                },
            },
        });

        var u = Cur.Usage;
        var t = Cur.Traffic;
        var limits = Section("Limits & alerts", "Usage counts every network adapter (the VPN and direct traffic); local-network traffic is not counted.",
            Row("Data cap per period (GB)", "0 = no cap.", Number(u.CapGb, 0, 100000, v => SetUsage(x => x with { CapGb = v }), 0.5)),
            Row("Daily cap (MB)", "0 = none.", Number(u.DailyGb * 1024, 0, 1_000_000, v => SetUsage(x => x with { DailyGb = v / 1024 }), 50)),
            Row("Period", "Persian months follow the Solar Hijri calendar.", Combo([(UsageCycle.Jalali, "Persian month"), (UsageCycle.Gregorian, "Calendar month"), (UsageCycle.Custom, "Custom (from a start date)")], u.Cycle, v => SetUsage(x => x with { Cycle = v }))),
            Row("Custom period length (days)", null, Number(u.CycleLength, 1, 365, v => SetUsage(x => x with { CycleLength = (int)v }))),
            Row("When the cap is reached", "Eco blocks updates and telemetry inside the tunnel and slows background downloads.", Combo([(CapAction.Notify, "Only warn"), (CapAction.Eco, "Switch to Eco")], u.OnCap, v => { SetUsage(x => x with { OnCap = v }); _ = S.Traffic.SettingsChangedAsync(); })),
            Row("Alerts", "Cap 80 % / 100 %, spikes, new apps, heavy uploads, updates downloading.", Toggle(u.Alerts, v => SetUsage(x => x with { Alerts = v }))),
            Row("Spike alert (MB in 5 min)", null, Number(u.SpikeMb, 50, 100000, v => SetUsage(x => x with { SpikeMb = (int)v }), 50)));
        var control = Section("Traffic control", "Speed limits and modes apply at once, without reconnecting (smart control).",
            Row("Mode", "Auto: Gaming when a game runs, Work on a workplace network, Eco when the cap is reached.", _modeCombo),
            _mode,
            Row("Smart control", "Connections pass a small in-app relay so limits work per app and per class.", Toggle(t.SmartControl, v => _ = SetTrafficAsync(x => x with { SmartControl = v }))),
            Row("Gaming mode by itself", "Switches on while a known game is in front.", Toggle(t.AutoGaming, v => _ = SetTrafficAsync(x => x with { AutoGaming = v }))),
            Row("Work mode on workplace networks", null, Toggle(t.AutoWork, v => _ = SetTrafficAsync(x => x with { AutoWork = v }))),
            Row("Block automatic updates in the tunnel", "System, browser, game launcher and cloud-sync updates — always, not only in Eco.", Toggle(t.BlockedUpdates.Count > 0, v => _ = SetTrafficAsync(x => x with { BlockedUpdates = v ? x.EcoUpdates : [] }))),
            Row("Background speed while gaming (kbit/s)", null, Number(t.BackgroundKbpsWhileGaming, 256, 1_000_000, v => _ = SetTrafficAsync(x => x with { BackgroundKbpsWhileGaming = (int)v }), 256)),
            Row("Background speed while streaming (kbit/s)", null, Number(t.BackgroundKbpsWhileStreaming, 256, 1_000_000, v => _ = SetTrafficAsync(x => x with { BackgroundKbpsWhileStreaming = (int)v }), 256)));

        var reset = Ghost("Reset counters", () => _ = ResetAsync());
        return Stack(
            Header("Usage", "How much data you use, by app and by site, and what may use it.", actions),
            budget,
            Section("Apps", "Click an app for limits, Eco cut and priority.", _apps),
            Section("Sites (through the VPN)", null, _sites),
            Section("Recent alerts", null, _alerts),
            control,
            limits,
            Section("Workplace", "On a recognised company network internal names and subnets stay outside the tunnel and Work mode applies.", _workplace),
            new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Children = { reset } });
    }

    public override void OnShown()
    {
        Render();
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsEffectivelyVisible)
            {
                _timer?.Stop();
                return;
            }

            RenderLive();
        });
        _timer.Start();
    }

    private void Render()
    {
        RenderLive();
        RenderLists();
        RenderWorkplace();
    }

    private void RenderLive()
    {
        var u = Cur.Usage;
        var b = S.UsageBook.Budget(u);
        _used.Text = UsageMonitor.Size(b.Used);
        _of.Text = b.Cap is { } cap ? "of " + UsageMonitor.Size(cap) : "no cap set";
        _bar.IsVisible = b.Cap is > 0;
        _bar.Value = Math.Clamp(b.Percent / 100, 0, 1);
        _bar.Foreground = B(b.Percent >= 100 ? Danger : b.Percent >= 80 ? Warning : Accent);
        var days = Math.Max(1, b.PeriodEnd.DayNumber - DateOnly.FromDateTime(S.UsageBook.LocalNow).DayNumber + 1);
        _left.Text = b.Cap is > 0 ? $"{UsageMonitor.Size(b.Remaining)} left · {days} day(s) · about {UsageMonitor.Size(b.DailyAllowance)} a day" : $"{b.PeriodStart:yyyy-MM-dd} → {b.PeriodEnd:yyyy-MM-dd}";
        _forecast.Text = $"At this pace: {UsageMonitor.Size(b.Forecast)} by the end of the period" + (b.RunOut is { } r ? $" · the cap runs out around {r:MM-dd}" : string.Empty);
        _today.Text = UsageMonitor.Size(b.Today) + (b.DailyCap is { } dc ? " / " + UsageMonitor.Size(dc) : string.Empty);
        _now.Text = $"↓ {Speed(S.Usage.DownPerSecond)}   ↑ {Speed(S.Usage.UpPerSecond)}";
        _mode.Text = S.Traffic.Mode == TrafficMode.Normal ? "Now: Normal" : $"Now: {S.Traffic.Mode} — {S.Traffic.Reason}";
        if (_modeCombo is not null)
        {
            _settingMode = true;
            _modeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == Cur.Traffic.Mode));
            _settingMode = false;
        }
    }

    private void RenderLists()
    {
        var u = Cur.Usage;
        var t = Cur.Traffic;
        var live = S.Usage.Live.GroupBy(l => l.App, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Fill(_apps, S.UsageBook.Apps(_days, u).Take(25).ToList(), row =>
        {
            var tags = new List<string>();
            if (t.Apps.FirstOrDefault(a => a.App.Equals(row.Key, StringComparison.OrdinalIgnoreCase)) is { } rule)
            {
                tags.Add(rule.Blocked ? "blocked" : $"limit {rule.DownKbps} kbit/s");
            }

            if (t.SaverApps.Contains(row.Key, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add("cut in Eco");
            }

            if (t.PriorityApps.Contains(row.Key, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add("priority");
            }

            if (live.TryGetValue(row.Key, out var l) && l.DownPerSecond + l.UpPerSecond > 0)
            {
                tags.Add($"now ↓ {Speed(l.DownPerSecond)}");
            }

            return string.Join(" · ", tags);
        }, AppMenu);
        Fill(_sites, S.UsageBook.Sites(_days, u).Take(20).ToList(), _ => string.Empty, null);

        _alerts.Children.Clear();
        var events = S.UsageBook.Events().Take(8).ToList();
        if (events.Count == 0)
        {
            _alerts.Children.Add(Hint("Nothing yet."));
        }

        foreach (var e in events)
        {
            var color = e.Level switch { "crit" => Danger, "warn" => Warning, _ => Accent };
            _alerts.Children.Add(Line(T(e.Title, 13, FontWeight.SemiBold, color), Hint(e.Message), Caption(e.At.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))));
        }

        S.UsageBook.MarkEventsSeen();
    }

    private void Fill(StackPanel list, IReadOnlyList<UsageRow> rows, Func<UsageRow, string> note, Func<UsageRow, ContextMenu>? menu)
    {
        list.Children.Clear();
        if (rows.Count == 0)
        {
            list.Children.Add(Hint("Nothing recorded yet."));
            return;
        }

        var max = Math.Max(1, rows.Max(r => r.Total));
        foreach (var row in rows)
        {
            var bar = new ProgressBar { Maximum = 1, Value = (double)row.Total / max, Height = 3, Foreground = B(Accent), Margin = new Thickness(0, 4, 0, 0) };
            var text = new StackPanel { Children = { T(row.Key, 13, FontWeight.SemiBold), bar } };
            if (note(row) is { Length: > 0 } n)
            {
                text.Children.Insert(1, Hint(n));
            }

            var size = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { T(UsageMonitor.Size(row.Total), 13, FontWeight.SemiBold), Hint($"↓ {UsageMonitor.Size(row.Down)} ↑ {UsageMonitor.Size(row.Up)}" + (row.TunnelDown + row.TunnelUp > 0 ? $" · VPN {UsageMonitor.Size(row.TunnelDown + row.TunnelUp)}" : string.Empty)) },
            };
            var line = Line(text, size);
            if (menu is not null)
            {
                line.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                line.PointerPressed += (_, _) =>
                {
                    line.ContextMenu = menu(row);
                    line.ContextMenu.Open(line);
                };
            }

            list.Children.Add(line);
        }
    }

    private ContextMenu AppMenu(UsageRow row)
    {
        var app = row.Key;
        var t = Cur.Traffic;
        var rule = t.Apps.FirstOrDefault(a => a.App.Equals(app, StringComparison.OrdinalIgnoreCase));
        var saver = t.SaverApps.Contains(app, StringComparer.OrdinalIgnoreCase);
        var priority = t.PriorityApps.Contains(app, StringComparer.OrdinalIgnoreCase);
        var limit = Cur.Usage.AppDailyLimitsMb.TryGetValue(app, out var mb) ? mb : 0;
        return new ContextMenu
        {
            ItemsSource = new[]
            {
                Item(rule is { Blocked: true } ? "Unblock" : "Block internet (through the VPN)", () => SetAppRule(app, r => r with { Blocked = !(rule?.Blocked ?? false) })),
                Item(rule is { DownKbps: > 0 } ? $"Speed limit: {rule.DownKbps} kbit/s…" : "Speed limit…", async () =>
                {
                    if (await AskNumber("Speed limit", $"Download limit for {app} in kbit/s (0 = none):", rule?.DownKbps ?? 0) is { } v)
                    {
                        await SetAppRule(app, r => r with { DownKbps = v, UpKbps = v });
                    }
                }),
                Item(limit > 0 ? $"Daily limit: {limit} MB…" : "Daily limit (alert)…", async () =>
                {
                    if (await AskNumber("Daily limit", $"Alert when {app} uses more than this many MB a day (0 = none):", limit) is { } v)
                    {
                        SetUsage(x =>
                        {
                            var d = new Dictionary<string, int>(x.AppDailyLimitsMb, StringComparer.OrdinalIgnoreCase);
                            if (v > 0)
                            {
                                d[app] = v;
                            }
                            else
                            {
                                d.Remove(app);
                            }

                            return x with { AppDailyLimitsMb = d };
                        });
                    }
                }),
                Item(saver ? "✓ Cut in Eco mode" : "Cut in Eco mode", () => SetTrafficAsync(x => x with { SaverApps = Toggled(x.SaverApps, app) })),
                Item(priority ? "✓ Priority (never slowed)" : "Priority (never slowed)", () => SetTrafficAsync(x => x with { PriorityApps = Toggled(x.PriorityApps, app) })),
            },
        };
    }

    private Task SetAppRule(string app, Func<AppRule, AppRule> change) => SetTrafficAsync(x =>
    {
        var current = x.Apps.FirstOrDefault(a => a.App.Equals(app, StringComparison.OrdinalIgnoreCase)) ?? new AppRule { App = app };
        var updated = change(current);
        var rest = x.Apps.Where(a => !a.App.Equals(app, StringComparison.OrdinalIgnoreCase));
        return x with { Apps = updated is { Blocked: false, DownKbps: 0, UpKbps: 0 } ? rest.ToList() : rest.Append(updated).ToList() };
    });

    private static IReadOnlyList<string> Toggled(IReadOnlyList<string> list, string app) =>
        list.Contains(app, StringComparer.OrdinalIgnoreCase) ? list.Where(a => !a.Equals(app, StringComparison.OrdinalIgnoreCase)).ToList() : [.. list, app];

    private async Task SetTrafficAsync(Func<TrafficControlSettings, TrafficControlSettings> change)
    {
        S.Settings.Update(x => x with { Traffic = change(x.Traffic) });
        await Guard(() => S.Traffic.SettingsChangedAsync());
        Dispatcher.UIThread.Post(Render);
    }

    private void SetUsage(Func<UsageSettings, UsageSettings> change)
    {
        S.Settings.Update(x => x with { Usage = change(x.Usage) });
        RenderLive();
    }

    private void RenderWorkplace()
    {
        _workplace.Children.Clear();
        var w = S.Workplace;
        var facts = w.Facts;
        var here = facts.Key.Length == 0 ? "No network identified" : string.Join(" · ", new[] { facts.Ssid, facts.DnsSuffix, facts.GatewayIp }.Where(x => !string.IsNullOrEmpty(x)));
        _workplace.Children.Add(Caption("This network: " + here + (w.Active is { } a ? $" — workplace \"{a.Name}\"" : string.Empty)));
        if (w.Suggestion is { } s)
        {
            _workplace.Children.Add(Hint($"This looks like a company network ({s.SuggestedName})."));
        }

        foreach (var p in Cur.Workplace.Profiles)
        {
            var remove = Ghost("Remove", () => _ = RemoveWorkplaceAsync(p));
            _workplace.Children.Add(Line(new StackPanel { Children = { T(p.Name, 13, FontWeight.SemiBold), Hint(p.PolicyKeySha256 is { } k ? $"Signed company policy · key {k[..12]}…" : $"{p.InternalDomains.Count} internal name(s) · {p.InternalCidrs.Count} subnet(s)") } }, remove));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (facts.Key.Length > 0 && w.Active is null)
        {
            buttons.Children.Add(Ghost("This is my workplace…", () => _ = MarkWorkplaceAsync()));
        }

        buttons.Children.Add(Ghost("Import company policy…", () => _ = ImportPolicyAsync()));
        _workplace.Children.Add(buttons);
    }

    private async Task MarkWorkplaceAsync()
    {
        var w = S.Workplace;
        var name = await Shell.PromptAsync("Workplace", "Name of the company or office for this network:", w.Suggestion?.SuggestedName ?? WorkplaceService.SuggestName(w.Facts));
        if (string.IsNullOrWhiteSpace(name) || w.Facts.Key.Length == 0)
        {
            return;
        }

        var profile = WorkplaceService.FromFacts(w.Facts, name.Trim());
        S.Settings.Update(x => x with { Workplace = x.Workplace with { Profiles = [.. x.Workplace.Profiles, profile] } });
        w.Refresh();
        await Guard(() => S.Traffic.SettingsChangedAsync());
        RenderWorkplace();
    }

    private async Task RemoveWorkplaceAsync(WorkplaceProfile profile)
    {
        if (await Shell.ConfirmAsync("Remove workplace", $"Remove \"{profile.Name}\"?", "Remove"))
        {
            S.Settings.Update(x => x with { Workplace = x.Workplace with { Profiles = x.Workplace.Profiles.Where(p => p.Id != profile.Id).ToArray() } });
            S.Workplace.Refresh();
            await Guard(() => S.Traffic.SettingsChangedAsync());
            RenderWorkplace();
        }
    }

    /// <summary>A policy signed by the company (ECDSA P-256); its key is pinned on first acceptance, later updates must match.</summary>
    private async Task ImportPolicyAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Company policy", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Company policy") { Patterns = ["*.json", "*.policy"] }] });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var preview = CompanyPolicyReader.Read(text, null, DateTimeOffset.UtcNow);
            var existing = Cur.Workplace.Profiles.FirstOrDefault(p => p.Id == preview.Profile.Id);
            var policy = CompanyPolicyReader.Read(text, existing?.PolicyKeySha256, DateTimeOffset.UtcNow);
            if (existing is null && !await Shell.ConfirmAsync("Company policy",
                    $"Accept the policy of \"{policy.Profile.Name}\"?\n\nSigner key: {policy.KeySha256[..16]}…\nCompare this with what your IT department gave you. Later updates must come from the same key.", "Accept"))
            {
                return;
            }

            S.Settings.Update(x => x with { Workplace = x.Workplace with { Profiles = [.. x.Workplace.Profiles.Where(p => p.Id != policy.Profile.Id), policy.Profile] } });
            S.Workplace.Refresh();
            await Guard(() => S.Traffic.SettingsChangedAsync());
            RenderWorkplace();
        }
        catch (Exception ex) when (ex is CompanyPolicyException or IOException or UnauthorizedAccessException)
        {
            Shell.Toast("Company policy", ex.Message, warning: true);
        }
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export usage", SuggestedFileName = $"ibox-usage-{DateTime.Now:yyyyMMdd}.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        await Guard(async () =>
        {
            var u = Cur.Usage;
            var csv = string.Join("\n\n", new[] { "days", "apps", "sites", "events" }.Select(k => S.UsageBook.ExportCsv(k, _days, u)));
            await File.WriteAllTextAsync(path, csv);
            Shell.Toast("Exported", path);
        });
    }

    private async Task ResetAsync()
    {
        if (await Shell.ConfirmAsync("Reset counters", "Clear all recorded usage (apps, sites, days and alerts)?", "Reset"))
        {
            S.UsageBook.Reset("all");
            Render();
        }
    }

    private async Task<int?> AskNumber(string title, string label, int current)
    {
        var text = await Shell.PromptAsync(title, label, current.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _ = action();
        return item;
    }

    private static Border Line(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(left);
        right.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(Col(right, 1));
        return new Border { Background = B(InputBg), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = grid };
    }

    private static Border Line(Control a, Control b, Control right) => Line(new StackPanel { Children = { a, b } }, right);

    private static TextBlock Bottom(TextBlock t)
    {
        t.VerticalAlignment = VerticalAlignment.Bottom;
        t.Margin = new Thickness(0, 0, 0, 6);
        return t;
    }

    private static T Col<T>(T control, int column)
        where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
