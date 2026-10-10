using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;

namespace JackiVpn.Desktop.ViewModels;

public sealed partial class SubscriptionItem : ObservableObject
{
    private readonly Func<SubscriptionItem, AutoUpdateInterval, Task> _setInterval;
    private readonly Func<SubscriptionItem, bool, Task>? _setSendDeviceId;

    [ObservableProperty]
    private Subscription _model;

    [ObservableProperty]
    private bool _isRefreshing;

    public SubscriptionItem(Subscription model, Func<SubscriptionItem, AutoUpdateInterval, Task> setInterval, Func<SubscriptionItem, bool, Task>? setSendDeviceId = null)
    {
        _model = model;
        _setInterval = setInterval;
        _setSendDeviceId = setSendDeviceId;
    }

    public bool IsOfficial => Model.IsOfficial;

    /// <summary>Official providers always get the device ID; for others it is the user's choice (default off).</summary>
    public bool SendDeviceId
    {
        get => Model.SendDeviceId ?? Model.IsOfficial;
        set
        {
            if (value != SendDeviceId && _setSendDeviceId is not null)
            {
                _ = _setSendDeviceId(this, value);
            }
        }
    }

    public bool CanChooseDeviceId => !Model.IsOfficial;

    public bool DeviceProblem => Model.DeviceLimit is DeviceLimitState.LimitReached or DeviceLimitState.DeviceIdRequired;

    /// <summary>Only what the provider actually reported — PasarGuard never reports counts, so none are shown.</summary>
    public string DeviceText => Model.DeviceLimit switch
    {
        DeviceLimitState.LimitReached => "Device limit reached — the provider refused this device. Remove an old device in your account.",
        DeviceLimitState.DeviceIdRequired => "The provider limits devices and needs a device ID: turn on \"Send device ID\".",
        DeviceLimitState.Registered => "Device limit active · this device is accepted.",
        _ => SendDeviceId ? "Device ID sent (counts toward the provider's device limit, if it has one)." : "Device ID not sent.",
    };

    private SubscriptionMetadata M => Model.Metadata;

    public bool HasAccountInfo => Model.Provider == SubscriptionProviderKind.PasarGuard;

    /// <summary>Device limit and online status from the panel (checked every 10 minutes).</summary>
    public string AccountText
    {
        get
        {
            var limit = M.DeviceLimit switch
            {
                null => "Device limit: not reported",
                0 => "No device limit",
                var n => $"Up to {n} devices at once",
            };
            var online = M.OnlineAtUtc is { } t
                ? (DateTimeOffset.UtcNow - t) switch
                {
                    { TotalMinutes: < 2 } => "online now",
                    { TotalMinutes: < 60 } d => $"last online {(int)d.TotalMinutes} min ago",
                    { TotalHours: < 48 } d => $"last online {(int)d.TotalHours} h ago",
                    var d => $"last online {(int)d.TotalDays} days ago",
                }
                : "online status not reported yet";
            var check = M.AccountCheckedUtc is { } c ? $" · checked {c.ToLocalTime():HH:mm}" : string.Empty;
            return $"{limit} · {online}{check}";
        }
    }

    public bool IsOnlineNow => M.OnlineAtUtc is { } o && DateTimeOffset.UtcNow - o < TimeSpan.FromMinutes(2);

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string Provider => Model.Provider == SubscriptionProviderKind.PasarGuard ? "PasarGuard" : "Generic";

    public string MaskedUrl => Model.MaskedUrl;

    public SubscriptionStatus Status => M.ComputeStatus(DateTimeOffset.UtcNow);

    public string Used => Format.Bytes(M.UsedBytes);

    public string Upload => Format.Bytes(M.UploadBytes);

    public string Download => Format.Bytes(M.DownloadBytes);

    public string Total => M.IsUnlimitedTraffic == true ? "Unlimited" : Format.Bytes(M.TotalBytes);

    public string Remaining => M.IsUnlimitedTraffic == true ? "Unlimited" : Format.Bytes(M.RemainingBytes);

    public double UsedPercent => M.UsedPercent ?? 0;

    public bool HasQuota => M.UsedPercent is not null;

    public string UsedPercentText => M.UsedPercent is { } p ? $"{p:0.#}%" : Format.Unknown;

    public string Expire => M.IsUnlimitedExpiry == true ? "Never" : M.ExpireUtc is { } e ? Format.When(e) : Format.Unknown;

    public string TimeLeft => M.IsUnlimitedExpiry == true ? "Unlimited" : Format.Duration(M.TimeRemaining(DateTimeOffset.UtcNow));

    public string LastUpdate => Format.When(Model.LastUpdateUtc);

    public string? LastError => Model.LastError;

    public int ProfileCount => Model.ProfileCount;

    public string Username => M.Username ?? Format.Unknown;

    public string? Announcement => M.Announcement;

    public string? SupportUrl => M.SupportUrl;

    public string? WebPageUrl => M.WebPageUrl;

    public string Endpoint => Model.ResolvedEndpoint ?? "—";

    public string FormatText => Model.LastFormat.ToString();

    public string UpdateInterval => Model.EffectiveUpdateInterval is { } i ? Format.Duration(i) : "Off";

