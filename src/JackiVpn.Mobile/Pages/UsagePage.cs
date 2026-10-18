using System.Globalization;
using JackiVpn.Application.Usage;
using JackiVpn.Domain.Usage;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Platforms.Android;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Pages;

/// <summary>
/// Data usage: the period and its cap, what is moving right now, the traffic mode (Eco / Gaming / Work), the sites
/// that went through the VPN and — with "Usage access" — every app's use on all networks. Live numbers refresh only
/// while the page is on screen; nothing here runs in the background.
/// </summary>
public sealed class UsagePage : AnimatedPage
{
    private static readonly TrafficMode[] Modes = [TrafficMode.Normal, TrafficMode.Eco, TrafficMode.Gaming, TrafficMode.Work];

    private readonly MobileServices _s;
    private readonly Label _used = new() { FontSize = 34, FontFamily = "OpenSansSemibold", TextColor = Rtl.Res("Text") };
    private readonly Label _of = new() { Style = Res<Style>("Hint"), VerticalOptions = LayoutOptions.End, Margin = new Thickness(0, 0, 0, 7) };
    private readonly UsageBar _bar = new() { Margin = new Thickness(0, 10, 0, 6) };
    private readonly Label _left = new() { Style = Res<Style>("Hint") };
    private readonly Label _forecast = new() { Style = Res<Style>("Hint") };
    private readonly Label _today = new() { Style = Res<Style>("Value"), FontSize = 14 };
    private readonly Label _now = new() { Style = Res<Style>("Value"), FontSize = 14 };
    private readonly Label _mode = new() { Style = Res<Style>("Hint"), Margin = new Thickness(4, 8, 4, 0) };
    private readonly SegmentedControl _modes = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly VerticalStackLayout _sites = [];
    private readonly VerticalStackLayout _apps = [];
    private readonly VerticalStackLayout _alerts = [];
    private readonly VerticalStackLayout _settings = [];
    private readonly OrbView _appsOrb = new() { WidthRequest = 36, HeightRequest = 36, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 12) };
    private bool _visible;
    private bool _changingMode;

    public UsagePage(MobileServices services)
    {
        _s = services;
        Title = L.Get("Usage_Title");
        Shell.SetNavBarIsVisible(this, true);
        _modes.Items = new[] { L.Get("Mode_Auto"), L.Get("Mode_Eco"), L.Get("Mode_Gaming"), L.Get("Mode_Work") };
        _modes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SegmentedControl.SelectedIndex) && !_changingMode)
            {
                _ = SetModeAsync(Modes[Math.Clamp(_modes.SelectedIndex, 0, Modes.Length - 1)]);
            }
        };

        var budget = Card(new VerticalStackLayout
        {
            Children =
            {
                Overline(L.Get("Usage_Period")),
                new HorizontalStackLayout { Spacing = 8, Children = { _used, _of } },
                _bar,
                _left,
                _forecast,
                new BoxView { HeightRequest = 1, Color = Rtl.Res("Divider"), Margin = new Thickness(0, 12) },
                new Grid
                {
                    ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
                    Children = { Stack(L.Get("Usage_Today"), _today), Column(Stack(L.Get("Usage_Now"), _now), 1) },
                },
            },
        });

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 12, 20, 40),
                Spacing = 4,
                Children =
                {
                    budget,
                    Overline(L.Get("Mode_Title")),
                    _modes,
                    _mode,
                    Group(L.Get("Usage_Sites"), _sites),
                    Group(L.Get("Usage_Apps"), _apps),
                    Group(L.Get("Usage_Alerts"), _alerts),
                    Group(null, _settings),
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        _s.Usage.Updated += OnUpdated;
        _s.Traffic.Changed += OnUpdated;
        Refresh(full: true);
        _ = LoadAppsAsync();
    }

    protected override void OnDisappearing()
    {
        _visible = false;
        _s.Usage.Updated -= OnUpdated;
        _s.Traffic.Changed -= OnUpdated;
        _appsOrb.IsRunning = false;
        base.OnDisappearing();
    }

    private void OnUpdated(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() => Refresh(full: false));

    private void Refresh(bool full)
    {
        if (!_visible)
        {
            return;
        }

        var settings = _s.Settings.Current;
        var u = settings.Usage;
        var b = _s.UsageBook.Budget(u);
        _used.Text = UsageMonitor.Size(b.Used);
        _of.Text = b.Cap is { } cap ? string.Format(CultureInfo.CurrentCulture, L.Get("Usage_Of"), UsageMonitor.Size(cap)) : L.Get("Usage_NoCap");
        _bar.Fraction = b.Cap is > 0 ? Math.Clamp(b.Percent / 100, 0, 1) : 0;
        _bar.BarColor = b.Percent >= 100 ? Rtl.Res("Danger") : b.Percent >= 80 ? Rtl.Res("Warn") : Rtl.Res("Primary2");
        _bar.IsVisible = b.Cap is > 0;
        var days = Math.Max(1, b.PeriodEnd.DayNumber - DateOnly.FromDateTime(_s.UsageBook.LocalNow).DayNumber + 1);
        _left.Text = b.Cap is > 0 ? string.Format(CultureInfo.CurrentCulture, L.Get("Usage_Left"), UsageMonitor.Size(b.Remaining), days) : string.Empty;
        _left.IsVisible = b.Cap is > 0;
        _forecast.Text = string.Format(CultureInfo.CurrentCulture, L.Get("Usage_Forecast"), UsageMonitor.Size(b.Forecast));
        _today.Text = UsageMonitor.Size(b.Today) + (b.DailyCap is { } dc ? " / " + UsageMonitor.Size(dc) : string.Empty);
        _now.Text = "↓ " + UsageMonitor.Size(_s.Usage.DownPerSecond) + "/s   ↑ " + UsageMonitor.Size(_s.Usage.UpPerSecond) + "/s";

        _changingMode = true;
        _modes.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, settings.Traffic.Mode));
        _changingMode = false;
        var active = _s.Traffic.Mode;
        _mode.Text = (active == TrafficMode.Normal ? ModeName(active) : string.Format(CultureInfo.CurrentCulture, L.Get("Mode_Active"), ModeName(active), _s.Traffic.Reason))
            + (active == TrafficMode.Eco ? "\n" + L.Get("Mode_Hint_Eco") : active == TrafficMode.Gaming ? "\n" + L.Get("Mode_Hint_Gaming") : string.Empty);

        if (!full)
        {
            return;
        }

        Fill(_sites, _s.UsageBook.Sites(0, u).Take(10).Select(r => (r.Key, r.Total)).ToList());
        var events = _s.UsageBook.Events().Take(6).ToList();
        _alerts.Children.Clear();
        if (events.Count == 0)
        {
            _alerts.Children.Add(Empty());
        }

        foreach (var ev in events)
        {
            _alerts.Children.Add(new SettingRow
            {
                Icon = ev.Level == "info" ? "chart" : "info",
                Tint = ev.Level switch { "crit" => Rtl.Res("Danger"), "warn" => Rtl.Res("Warn"), _ => Rtl.Res("Primary2") },
                Title = ev.Title,
                Subtitle = ev.Message,
                Value = ev.At.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture),
            });
        }

        _s.UsageBook.MarkEventsSeen();
        BuildSettings(settings.Usage, settings.Traffic);
    }

    /// <summary>Per-app use from Android's own statistics (needs "Usage access"); read once per visit, off the UI thread.</summary>
    private async Task LoadAppsAsync()
    {
        var context = _s.Context;
        if (!AndroidAppUsage.HasAccess(context))
        {
            // Without "Usage access" (not requested: Play Protect counts it against VPN apps) Android's own screen has the per-app totals.
            _apps.Children.Clear();
            var grant = new Button { Text = L.Get("Usage_GrantButton"), Style = Res<Style>("Ghost"), HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(16, 0, 16, 14) };
            grant.Clicked += (_, _) => AndroidAppUsage.OpenDataUsageSettings(context);
            _apps.Children.Add(new Label { Text = L.Get("Usage_Grant"), Style = Res<Style>("Hint"), Margin = new Thickness(16, 14, 16, 8) });
            _apps.Children.Add(grant);
            return;
        }

        _apps.Children.Clear();
        _apps.Children.Add(_appsOrb);
        _appsOrb.IsRunning = true;
        var start = UsageBook.Period(_s.Settings.Current.Usage, DateOnly.FromDateTime(_s.UsageBook.LocalNow)).Start;
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(start.ToDateTime(TimeOnly.MinValue)));
        var apps = await Task.Run(() => AndroidAppUsage.Query(context, from, DateTimeOffset.Now));
        _appsOrb.IsRunning = false;
        if (_visible)
        {
            Fill(_apps, apps.Take(12).Select(a => (a.Label, a.Total)).ToList());
        }
    }

    private void Fill(VerticalStackLayout list, IReadOnlyList<(string Name, long Bytes)> rows)
    {
        list.Children.Clear();
        if (rows.Count == 0)
        {
            list.Children.Add(Empty());
            return;
        }

        var max = Math.Max(1, rows.Max(r => r.Bytes));
        foreach (var (name, bytes) in rows)
        {
            var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)], Padding = new Thickness(16, 10), ColumnSpacing = 10 };
            grid.Add(new Label { Text = name, Style = Res<Style>("Value"), FontSize = 14, LineBreakMode = LineBreakMode.TailTruncation }, 0, 0);
            grid.Add(new Label { Text = UsageMonitor.Size(bytes), Style = Res<Style>("Hint") }, 1, 0);
            var bar = new UsageBar { HeightRequest = 4, Margin = new Thickness(0, 6, 0, 0), Fraction = (double)bytes / max };
            grid.Add(bar, 0, 1);
            Grid.SetColumnSpan(bar, 2);
            list.Children.Add(grid);
        }
    }

    private void BuildSettings(UsageSettings u, TrafficControlSettings t)
    {
        _settings.Children.Clear();
        _settings.Children.Add(Row("chart", L.Get("Usage_Cap"), u.CapGb > 0 ? u.CapGb.ToString("0.#", CultureInfo.InvariantCulture) + " GB" : L.Get("Usage_NoCap"), async () =>
        {
            if (await Number(L.Get("Usage_Cap"), L.Get("Usage_CapPrompt"), u.CapGb) is { } gb)
            {
                Save(s => s with { Usage = s.Usage with { CapGb = gb } });
            }
        }));
        _settings.Children.Add(Row("clock", L.Get("Usage_DailyCap"), u.DailyGb > 0 ? (u.DailyGb * 1024).ToString("0", CultureInfo.InvariantCulture) + " MB" : L.Get("Usage_NoCap"), async () =>
        {
            if (await Number(L.Get("Usage_DailyCap"), L.Get("Usage_DailyPrompt"), u.DailyGb * 1024) is { } mb)
            {
                Save(s => s with { Usage = s.Usage with { DailyGb = mb / 1024 } });
            }
        }));
        _settings.Children.Add(Row("sync", L.Get("Usage_Cycle"), u.Cycle == UsageCycle.Gregorian ? L.Get("Usage_CycleGregorian") : L.Get("Usage_CycleJalali"), async () =>
        {
            var cycle = await Sheets.PickAsync(L.Get("Usage_Cycle"), [new SheetOption<UsageCycle>(L.Get("Usage_CycleJalali"), UsageCycle.Jalali), new SheetOption<UsageCycle>(L.Get("Usage_CycleGregorian"), UsageCycle.Gregorian)], u.Cycle);
            if (cycle != u.Cycle)
            {
                Save(s => s with { Usage = s.Usage with { Cycle = cycle } });
            }
        }));
        _settings.Children.Add(Row("eco", L.Get("Usage_OnCap"), u.OnCap == CapAction.Eco ? L.Get("Usage_OnCapEco") : L.Get("Usage_OnCapWarn"), async () =>
        {
            var action = await Sheets.PickAsync(L.Get("Usage_OnCap"), [new SheetOption<CapAction>(L.Get("Usage_OnCapWarn"), CapAction.Notify), new SheetOption<CapAction>(L.Get("Usage_OnCapEco"), CapAction.Eco)], u.OnCap);
            if (action != u.OnCap)
            {
                Save(s => s with { Usage = s.Usage with { OnCap = action } }, traffic: true);
            }
        }));
        var blocking = t.BlockedUpdates.Count > 0;
        var toggle = new ToggleSwitch { IsToggled = blocking, VerticalOptions = LayoutOptions.Center };
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToggleSwitch.IsToggled) && toggle.IsToggled != blocking)
            {
                blocking = toggle.IsToggled;
                var on = blocking;
                Save(s => s with { Traffic = s.Traffic with { BlockedUpdates = on ? s.Traffic.EcoUpdates : [] } }, traffic: true);
            }
        };
        _settings.Children.Add(new SettingRow { Icon = "update", Tint = Rtl.Res("Teal"), Title = L.Get("Usage_Blocked"), Trailing = toggle });
        _settings.Children.Add(Row("trash", L.Get("Usage_Reset"), null, async () =>
        {
            if (await DisplayAlertAsync(L.Get("Usage_Reset"), L.Get("Usage_ResetConfirm"), L.Get("Ok"), L.Get("Cancel")))
            {
                _s.UsageBook.Reset("all");
                Refresh(full: true);
            }
        }, danger: true));
    }

    private async Task SetModeAsync(TrafficMode mode)
    {
        if (_s.Settings.Current.Traffic.Mode == mode)
        {
            return;
        }

        _s.Settings.Update(s => s with { Traffic = s.Traffic with { Mode = mode } });
        await _s.Traffic.SettingsChangedAsync();
        Refresh(full: false);
    }

    private void Save(Func<Domain.Settings.AppSettings, Domain.Settings.AppSettings> change, bool traffic = false)
    {
        _s.Settings.Update(change);
        if (traffic)
        {
            _ = _s.Traffic.SettingsChangedAsync();
        }

        Refresh(full: true);
    }

    private async Task<double?> Number(string title, string prompt, double current)
    {
        var text = await DisplayPromptAsync(title, prompt, L.Get("Ok"), L.Get("Cancel"), initialValue: current.ToString("0.##", CultureInfo.InvariantCulture), keyboard: Keyboard.Numeric);
        return double.TryParse(text?.Replace('٫', '.').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1_000_000 ? v : null;
    }

    private static string ModeName(TrafficMode mode) => mode switch
    {
        TrafficMode.Eco => L.Get("Mode_Eco"),
        TrafficMode.Gaming => L.Get("Mode_Gaming"),
        TrafficMode.Work => L.Get("Mode_Work"),
        TrafficMode.Streaming => "Streaming",
        _ => L.Get("Mode_Normal"),
    };

    private static SettingRow Row(string icon, string title, string? value, Func<Task> action, bool danger = false) => new()
    {
        Icon = icon,
        Tint = danger ? Rtl.Res("Danger") : Rtl.Res("Primary2"),
        Title = title,
        Value = value,
        Command = new Command(() => _ = action()),
    };

    private static Border Card(View content) => new()
    {
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
        Stroke = Rtl.Res("Stroke"),
        StrokeThickness = 1,
        BackgroundColor = Rtl.Res("Surface"),
        Padding = new Thickness(18, 16),
        Content = content,
    };

    private static View Group(string? header, VerticalStackLayout rows)
    {
        var g = new SettingGroup { Header = header };
        g.Rows.Add(rows);
        return g;
    }

    private static Label Overline(string text) => new() { Text = text.ToUpperInvariant(), Style = Res<Style>("Overline"), Margin = new Thickness(4, 16, 4, 6) };

    private static VerticalStackLayout Stack(string caption, Label value) => new() { Spacing = 2, Children = { new Label { Text = caption, Style = Res<Style>("Hint") }, value } };

    private static View Column(View view, int column)
    {
        Grid.SetColumn(view, column);
        return view;
    }

    private static Label Empty() => new() { Text = L.Get("Usage_None"), Style = Res<Style>("Hint"), Margin = new Thickness(16, 14) };

    private static T Res<T>(string key) => (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];
}
