using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Profiles;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Mobile.Services;
using JackiVpn.Security;

namespace JackiVpn.Mobile.ViewModels;

public sealed partial class ServerItem : ObservableObject
{
    [ObservableProperty] private ProxyProfile _profile;
    [ObservableProperty] private NodeHealthSnapshot _health;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _countryCode;

    public ServerItem(ProxyProfile profile, NodeHealthSnapshot health, string group, bool isOfficial = false)
    {
        _profile = profile;
        _health = health;
        Group = group;
        IsOfficial = isOfficial;
    }

    /// <summary>From an official provider subscription (verified over HTTPS).</summary>
    public bool IsOfficial { get; }

    /// <summary>Measured fast (Excellent/Good) — shown only when a test backs it.</summary>
    public bool IsFast => Health.Label is HealthLabel.Excellent or HealthLabel.Good;

    public string Id => Profile.Id;

    public string Name => Profile.DisplayName;

    public string Group { get; }

    public string Details
    {
        get
        {
            var parts = new List<string> { Fmt.Protocol(Profile.Protocol) };
            if (Profile.Transport.Type != TransportType.Tcp)
            {
                parts.Add(Fmt.Transport(Profile.Transport.Type));
            }

            if (Profile.Tls.IsReality)
            {
                parts.Add("Reality");
            }
            else if (Profile.Tls.Enabled)
            {
                parts.Add("TLS");
            }

            parts.Add(Group);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Regional-indicator emoji for the exit country, or a globe until the server has been used once.</summary>
    public string Flag => Fmt.Flag(CountryCode);

    public string Latency => Health.Label switch
    {
        HealthLabel.Unknown => "—",
        HealthLabel.Failed => "Failed",
        _ => Fmt.Connect(Health.MedianLatencyMs),
    };

    public Color LatencyColor => Health.Label switch
    {
        HealthLabel.Unknown => Color.FromArgb("#6B7280"),
        HealthLabel.Failed => Color.FromArgb("#EF4444"),
        _ when Health.MedianLatencyMs is < 800 => Color.FromArgb("#22C55E"),
        _ when Health.MedianLatencyMs is < 1800 => Color.FromArgb("#F59E0B"),
        _ => Color.FromArgb("#F97316"),
    };

    public double Score => Health.Score;

    public HealthLabel Label => Health.Label;

    public bool IsFavorite => Profile.User.IsFavorite;

    /// <summary>Default or currently connected: shown as the highlighted row.</summary>
    public bool IsSelected => IsDefault || IsActive;

    /// <summary>1–4 bars from the measured connect time (0 = not tested / failed).</summary>
    public int SignalLevel => Health.Label switch
    {
        HealthLabel.Unknown or HealthLabel.Failed => 0,
        _ when Health.MedianLatencyMs is < 400 => 4,
        _ when Health.MedianLatencyMs is < 800 => 3,
        _ when Health.MedianLatencyMs is < 1600 => 2,
        _ => 1,
    };

    public string Subtitle => Details.Replace(" · " + Group, string.Empty, StringComparison.Ordinal) + (Health.Label is HealthLabel.Unknown ? string.Empty : " · " + Latency);

    partial void OnIsDefaultChanged(bool value) => OnPropertyChanged(nameof(IsSelected));

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(IsSelected));

    partial void OnProfileChanged(ProxyProfile value) => OnPropertyChanged(string.Empty);

    partial void OnCountryCodeChanged(string? value) => OnPropertyChanged(nameof(Flag));

    partial void OnHealthChanged(NodeHealthSnapshot value)
    {
        OnPropertyChanged(nameof(Latency));
        OnPropertyChanged(nameof(LatencyColor));
        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsFast));
        OnPropertyChanged(nameof(SignalLevel));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>Servers of one subscription (or "Manual"); collapsible.</summary>
public sealed class ServerGroup(string name, bool official, IReadOnlyList<ServerItem> items) : ObservableCollection<ServerItem>(items)
{
    private readonly IReadOnlyList<ServerItem> _all = items;
    private bool _isExpanded = true;

