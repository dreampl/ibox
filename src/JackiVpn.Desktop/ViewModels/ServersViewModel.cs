using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Security;

namespace JackiVpn.Desktop.ViewModels;

public sealed partial class ServerItem : ObservableObject
{
    [ObservableProperty]
    private ProxyProfile _profile;

    [ObservableProperty]
    private NodeHealthSnapshot _health;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private bool _isTesting;

    /// <summary>Exit country last observed through this server (null until it has been used once).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagCode))]
    [NotifyPropertyChangedFor(nameof(FlagTip))]
    private string? _countryCode;

    /// <summary>The flag shown: the observed exit, else the country the server's name points at.</summary>
    public string? FlagCode => CountryCode ?? Domain.Visuals.CountryGuess.FromName(Profile.DisplayName);

    public string FlagTip => CountryCode is { } cc ? "Exit seen through this server: " + cc : FlagCode is { } guess ? "From the server's name: " + guess + " (not connected yet)" : "Country not known yet";

    public ServerItem(ProxyProfile profile, NodeHealthSnapshot health, string? subscriptionName, bool isOfficial = false)
    {
        _profile = profile;
        _health = health;
        SubscriptionName = subscriptionName ?? "Manual";
        IsOfficial = isOfficial;
    }

    /// <summary>From an official provider subscription (verified over HTTPS).</summary>
    public bool IsOfficial { get; }

    /// <summary>Measured fast (Excellent/Good) — the badge is only shown when a test backs it.</summary>
    public bool IsFast => Health.Label is HealthLabel.Excellent or HealthLabel.Good;

    public string Id => Profile.Id;

    public string Name => Profile.DisplayName;

    public string SubscriptionName { get; }

