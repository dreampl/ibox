using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Usage;
using JackiVpn.Application.Workplace;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Desktop.ViewModels;

public sealed record UsageAppRow
{
    public required string App { get; init; }
    public string? Path { get; init; }
    public long Down { get; init; }
    public long Up { get; init; }
    public long Tunnel { get; init; }
    public double Share { get; init; }
    public bool IsNew { get; init; }
    public bool IsVpn { get; init; }
    public bool IsCut { get; init; }
    public bool IsLimited { get; init; }
    public bool IsPriority { get; init; }
    public bool IsSaver { get; init; }
    public bool OverLimit { get; init; }
    public string DownText => Format.Bytes(Down);
    public string UpText => Format.Bytes(Up);
    public string TotalText => Format.Bytes(Down + Up);
    public string TunnelText => Tunnel > 0 ? Format.Bytes(Tunnel) + " via VPN" : "direct";
    public double ShareWidth => Math.Max(2, Share * 120);
    public string Badges => string.Join("  ", new[] { IsNew ? "NEW" : null, IsVpn ? "VPN app" : null, IsCut ? "CUT" : null, IsLimited ? "LIMITED" : null, IsPriority ? "PRIORITY" : null, IsSaver ? "ECO CUT" : null, OverLimit ? "OVER LIMIT" : null }.Where(b => b is not null));
}

public enum UsageTab
{
    Apps = 0,
    Sites,
    Alerts,
}

public sealed record UsageSiteRow(string Site, string DownText, string UpText, string Apps, double ShareWidth);

public sealed record UsageLiveRow(string App, string Down, string Up, string Site);

public sealed record UsageEventRow(string When, string Title, string Message, bool Warn, bool Unseen);

public sealed record UpdateGroupItem(UpdateGroup Group, string Name, string Hint)
{
    public bool Blocked { get; set; }
}