    public string Name { get; } = name;

    public bool IsOfficial { get; } = official;

    public int Total => _all.Count;

    public string CountText => Localization.L.N(_all.Count.ToString(CultureInfo.InvariantCulture));

    public bool IsExpanded => _isExpanded;

    public void Toggle()
    {
        _isExpanded = !_isExpanded;
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
        Clear();
        if (_isExpanded)
        {
            foreach (var i in _all)
            {
                Add(i);
            }
        }
    }
}
public sealed partial class ServersViewModel : ObservableObject
{
    private readonly MobileServices _s;
    private readonly Dictionary<string, ServerItem> _byId = new(StringComparer.Ordinal);
    private List<ServerItem> _all = [];
    private CancellationTokenSource? _testCts;

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private string _sortMode = "Best";
    [ObservableProperty] private int _segment;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string? _message;

    public ServersViewModel(MobileServices services)
    {
        _s = services;
        services.Health.Progress += (_, p) => Fmt.OnUi(() =>
        {
            if (_byId.TryGetValue(p.ProfileId, out var item))
            {
                item.Health = p.Snapshot;
                item.IsTesting = false;
            }

            Message = $"Tested {p.Completed} of {p.Total}";
        });
        services.Orchestrator.StatusChanged += (_, st) => Fmt.OnUi(() =>
        {
            foreach (var i in _all)
            {
                i.IsActive = st.State == ConnectionState.Connected && i.Id == st.ProfileId;
            }
        });
        services.SubscriptionService.Updated += (_, _) => Fmt.OnUi(() => _ = LoadAsync());
        services.Settings.Changed += (_, _) => Fmt.OnUi(() => OnPropertyChanged(nameof(AutoSelection)));
        services.Insights.Changed += (_, fingerprint) => Fmt.OnUi(() =>
        {
            foreach (var i in _all.Where(i => i.Profile.Fingerprint == fingerprint))
            {
                i.CountryCode = _s.Insights.Get(fingerprint)?.Exit?.CountryCode;
            }
        });
    }

    public ObservableCollection<ServerItem> Items { get; } = [];

    /// <summary>Servers grouped by subscription (the list shows which subscription each config belongs to).</summary>
    public ObservableCollection<ServerGroup> Groups { get; } = [];

    public IReadOnlyList<string> Segments => [Localization.L.Get("Srv_All"), Localization.L.Get("Srv_Official"), Localization.L.Get("Srv_Favorites")];