    public string Details
    {
        get
        {
            var parts = new List<string> { Names.Protocol(Profile.Protocol) };
            if (Profile.Transport.Type != TransportType.Tcp)
            {
                parts.Add(Names.Transport(Profile.Transport.Type));
            }

            if (Profile.Tls.IsReality)
            {
                parts.Add("Reality");
            }
            else if (Profile.Tls.Enabled)
            {
                parts.Add("TLS");
            }

            if (Profile.Tls.Fragment?.Enabled == true)
            {
                parts.Add("Fragment");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool IsFavorite => Profile.User.IsFavorite;

    public bool IsRemoved => Profile.SyncState == ProfileSyncState.RemovedByProvider;

    public bool IsNew => Profile.SyncState == ProfileSyncState.New;

    /// <summary>Time for a new connection plus the first request (handshakes included) — not a ping.</summary>
    public string Latency => Health.Label switch
    {
        HealthLabel.Unknown => "—",
        HealthLabel.Failed => "Failed",
        _ => Health.MedianLatencyMs is double ms and >= 1000 ? (ms / 1000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s" : Format.Ms(Health.MedianLatencyMs),
    };

    public string ScoreText => Health.Label switch
    {
        HealthLabel.Unknown => "Not tested",
        HealthLabel.Failed => "Not reachable",
        _ => $"{Health.Label} · score {Health.Score:0}",
    };

    public double Score => Health.Score;

    public HealthLabel Label => Health.Label;

    public string Tooltip => Health.Label == HealthLabel.Unknown
        ? "Not tested yet."
        : $"New connection + first request: median {Format.Ms(Health.MedianLatencyMs)} · jitter {Format.Ms(Health.JitterMs)} · success {Health.SuccessRate:P0} · {Health.SampleCount} samples"
          + (Health.LastFailure != ProbeFailureKind.None ? $" · last failure: {Health.LastFailure}" : string.Empty);

    partial void OnProfileChanged(ProxyProfile value) => OnPropertyChanged(string.Empty);

    partial void OnHealthChanged(NodeHealthSnapshot value)
    {
        OnPropertyChanged(nameof(Latency));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(IsFast));
    }
}

public sealed partial class ServersViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, ServerItem> _byId = new(StringComparer.Ordinal);
    private CancellationTokenSource? _testCts;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string _sortMode = "Default";

    [ObservableProperty]
    private string _groupFilter = "All";

    [ObservableProperty]
    private ServerItem? _selected;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testProgress = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    public ServersViewModel(AppServices services, IDialogService dialogs)
    {
        _s = services;
        _dialogs = dialogs;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ServerItem i && Matches(i);
        // The list shows which subscription each server belongs to (one collapsible group per subscription).
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerItem.SubscriptionName)));
        services.Health.Progress += (_, p) => Ui.Post(() =>
        {
            if (_byId.TryGetValue(p.ProfileId, out var item))
            {
                item.Health = p.Snapshot;
                item.IsTesting = false;
            }

            TestProgress = $"Tested {p.Completed} of {p.Total}";
        });
        services.Orchestrator.StatusChanged += (_, st) => Ui.Post(() =>
        {
            // The status arrives every second (traffic): walk the list only when the active server changed.
            var active = st.State == ConnectionState.Connected ? st.ProfileId : null;
            if (active == _shownActive)
            {
                return;
            }

            _shownActive = active;
            foreach (var i in Items)
            {
                i.IsActive = st.State == ConnectionState.Connected && i.Id == st.ProfileId;
            }
        });
        services.SubscriptionService.Updated += (_, _) => Ui.Post(() => _ = LoadAsync());
        services.Insights.Changed += (_, fingerprint) => Ui.Post(() =>
        {
            foreach (var i in Items.Where(i => i.Profile.Fingerprint == fingerprint))
            {
                i.CountryCode = _s.Insights.Get(fingerprint)?.Exit?.CountryCode;
            }
        });
    }

    public ObservableCollection<ServerItem> Items { get; } = [];

    private string? _shownActive;

    public ObservableCollection<string> Groups { get; } = ["All"];

    public IReadOnlyList<string> SortModes { get; } = ["Default", "Name", "Latency", "Score"];

    public ICollectionView View { get; }

    partial void OnFilterChanged(string value) => View.Refresh();

    partial void OnGroupFilterChanged(string value) => View.Refresh();

    partial void OnSortModeChanged(string value) => ApplySort();

    private bool Matches(ServerItem i)
    {
        if (GroupFilter != "All" && i.SubscriptionName != GroupFilter)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(Filter)
            || i.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
            || i.Details.Contains(Filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            switch (SortMode)
            {
                case "Name":
                    View.SortDescriptions.Add(new SortDescription(nameof(ServerItem.Name), ListSortDirection.Ascending));
                    break;
                case "Latency":
                    View.SortDescriptions.Add(new SortDescription(nameof(ServerItem.Label), ListSortDirection.Ascending));
                    View.SortDescriptions.Add(new SortDescription(nameof(ServerItem.Score), ListSortDirection.Descending));
                    break;
                case "Score":
                    View.SortDescriptions.Add(new SortDescription(nameof(ServerItem.Score), ListSortDirection.Descending));
                    break;
                default:
                    View.SortDescriptions.Add(new SortDescription(nameof(ServerItem.IsFavorite), ListSortDirection.Descending));
                    break;
            }
        }
    }

    public async Task LoadAsync()
    {
        var profiles = await _s.Profiles.GetAllAsync(_s.Lifetime);
        var subs = (await _s.Subscriptions.GetAllAsync(_s.Lifetime)).ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (_s.Health.AllSnapshots.Count == 0)
        {
            await _s.Health.LoadAsync(_s.Lifetime);
        }

        var selectedId = _s.Settings.Current.Connection.SelectedProfileId;
        var active = _s.Orchestrator.Status is { State: ConnectionState.Connected } st ? st.ProfileId : null;
        Items.Clear();
        _byId.Clear();
        foreach (var p in profiles)
        {
            var sub = p.Origin.SubscriptionId is { } sid && subs.TryGetValue(sid, out var s) ? s : null;
            var item = new ServerItem(p, _s.Health.Snapshot(p.Id), sub?.Name, sub?.IsOfficial == true)
            {
                IsDefault = p.Id == selectedId,
                IsActive = p.Id == active,
                CountryCode = _s.Insights.Get(p.Fingerprint)?.Exit?.CountryCode,
            };
            Items.Add(item);
            _byId[p.Id] = item;
        }

        var current = GroupFilter;
        Groups.Clear();
        Groups.Add("All");
        foreach (var g in Items.Select(i => i.SubscriptionName).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.CurrentCulture))
        {
            Groups.Add(g);
        }

        GroupFilter = Groups.Contains(current) ? current : "All";
        Summary = $"{Items.Count} servers · {Items.Count(i => i.Label is not (HealthLabel.Unknown or HealthLabel.Failed))} healthy";
        ApplySort();
    }

    [RelayCommand]
    private async Task TestAllAsync() => await TestAsync(View.Cast<ServerItem>().ToList());

    [RelayCommand]
    private async Task TestOneAsync(ServerItem? item)
    {
        if (item is not null)
        {
            await TestAsync([item]);
        }
    }

    [RelayCommand]
    private void CancelTest() => _testCts?.Cancel();

    private async Task TestAsync(IReadOnlyList<ServerItem> items)
    {
        if (IsTesting || items.Count == 0)
        {
            return;
        }

        IsTesting = true;
        TestProgress = $"Testing {items.Count} servers…";
        _testCts = CancellationTokenSource.CreateLinkedTokenSource(_s.Lifetime);
        foreach (var i in items)
        {
            i.IsTesting = true;
        }

        try
        {
            await Task.Run(() => _s.Health.TestAsync(items.Select(i => i.Profile).ToList(), new ProbeRequestOptions { Force = true }, _testCts.Token));
            TestProgress = "Test finished.";
        }
        catch (OperationCanceledException)
        {
            TestProgress = "Test cancelled.";
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
            Summary = $"{Items.Count} servers · {Items.Count(i => i.Label is not (HealthLabel.Unknown or HealthLabel.Failed))} healthy";
        }
    }

    /// <summary>Use this server: manual mode, remembered as the default, connected (or switched to) now.</summary>
    [RelayCommand]
    private async Task ConnectAsync(ServerItem? item)
    {
        if (item is null)
        {
            return;
        }

        SetDefault(item);
        _s.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = false, SelectedProfileId = item.Id } });
        if (_s.Orchestrator.State == ConnectionState.Connected)
        {
            await _s.Orchestrator.SwitchToAsync(item.Id, _s.Lifetime);
        }
        else if (ConnectionStateRules.IsIdle(_s.Orchestrator.State))
        {
            await _s.Orchestrator.ConnectAsync(new ConnectRequest { ProfileId = item.Id, Smart = false }, _s.Lifetime);
        }
    }

    [RelayCommand]
    private void SelectDefault(ServerItem? item)
    {
        if (item is null)
        {
            return;
        }

        SetDefault(item);
        _s.Settings.Update(x => x with { Connection = x.Connection with { SelectedProfileId = item.Id } });
    }

    private void SetDefault(ServerItem item)
    {
        foreach (var i in Items)
        {
            i.IsDefault = ReferenceEquals(i, item);
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ServerItem? item)
    {
        if (item is not null)
        {
            item.Profile = await _s.ProfileService.SetFavoriteAsync(item.Id, !item.IsFavorite, _s.Lifetime);
        }
    }

    [RelayCommand]
    private async Task RenameAsync(ServerItem? item)
    {
        if (item is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync("Rename server", "New display name:", item.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            item.Profile = await _s.ProfileService.RenameAsync(item.Id, name, _s.Lifetime);
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync(ServerItem? item)
    {
        if (item is not null)
        {
            await _s.ProfileService.DuplicateAsync(item.Id, _s.Lifetime);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ServerItem? item)
    {
        if (item is null || !_dialogs.Confirm("Delete server", $"Delete \"{item.Name}\"? Servers from a subscription come back on its next update unless the provider removed them.", danger: true))
        {
            return;
        }

        await _s.ProfileService.DeleteAsync(item.Id, _s.Lifetime);
        Items.Remove(item);
        _byId.Remove(item.Id);
    }

    /// <summary>
    /// Full editor: the normalized profile as JSON (credentials included — shown only locally, never logged).
    /// The result is validated before saving; edits to subscription servers are saved as a manual copy.
    /// </summary>
    [RelayCommand]
    private async Task EditAsync(ServerItem? item)
    {
        if (item is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(item.Profile, new JsonSerializerOptions(JsonDefaults.Storage) { WriteIndented = true });
        var edited = await _dialogs.PromptAsync("Edit server", "Edit the normalized configuration. Unknown fields are preserved. It is validated before saving.", json, multiline: true, monospace: true);
        if (edited is null || edited == json)
        {
            return;
        }

        try
        {
            var profile = JsonSerializer.Deserialize<ProxyProfile>(edited, JsonDefaults.Storage) ?? throw new JsonException("Empty document.");
            var validation = ProfileValidator.Validate(profile);
            if (!validation.IsValid)
            {
                _dialogs.Info("Not saved", string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message)));
                return;
            }

            await _s.ProfileService.SaveEditedAsync(profile with { Id = item.Id }, _s.Lifetime);
            await LoadAsync();
        }
        catch (JsonException ex)
        {
            _dialogs.Info("Not saved", "Invalid JSON: " + SecretRedactor.Redact(ex.Message));
        }
    }

    [RelayCommand]
    private async Task ImportClipboardAsync()
    {
        string text;
        try
        {
            text = System.Windows.Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            text = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = await _dialogs.PromptAsync("Import", "Paste share links (vless://, vmess://, trojan://, ss://, hysteria2://, tuic://, wireguard://…), a Base64 list, or a sing-box / Xray / Clash config:", multiline: true, monospace: true) ?? string.Empty;
        }

        await ImportTextAsync(text);
    }

    [RelayCommand]
    private async Task ImportPasteAsync()
    {
        var text = await _dialogs.PromptAsync("Import", "Paste share links, a Base64 list, or a sing-box / Xray / Clash config:", multiline: true, monospace: true);
        await ImportTextAsync(text ?? string.Empty);
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var path = _dialogs.OpenFile("Config files|*.json;*.yaml;*.yml;*.txt;*.conf|All files|*.*");
        if (path is null)
        {
            return;
        }

        try
        {
            var result = await _s.ProfileService.ImportFileAsync(path, _s.Lifetime);
            ReportImport(result.Imported.Count, result.Issues.Count, result.Format.ToString());
            await LoadAsync();
        }
        catch (Exception ex) when (ex is Application.Profiles.ProfileException or System.IO.IOException or UnauthorizedAccessException)
        {
            _dialogs.Info("Import failed", SecretRedactor.Redact(ex.Message));
        }
    }

    private async Task ImportTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var result = await _s.ProfileService.ImportTextAsync(text, _s.Lifetime);
            ReportImport(result.Imported.Count, result.Issues.Count, result.Format.ToString());
            await LoadAsync();
        }
        catch (Application.Profiles.ProfileException ex)
        {
            _dialogs.Info("Import failed", SecretRedactor.Redact(ex.Message));
        }
    }

    private void ReportImport(int imported, int issues, string format) =>
        _dialogs.Info("Import", imported == 0
            ? $"No valid servers found ({issues} problem(s), format: {format})."
            : $"Imported {imported} server(s) from {format}." + (issues > 0 ? $" {issues} entr(ies) were skipped; see Logs for details." : string.Empty));
}