    public string Ownership => Model.IsOfficial
        ? $"Official · {Domain.ProductInfo.SmartConnectName}"
        : Model.Ownership.IsOwned ? $"{Domain.ProductInfo.SmartConnectName} verified ({Model.Ownership.Method})" : "Third-party";

    public bool IsOwned => Model.Ownership.IsOwned;

    public AutoUpdateInterval AutoUpdate
    {
        get => Model.AutoUpdate;
        set
        {
            if (value != Model.AutoUpdate)
            {
                _ = _setInterval(this, value);
            }
        }
    }

    partial void OnModelChanged(Subscription value) => OnPropertyChanged(string.Empty);
}

public sealed partial class SubscriptionsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private string _newUrl = string.Empty;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    public SubscriptionsViewModel(AppServices services, IDialogService dialogs)
    {
        _s = services;
        _dialogs = dialogs;
        services.SubscriptionService.Updated += (_, r) => Ui.Post(() => _ = ReloadOneAsync(r.SubscriptionId));
        services.SubscriptionService.AccountChecked += (_, id) => Ui.Post(() => _ = ReloadOneAsync(id));
    }

    public ObservableCollection<SubscriptionItem> Items { get; } = [];

    public IReadOnlyList<AutoUpdateInterval> Intervals { get; } = Choices.Of<AutoUpdateInterval>();

    public async Task LoadAsync()
    {
        var subs = await _s.Subscriptions.GetAllAsync(_s.Lifetime);
        Items.Clear();
        foreach (var sub in subs)
        {
            Items.Add(new SubscriptionItem(sub, SetIntervalAsync, SetSendDeviceIdAsync));
        }
    }

    private async Task SetSendDeviceIdAsync(SubscriptionItem item, bool send)
    {
        await _s.SubscriptionService.SetSendDeviceIdAsync(item.Id, send, _s.Lifetime);
        await ReloadOneAsync(item.Id);
        Message = send
            ? $"{item.Name}: this device's ID will be sent on the next update."
            : $"{item.Name}: the device ID is no longer sent.";
    }

    private async Task ReloadOneAsync(string id)
    {
        var sub = await _s.Subscriptions.GetAsync(id, _s.Lifetime);
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (sub is null)
        {
            if (item is not null)
            {
                Items.Remove(item);
            }
        }
        else if (item is null)
        {
            Items.Add(new SubscriptionItem(sub, SetIntervalAsync, SetSendDeviceIdAsync));
        }
        else
        {
            item.Model = sub;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var url = NewUrl.Trim();
        if (url.Length == 0)
        {
            return;
        }

        IsBusy = true;
        Message = "Adding subscription…";
        try
        {
            var sub = await _s.SubscriptionService.AddAsync(url, string.IsNullOrWhiteSpace(NewName) ? null : NewName, null, _s.Lifetime);
            await ReloadOneAsync(sub.Id);
            NewUrl = string.Empty;
            NewName = string.Empty;
            Message = sub.LastError is null
                ? $"Added \"{sub.Name}\" with {sub.ProfileCount} server(s)."
                : $"Added \"{sub.Name}\", but the first update failed: {sub.LastError}";
        }
        catch (Exception ex) when (ex is SubscriptionException or Subscriptions.Fetching.FetchException or InvalidOperationException)
        {
            Message = "Could not add: " + SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(SubscriptionItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsRefreshing = true;
        try
        {
            var r = await _s.SubscriptionService.RefreshAsync(item.Id, _s.Lifetime);
            Message = r.Succeeded
                ? $"{item.Name}: {r.Added} new, {r.Changed} changed, {r.Removed} removed" + (r.Outcome == Subscriptions.Providers.FetchOutcome.NotModified ? " (not modified)." : ".")
                : $"{item.Name}: update failed — {r.Error}. Existing servers were kept.";
            await ReloadOneAsync(item.Id);
        }
        finally
        {
            item.IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        try
        {
            foreach (var item in Items.ToList())
            {
                await RefreshAsync(item);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameAsync(SubscriptionItem? item)
    {
        if (item is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync("Rename subscription", "Name:", item.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _s.SubscriptionService.RenameAsync(item.Id, name, _s.Lifetime);
            await ReloadOneAsync(item.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SubscriptionItem? item)
    {
        if (item is null || !_dialogs.Confirm("Delete subscription", $"Delete \"{item.Name}\" and its {item.ProfileCount} server(s)?", danger: true))
        {
            return;
        }

        await _s.SubscriptionService.DeleteAsync(item.Id, _s.Lifetime);
        Items.Remove(item);
    }

    /// <summary>Provider links open in the browser only after confirmation (they come from the subscription, not the user).</summary>
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            && _dialogs.Confirm("Open link", $"Open this provider link in your browser?{Environment.NewLine}{u.AbsoluteUri}"))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async Task SetIntervalAsync(SubscriptionItem item, AutoUpdateInterval interval)
    {
        await _s.SubscriptionService.SetAutoUpdateAsync(item.Id, interval, _s.Lifetime);
        await ReloadOneAsync(item.Id);
    }
}