/// <summary>Usage page: what used the data, the cap, alerts, traffic modes and the workplace.</summary>
public sealed partial class UsageViewModel : SettingsPageViewModel
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private string _periodUsed = "—";
    [ObservableProperty] private string _periodCap = "No cap set";
    [ObservableProperty] private double _periodPercent;
    [ObservableProperty] private string _periodDetail = string.Empty;
    [ObservableProperty] private string _periodLevel = "Good";
    [ObservableProperty] private string _today = "—";
    [ObservableProperty] private string _todayDetail = string.Empty;
    [ObservableProperty] private string _nowDown = "—";
    [ObservableProperty] private string _nowUp = "—";
    [ObservableProperty] private string _peak = "—";
    [ObservableProperty] private string _modeReason = string.Empty;
    [ObservableProperty] private int _range = 1;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private UsageTab _tab;
    [ObservableProperty] private IReadOnlyList<double> _dailyDown = [];
    [ObservableProperty] private IReadOnlyList<double> _dailyUp = [];
    [ObservableProperty] private IReadOnlyList<double> _hourlyDown = [];
    [ObservableProperty] private IReadOnlyList<double> _hourlyUp = [];
    [ObservableProperty] private string _workplaceStatus = string.Empty;
    [ObservableProperty] private string? _suggestion;
    [ObservableProperty] private int _unseen;
    [ObservableProperty] private bool _isBusy;

    public UsageViewModel(AppServices services, IDialogService dialogs)
        : base(services.Settings)
    {
        _services = services;
        _dialogs = dialogs;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        services.Traffic.Changed += (_, _) => Ui.Post(() => OnPropertyChanged(nameof(Mode)));
        services.Workplace.Changed += (_, _) => Ui.Post(RefreshWorkplace);
        UpdateGroups = Enum.GetValues<UpdateGroup>().Select(g => new UpdateGroupItem(g, GroupName(g), string.Join(", ", TrafficCatalog.UpdateDomains(g).Take(3)))).ToList();
    }

    public ObservableCollection<UsageAppRow> Apps { get; } = [];
    public ObservableCollection<UsageSiteRow> Sites { get; } = [];
    public ObservableCollection<UsageLiveRow> Live { get; } = [];
    public ObservableCollection<UsageEventRow> Events { get; } = [];
    public ObservableCollection<WorkplaceProfile> Workplaces { get; } = [];
    public IReadOnlyList<UpdateGroupItem> UpdateGroups { get; }

    public IReadOnlyList<Option<int>> Ranges { get; } = [new(1, "Today"), new(7, "7 days"), new(30, "30 days"), new(0, "This period")];
    public IReadOnlyList<UsageCycle> Cycles { get; } = Choices.Of<UsageCycle>();
    public IReadOnlyList<CapAction> CapActions { get; } = Choices.Of<CapAction>();
    public IReadOnlyList<TrafficMode> Modes { get; } = Choices.Of<TrafficMode>();

    /// <summary>The page is on screen (polling only then).</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            Refresh();
            RefreshWorkplace();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    partial void OnRangeChanged(int value) => Refresh();

    partial void OnSearchChanged(string value) => Refresh();

    partial void OnTabChanged(UsageTab value)
    {
        if (value == UsageTab.Alerts)
        {
            _services.UsageBook.MarkEventsSeen();
        }

        Refresh();
    }

    private UsageSettings U => S.Usage;

    private TrafficControlSettings T => S.Traffic;

    // ------------------------------------------------------------------ refresh

    private void Refresh()
    {
        var book = _services.UsageBook;
        var b = book.Budget(U);
        PeriodUsed = Format.Bytes(b.Used);
        PeriodCap = b.Cap is { } cap ? "of " + Format.Bytes(cap) : "No cap set";
        PeriodPercent = Math.Min(100, b.Percent);
        PeriodLevel = b.Level.ToString();
        PeriodDetail = b.Cap is null
            ? $"{b.PeriodStart:MMM d} – {b.PeriodEnd:MMM d} · average {Format.Bytes(b.Average7)}/day · forecast {Format.Bytes(b.Forecast)}"
            : $"{Format.Bytes(b.Remaining)} left · {Format.Bytes(b.DailyAllowance)}/day allowed · forecast {Format.Bytes(b.Forecast)}" + (b.RunOut is { } r && r <= b.PeriodEnd ? $" · runs out {r:MMM d}" : string.Empty);
        var day = book.DayTotals(DateOnly.FromDateTime(DateTime.Now));
        Today = Format.Bytes(day[0] + day[1]);
        TodayDetail = $"↓ {Format.Bytes(day[0])}  ↑ {Format.Bytes(day[1])} · local network {Format.Bytes(day[2] + day[3])} · through VPN {Format.Bytes(day[4] + day[5])}";
        NowDown = "↓ " + Format.Speed(_services.Usage.DownPerSecond);
        NowUp = "↑ " + Format.Speed(_services.Usage.UpPerSecond);
        Peak = Format.Speed(book.Peak);
        ModeReason = _services.Traffic.Mode == T.Mode ? (T.Mode == TrafficMode.Normal ? "Normal" : "Chosen by you") : $"Automatic: {_services.Traffic.Reason}";
        OnPropertyChanged(nameof(Mode));
        var daily = book.Daily(30);
        DailyDown = daily.Select(p => (double)p.Down).ToList();
        DailyUp = daily.Select(p => (double)p.Up).ToList();
        var hourly = book.Hourly(DateOnly.FromDateTime(DateTime.Now));
        HourlyDown = hourly.Select(p => (double)p.Down).ToList();
        HourlyUp = hourly.Select(p => (double)p.Up).ToList();
        Sync(Live, _services.Usage.Live.Take(8).Select(l => new UsageLiveRow(l.App, Format.Speed(l.DownPerSecond), Format.Speed(l.UpPerSecond), l.TopSite ?? string.Empty)).ToList());
        Unseen = book.UnseenEvents();
        switch (Tab)
        {
            case UsageTab.Apps:
                RefreshApps();
                break;
            case UsageTab.Sites:
                RefreshSites();
                break;
            case UsageTab.Alerts:
                Sync(Events, book.Events().Take(200).Select(e => new UsageEventRow(e.At.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture), e.Title, e.Message, e.Level != "info", !e.Seen)).ToList());
                break;
        }
    }

    private void RefreshApps()
    {
        var rows = _services.UsageBook.Apps(Range, U);
        var total = Math.Max(1, rows.Sum(r => r.Total));
        var limits = U.AppDailyLimitsMb;
        var today = Range == 1 ? rows : _services.UsageBook.Apps(1, U);
        var list = rows.Where(r => Search.Length == 0 || r.Key.Contains(Search, StringComparison.OrdinalIgnoreCase)).Take(200).Select(r =>
        {
            var rule = T.Apps.FirstOrDefault(a => string.Equals(a.App, r.Key, StringComparison.OrdinalIgnoreCase));
            var used = today.FirstOrDefault(x => string.Equals(x.Key, r.Key, StringComparison.OrdinalIgnoreCase))?.Total ?? 0;
            return new UsageAppRow
            {
                App = r.Key == "?" ? "(unknown app)" : r.Key,
                Path = _services.UsageBook.ExeOf(r.Key),
                Down = r.Down,
                Up = r.Up,
                Tunnel = r.TunnelDown + r.TunnelUp,
                Share = (double)r.Total / total,
                IsNew = _services.UsageBook.IsNewApp(r.Key, TimeSpan.FromDays(2)),
                IsVpn = TrafficCatalog.IsVpnApp(r.Key),
                IsCut = rule?.Blocked == true,
                IsLimited = rule is { Blocked: false } && (rule.DownKbps > 0 || rule.UpKbps > 0),
                IsPriority = T.PriorityApps.Contains(r.Key, StringComparer.OrdinalIgnoreCase),
                IsSaver = T.SaverApps.Contains(r.Key, StringComparer.OrdinalIgnoreCase),
                OverLimit = limits.TryGetValue(r.Key, out var mb) && mb > 0 && used >= mb * 1024L * 1024,
            };
        }).ToList();
        Sync(Apps, list);
    }

    private void RefreshSites()
    {
        var rows = _services.UsageBook.Sites(Range, U);
        var total = Math.Max(1, rows.Sum(r => r.Total));
        Sync(Sites, rows.Where(r => Search.Length == 0 || r.Key.Contains(Search, StringComparison.OrdinalIgnoreCase)).Take(200).Select(r =>
            new UsageSiteRow(r.Key, Format.Bytes(r.Down), Format.Bytes(r.Up),
                string.Join(", ", _services.UsageBook.AppsOf(r.Key, Range, U).Take(3).Select(a => a.Key)), Math.Max(2, (double)r.Total / total * 120))).ToList());
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.Count == items.Count && target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var i in items)
        {
            target.Add(i);
        }
    }

    // ------------------------------------------------------------------ mode & control

    public TrafficMode Mode
    {
        get => _services.Traffic.Mode;
        set
        {
            Set(s => s with { Traffic = s.Traffic with { Mode = value } });
            _ = _services.Traffic.SettingsChangedAsync();
        }
    }

    public bool SmartControl
    {
        get => T.SmartControl;
        set => SetTraffic(t => t with { SmartControl = value });
    }

    public bool AutoGaming
    {
        get => T.AutoGaming;
        set => SetTraffic(t => t with { AutoGaming = value });
    }

    public bool AutoWork
    {
        get => T.AutoWork;
        set => SetTraffic(t => t with { AutoWork = value });
    }

    public int GamingKbps
    {
        get => T.BackgroundKbpsWhileGaming;
        set => SetTraffic(t => t with { BackgroundKbpsWhileGaming = Clamp(value, 0, 1_000_000) });
    }

    public int StreamingKbps
    {
        get => T.BackgroundKbpsWhileStreaming;
        set => SetTraffic(t => t with { BackgroundKbpsWhileStreaming = Clamp(value, 0, 1_000_000) });
    }

    public string BlockedDomains
    {
        get => Format.Lines(T.BlockedDomains);
        set => SetTraffic(t => t with { BlockedDomains = Format.SplitLines(value) });
    }

    public string BlockedIps
    {
        get => Format.Lines(T.BlockedIps);
        set => SetTraffic(t => t with { BlockedIps = Format.SplitLines(value).Where(Domain.Common.NetText.IsCidr).ToArray() });
    }

    [RelayCommand]
    private void ToggleUpdate(UpdateGroupItem? item)
    {
        if (item is null)
        {
            return;
        }

        SetTraffic(t => t with { BlockedUpdates = item.Blocked ? [.. t.BlockedUpdates.Append(item.Group).Distinct()] : t.BlockedUpdates.Where(g => g != item.Group).ToArray() });
    }

    public void SyncUpdateGroups()
    {
        foreach (var g in UpdateGroups)
        {
            g.Blocked = T.BlockedUpdates.Contains(g.Group);
        }
    }

    private void SetTraffic(Func<TrafficControlSettings, TrafficControlSettings> change)
    {
        Set(s => s with { Traffic = change(s.Traffic) });
        _ = _services.Traffic.SettingsChangedAsync();
    }

    [RelayCommand]
    private void Cut(UsageAppRow? row) => EditRule(row, r => r with { Blocked = !r.Blocked });

    [RelayCommand]
    private async Task LimitAsync(UsageAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        var current = T.Apps.FirstOrDefault(a => string.Equals(a.App, row.App, StringComparison.OrdinalIgnoreCase));
        var mb = U.AppDailyLimitsMb.TryGetValue(row.App, out var m) ? m : 0;
        var text = await _dialogs.PromptAsync("Limit " + row.App,
            "Download kbit/s, upload kbit/s, daily limit MB (alert) — 0 = none. Example: 2000, 500, 1024",
            $"{current?.DownKbps ?? 0}, {current?.UpKbps ?? 0}, {mb}");
        if (text is null)
        {
            return;
        }

        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var v) ? Math.Max(0, v) : 0).ToArray();
        var down = parts.ElementAtOrDefault(0);
        var up = parts.ElementAtOrDefault(1);
        var daily = parts.ElementAtOrDefault(2);
        EditRule(row, r => r with { DownKbps = down, UpKbps = up });
        Set(s => s with
        {
            Usage = s.Usage with
            {
                AppDailyLimitsMb = daily > 0
                    ? new Dictionary<string, int>(s.Usage.AppDailyLimitsMb, StringComparer.OrdinalIgnoreCase) { [row.App] = daily }
                    : s.Usage.AppDailyLimitsMb.Where(kv => !string.Equals(kv.Key, row.App, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            },
        });
    }

    [RelayCommand]
    private void Priority(UsageAppRow? row)
    {
        if (row is not null)
        {
            SetTraffic(t => t with { PriorityApps = Toggle(t.PriorityApps, row.App) });
        }
    }

    [RelayCommand]
    private void Saver(UsageAppRow? row)
    {
        if (row is not null)
        {
            SetTraffic(t => t with { SaverApps = Toggle(t.SaverApps, row.App) });
        }
    }

    private static IReadOnlyList<string> Toggle(IReadOnlyList<string> list, string app) =>
        list.Contains(app, StringComparer.OrdinalIgnoreCase) ? list.Where(a => !string.Equals(a, app, StringComparison.OrdinalIgnoreCase)).ToArray() : [.. list, app];

    private void EditRule(UsageAppRow? row, Func<AppRule, AppRule> change)
    {
        if (row is null || row.App.StartsWith('(') || TrafficCatalog.Uncuttable.Contains(row.App))
        {
            return;
        }

        SetTraffic(t =>
        {
            var existing = t.Apps.FirstOrDefault(a => string.Equals(a.App, row.App, StringComparison.OrdinalIgnoreCase)) ?? new AppRule { App = row.App };
            var updated = change(existing);
            var others = t.Apps.Where(a => !string.Equals(a.App, row.App, StringComparison.OrdinalIgnoreCase));
            return t with { Apps = updated is { Blocked: false, DownKbps: 0, UpKbps: 0 } ? others.ToArray() : [.. others, updated] };
        });
        Refresh();
    }

    // ------------------------------------------------------------------ cap & alerts

    public double CapGb
    {
        get => U.CapGb;
        set => SetUsage(u => u with { CapGb = Math.Max(0, value) });
    }

    public double DailyGb
    {
        get => U.DailyGb;
        set => SetUsage(u => u with { DailyGb = Math.Max(0, value) });
    }

    public UsageCycle Cycle
    {
        get => U.Cycle;
        set => SetUsage(u => u with { Cycle = value, CycleStart = value == UsageCycle.Custom ? u.CycleStart ?? DateOnly.FromDateTime(DateTime.Today) : u.CycleStart });
    }

    public string CycleStart
    {
        get => U.CycleStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        set => SetUsage(u => u with { CycleStart = DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var d) ? d : u.CycleStart });
    }

    public int CycleLength
    {
        get => U.CycleLength;
        set => SetUsage(u => u with { CycleLength = Clamp(value, 1, 366) });
    }

    public int FreeFrom
    {
        get => U.FreeFromHour;
        set => SetUsage(u => u with { FreeFromHour = Clamp(value, 0, 23) });
    }

    public int FreeTo
    {
        get => U.FreeToHour;
        set => SetUsage(u => u with { FreeToHour = Clamp(value, 0, 23) });
    }

    public string AlertPercents
    {
        get => string.Join(", ", U.AlertPercents);
        set => SetUsage(u => u with { AlertPercents = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var v) ? v : 0).Where(v => v is > 0 and <= 200).Distinct().ToArray() });
    }

    public int SpikeMb
    {
        get => U.SpikeMb;
        set => SetUsage(u => u with { SpikeMb = Clamp(value, 0, 100_000) });
    }

    public int SpikeMinutes
    {
        get => U.SpikeMinutes;
        set => SetUsage(u => u with { SpikeMinutes = Clamp(value, 1, 60) });
    }

    public bool Alerts
    {
        get => U.Alerts;
        set => SetUsage(u => u with { Alerts = value });
    }

    public bool AlertNewApp
    {
        get => U.AlertNewApp;
        set => SetUsage(u => u with { AlertNewApp = value });
    }

    public bool AlertHeavyUpload
    {
        get => U.AlertHeavyUpload;
        set => SetUsage(u => u with { AlertHeavyUpload = value });
    }

    public bool AlertUpdates
    {
        get => U.AlertUpdates;
        set => SetUsage(u => u with { AlertUpdates = value });
    }

    public CapAction OnCap
    {
        get => U.OnCap;
        set => SetUsage(u => u with { OnCap = value });
    }

    public bool ExcludeLan
    {
        get => U.ExcludeLan;
        set => SetUsage(u => u with { ExcludeLan = value });
    }

    private void SetUsage(Func<UsageSettings, UsageSettings> change)
    {
        Set(s => s with { Usage = change(s.Usage) });
        Refresh();
    }

    [RelayCommand]
    private void ClearEvents()
    {
        _services.UsageBook.ClearEvents();
        Refresh();
    }

    [RelayCommand]
    private void Export(string? kind)
    {
        var path = _dialogs.SaveFile("CSV|*.csv", $"jacki-usage-{kind ?? "days"}.csv");
        if (path is not null)
        {
            File.WriteAllText(path, _services.UsageBook.ExportCsv(kind ?? "days", Range, U));
        }
    }

    [RelayCommand]
    private void Reset(string? scope)
    {
        if (_dialogs.Confirm("Reset usage", scope switch { "today" => "Forget today's usage?", "apps" => "Forget per-app and per-site history?", _ => "Forget all usage history?" }, danger: true))
        {
            _services.UsageBook.Reset(scope ?? "all");
            _services.UsageBook.Save(force: true);
            Refresh();
        }
    }

    // ------------------------------------------------------------------ workplace

    private void RefreshWorkplace()
    {
        var w = _services.Workplace;
        var f = w.Facts;
        WorkplaceStatus = w.Active is { } a
            ? $"On workplace network: {a.Name}. Internal names and subnets stay outside the tunnel" + (a.WorkMode ? "; Work mode applies." : ".")
            : f.Key.Length == 0 ? "No network." : $"Network: {f.DnsSuffix ?? f.Ssid ?? f.GatewayIp ?? "unknown"} (not a workplace).";
        Suggestion = w.Suggestion is { } s ? $"This looks like a company network ({s.Facts.AdDomain ?? s.Facts.DnsSuffix}). Set it up as \"{s.SuggestedName}\"?" : null;
        Sync(Workplaces, S.Workplace.Profiles.ToList());
    }

    [RelayCommand]
    private async Task AddWorkplaceAsync()
    {
        var w = _services.Workplace;
        var name = await _dialogs.PromptAsync("Workplace", "Name of the company or office for this network:", w.Suggestion?.SuggestedName ?? WorkplaceService.SuggestName(w.Facts));
        if (string.IsNullOrWhiteSpace(name) || w.Facts.Key.Length == 0)
        {
            return;
        }

        var profile = WorkplaceService.FromFacts(w.Facts, name);
        Set(s => s with { Workplace = s.Workplace with { Profiles = [.. s.Workplace.Profiles, profile] } });
        w.Refresh();
        RefreshWorkplace();
    }

    [RelayCommand]
    private void DismissWorkplace()
    {
        var key = _services.Workplace.Facts.Key;
        Set(s => s with { Workplace = s.Workplace with { DismissedNetworks = [.. s.Workplace.DismissedNetworks, key] } });
        _services.Workplace.Refresh();
        RefreshWorkplace();
    }

    [RelayCommand]
    private async Task EditDualVpnAsync(WorkplaceProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        var servers = await _services.Profiles.GetAllAsync(System.Threading.CancellationToken.None);
        if (Views.DualVpnWindow.Edit(profile, servers) is not { } edited)
        {
            return;
        }

        Set(s => s with { Workplace = s.Workplace with { Profiles = s.Workplace.Profiles.Select(p => p.Id == edited.Id ? edited : p).ToArray() } });
        _ = _services.Traffic.SettingsChangedAsync();
        RefreshWorkplace();
    }

    [RelayCommand]
    private void RemoveWorkplace(WorkplaceProfile? profile)
    {
        if (profile is not null && _dialogs.Confirm("Remove workplace", $"Remove \"{profile.Name}\"?"))
        {
            Set(s => s with { Workplace = s.Workplace with { Profiles = s.Workplace.Profiles.Where(p => p.Id != profile.Id).ToArray() } });
            _services.Workplace.Refresh();
            RefreshWorkplace();
        }
    }

    [RelayCommand]
    private void ImportPolicy()
    {
        var path = _dialogs.OpenFile("Company policy|*.json;*.jacki-policy|All files|*.*");
        if (path is null)
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            var preview = CompanyPolicyReader.Read(text, null, DateTimeOffset.UtcNow);
            var existing = S.Workplace.Profiles.FirstOrDefault(p => p.Id == preview.Profile.Id);
            var policy = CompanyPolicyReader.Read(text, existing?.PolicyKeySha256, DateTimeOffset.UtcNow);
            if (existing is null && !_dialogs.Confirm("Company policy",
                    $"Accept the policy of \"{policy.Profile.Name}\"?\n\nSigner key: {policy.KeySha256[..16]}…\nCompare this with what your IT department gave you. Later updates must come from the same key."))
            {
                return;
            }

            Set(s => s with { Workplace = s.Workplace with { Profiles = [.. s.Workplace.Profiles.Where(p => p.Id != policy.Profile.Id), policy.Profile] } });
            _services.Workplace.Refresh();
            RefreshWorkplace();
            _ = _services.Traffic.SettingsChangedAsync();
        }
        catch (Exception ex) when (ex is CompanyPolicyException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Info("Company policy", ex.Message);
        }
    }

    public static string GroupName(UpdateGroup g) => g switch
    {
        UpdateGroup.Windows => "Windows Update & Store",
        UpdateGroup.Office => "Microsoft Office",
        UpdateGroup.Browsers => "Browsers (Chrome, Edge, Firefox…)",
        UpdateGroup.Games => "Game launchers (Steam, Epic, Battle.net…)",
        UpdateGroup.Apple => "Apple (iOS / macOS)",
        UpdateGroup.Android => "Android & Play Store",
        UpdateGroup.Linux => "Linux packages (apt, snap)",
        UpdateGroup.Apps => "Apps (Adobe, VS Code, Telegram…)",
        UpdateGroup.Drivers => "Drivers (NVIDIA, AMD, Intel)",
        UpdateGroup.CloudSync => "Cloud sync (OneDrive, Dropbox)",
        _ => g.ToString(),
    };
}