    /// <summary>Auto selection = smart mode (fastest working server).</summary>
    public bool AutoSelection
    {
        get => _s.Settings.Current.Connection.SmartMode;
        set => _s.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = value } });
    }

    partial void OnSegmentChanged(int value) => ApplyView();

    [RelayCommand]
    private static void ToggleGroup(ServerGroup? group) => group?.Toggle();

    [RelayCommand]
    private async Task FavoriteAsync(ServerItem? item)
    {
        if (item is not null)
        {
            item.Profile = await _s.ProfileService.SetFavoriteAsync(item.Id, !item.IsFavorite, _s.Lifetime);
            if (Segment == 2)
            {
                ApplyView();
            }
        }
    }

    public IReadOnlyList<string> SortModes { get; } = ["Best", "Name", "Favorites"];

    public bool IsEmpty => Items.Count == 0 && !IsRefreshing;

    partial void OnFilterChanged(string value) => ApplyView();

    partial void OnSortModeChanged(string value) => ApplyView();

    public async Task LoadAsync()
    {
        await _s.InitializeAsync();
        var profiles = await _s.Profiles.GetAllAsync(_s.Lifetime);
        var subs = (await _s.Subscriptions.GetAllAsync(_s.Lifetime)).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var selectedId = _s.Settings.Current.Connection.SelectedProfileId;
        var active = _s.Orchestrator.Status is { State: ConnectionState.Connected } st ? st.ProfileId : null;
        _byId.Clear();
        _all = profiles.Select(p =>
        {
            var sub = p.Origin.SubscriptionId is { } sid && subs.TryGetValue(sid, out var s) ? s : null;
            var item = new ServerItem(p, _s.Health.Snapshot(p.Id), sub?.Name ?? "Manual", sub?.IsOfficial == true)
            {
                IsDefault = p.Id == selectedId,
                IsActive = p.Id == active,
                CountryCode = _s.Insights.Get(p.Fingerprint)?.Exit?.CountryCode,
            };
            _byId[p.Id] = item;
            return item;
        }).ToList();
        ApplyView();
    }

    private void ApplyView()
    {
        IEnumerable<ServerItem> q = _all;
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            q = q.Where(i => i.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || i.Details.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        }

        q = Segment switch
        {
            1 => q.Where(i => i.IsOfficial),
            2 => q.Where(i => i.IsFavorite),
            _ => q,
        };

        q = SortMode switch
        {
            "Name" => q.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "Favorites" => q.OrderByDescending(i => i.IsFavorite).ThenByDescending(i => i.Score),
            _ => q.OrderBy(i => i.Label is HealthLabel.Unknown or HealthLabel.Failed ? 1 : 0).ThenByDescending(i => i.IsOfficial).ThenByDescending(i => i.Score),
        };

        var list = q.ToList();
        Items.Clear();
        foreach (var i in list)
        {
            Items.Add(i);
        }

        // Keep each group's open/closed state across refreshes.
        var collapsed = Groups.Where(g => !g.IsExpanded).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        Groups.Clear();
        foreach (var g in list.GroupBy(i => i.Group).OrderByDescending(g => g.Any(i => i.IsOfficial)).ThenBy(g => g.Key == "Manual").ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var group = new ServerGroup(g.Key == "Manual" ? Localization.L.Get("Srv_Manual") : g.Key, g.Any(i => i.IsOfficial), g.ToList());
            if (collapsed.Contains(group.Name))
            {
                group.Toggle();
            }

            Groups.Add(group);
        }

        var healthy = _all.Count(i => i.Label is not (HealthLabel.Unknown or HealthLabel.Failed));
        Summary = Localization.L.N(Localization.L.F("Srv_Summary", _all.Count, healthy));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Raised when choosing a server should bring the user back to Home.</summary>
    public event EventHandler? Navigated;

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Segments));
        ApplyView();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await LoadAsync();
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestAllAsync()
    {
        if (IsTesting)
        {
            _testCts?.Cancel();
            return;
        }

        var items = Items.ToList();
        if (items.Count == 0)
        {
            return;
        }

        IsTesting = true;
        Message = $"Testing {items.Count} servers…";
        _testCts = CancellationTokenSource.CreateLinkedTokenSource(_s.Lifetime);
        foreach (var i in items)
        {
            i.IsTesting = true;
        }

        try
        {
            await Task.Run(() => _s.Health.TestAsync(items.Select(i => i.Profile).ToList(), new ProbeRequestOptions { Force = true }, _testCts.Token));
            Message = "Test finished.";
        }
        catch (OperationCanceledException)
        {
            Message = "Test cancelled.";
        }
        finally
        {
            foreach (var i in items)
            {
                i.IsTesting = false;
            }

            IsTesting = false;
            _testCts.Dispose();
            _testCts = null;
            ApplyView();
        }
    }

    /// <summary>Use this server: manual mode, remembered as the default, connected (or switched to) now.</summary>
    [RelayCommand]
    private async Task UseAsync(ServerItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var i in _all)
        {
            i.IsDefault = ReferenceEquals(i, item);
        }

        _s.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = false, SelectedProfileId = item.Id } });
        if (_s.Orchestrator.State == ConnectionState.Connected)
        {
            Message = "Switching to " + item.Name + "…";
            await _s.Orchestrator.SwitchToAsync(item.Id, _s.Lifetime);
            return;
        }

        if (!ConnectionStateRules.IsIdle(_s.Orchestrator.State))
        {
            return;
        }

        if (Platform.CurrentActivity is not { } activity || !await Platforms.Android.VpnPermission.RequestAsync(activity))
        {
            Message = "Android did not allow IBOX to create a VPN.";
            return;
        }

        Navigated?.Invoke(this, EventArgs.Empty);
        await _s.Orchestrator.ConnectAsync(new ConnectRequest { ProfileId = item.Id, Smart = false }, _s.Lifetime);
    }

    [RelayCommand]
    private async Task MoreAsync(ServerItem? item)
    {
        if (item is null)
        {
            return;
        }

        var choice = await Controls.Sheets.ActionsAsync(item.Name,
            ("connect", Localization.L.Get("Act_Connect"), "power", false),
            ("test", Localization.L.Get("Act_Test"), "bolt", false),
            ("fav", Localization.L.Get(item.IsFavorite ? "Act_Unfavorite" : "Act_Favorite"), "star", false),
            ("rename", Localization.L.Get("Act_Rename"), "edit", false),
            ("delete", Localization.L.Get("Act_Delete"), "trash", true));
        switch (choice)
        {
            case "connect":
                await UseAsync(item);
                break;
            case "test":
                item.IsTesting = true;
                try
                {
                    await Task.Run(() => _s.Health.TestAsync([item.Profile], new ProbeRequestOptions { Force = true }, _s.Lifetime));
                }
                finally
                {
                    item.IsTesting = false;
                }

                break;
            case "fav":
                await FavoriteAsync(item);
                break;
            case "rename":
                var name = await Shell.Current.DisplayPromptAsync(Localization.L.Get("Act_Rename"), Localization.L.Get("Srv_RenamePrompt"), Localization.L.Get("Ok"), Localization.L.Get("Cancel"), initialValue: item.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    item.Profile = await _s.ProfileService.RenameAsync(item.Id, name, _s.Lifetime);
                }

                break;
            case "delete":
                if (await Shell.Current.DisplayAlertAsync(Localization.L.Get("Act_Delete"), Localization.L.F("Srv_DeleteConfirm", item.Name), Localization.L.Get("Act_Delete"), Localization.L.Get("Cancel")))
                {
                    await _s.ProfileService.DeleteAsync(item.Id, _s.Lifetime);
                    _all.Remove(item);
                    _byId.Remove(item.Id);
                    ApplyView();
                }

                break;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var text = Clipboard.Default.HasText ? await Clipboard.Default.GetTextAsync() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = await Shell.Current.DisplayPromptAsync(Localization.L.Get("Srv_Import"), Localization.L.Get("Srv_ImportPrompt"), Localization.L.Get("Srv_Import"), Localization.L.Get("Cancel"), maxLength: 1_000_000);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            await ImportTextAsync(text);
        }
    }

    /// <summary>Imports share links or a subscription link (from the clipboard, a typed link or a scanned QR code).</summary>
    public async Task ImportTextAsync(string text)
    {
        text = text.Trim();
        try
        {
            if (!text.Contains('\n', StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            {
                Message = "Adding subscription…";
                var sub = await _s.SubscriptionService.AddAsync(text, null, null, _s.Lifetime);
                Message = sub.LastError is null ? $"Added subscription \"{sub.Name}\" with {sub.ProfileCount} server(s)." : $"Added \"{sub.Name}\", but the first update failed: {sub.LastError}";
            }
            else
            {
                var result = await _s.ProfileService.ImportTextAsync(text, _s.Lifetime);
                Message = result.Imported.Count == 0
                    ? $"No valid servers found ({result.Issues.Count} problem(s))."
                    : $"Imported {result.Imported.Count} server(s)." + (result.Issues.Count > 0 ? $" {result.Issues.Count} skipped." : string.Empty);
            }

            await LoadAsync();
        }
        catch (Exception ex) when (ex is ProfileException or SubscriptionException or Subscriptions.Fetching.FetchException or InvalidOperationException)
        {
            Message = "Import failed: " + SecretRedactor.Redact(ex.Message);
        }
    }
}
