using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;
using JackiVpn.Security;

namespace JackiVpn.Mobile.ViewModels;

public sealed partial class SubscriptionItem : ObservableObject
{
    [ObservableProperty] private Subscription _model;
    [ObservableProperty] private bool _isRefreshing;

    public SubscriptionItem(Subscription model) => _model = model;

    private SubscriptionMetadata M => Model.Metadata;

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string Provider => Model.Provider == SubscriptionProviderKind.PasarGuard ? "PasarGuard" : "Subscription";

    public bool IsOfficial => Model.IsOfficial;

    public bool SendsDeviceId => Model.SendDeviceId ?? Model.IsOfficial;

    public bool DeviceProblem => Model.DeviceLimit is DeviceLimitState.LimitReached or DeviceLimitState.DeviceIdRequired;

    /// <summary>Only what the provider reported (PasarGuard never reports device counts).</summary>
    public string DeviceText => Model.DeviceLimit switch
    {
        DeviceLimitState.LimitReached => L.Get("Sub_DeviceLimitReached"),
        DeviceLimitState.DeviceIdRequired => L.Get("Sub_DeviceIdRequired"),
        DeviceLimitState.Registered => L.Get("Sub_DeviceRegistered"),
        _ => SendsDeviceId ? L.Get("Sub_DeviceIdSent") : string.Empty,
    };

    public bool HasDeviceText => DeviceText.Length > 0;

    /// <summary>"Up to 3 devices" from the panel (PasarGuard hwid_limit), when it reports one.</summary>
    public string DeviceLimitText => M.DeviceLimit switch
    {
        null => L.Get("Sub_DevicesUnknown"),
        0 => L.Get("Sub_DevicesUnlimited"),
        var n => L.N(L.F("Sub_DevicesUpTo", n)),
    };

    /// <summary>When the account was last online on any device (from the panel, checked every 10 minutes).</summary>
    public string OnlineText => M.OnlineAtUtc is { } t
        ? (DateTimeOffset.UtcNow - t) switch
        {
            { TotalMinutes: < 2 } => L.Get("Sub_OnlineNow"),
            { TotalMinutes: < 60 } d => L.N(L.F("Sub_OnlineMinutes", (int)d.TotalMinutes)),
            { TotalHours: < 48 } d => L.N(L.F("Sub_OnlineHours", (int)d.TotalHours)),
            var d => L.N(L.F("Sub_OnlineDays", (int)d.TotalDays)),
        }
        : L.Get("Sub_OnlineUnknown");

    public bool IsOnlineNow => M.OnlineAtUtc is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(2);

    public bool HasAccountInfo => Model.Provider == SubscriptionProviderKind.PasarGuard;

    public string CheckedText => M.AccountCheckedUtc is { } c ? L.N(L.F("Sub_Checked", c.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))) : string.Empty;

    public Color DeviceColor => DeviceProblem ? Color.FromArgb("#EF4444") : Color.FromArgb("#6B7280");

    public string MaskedUrl => Model.MaskedUrl;

    public SubscriptionStatus Status => M.ComputeStatus(DateTimeOffset.UtcNow);

    public Color StatusColor => Status switch
    {
        SubscriptionStatus.Active => Color.FromArgb("#22C55E"),
        SubscriptionStatus.Unknown => Color.FromArgb("#6B7280"),
        SubscriptionStatus.OnHold => Color.FromArgb("#F59E0B"),
        _ => Color.FromArgb("#EF4444"),
    };

    public string Usage => L.N(M.IsUnlimitedTraffic == true
        ? L.F("Sub_UsedUnlimited", Fmt.Bytes(M.UsedBytes))
        : L.F("Sub_UsedOf", Fmt.Bytes(M.UsedBytes), Fmt.Bytes(M.TotalBytes)));

    public double UsedFraction => (M.UsedPercent ?? 0) / 100.0;

    public bool HasQuota => M.UsedPercent is not null;

    public Color QuotaColor => M.UsedPercent switch
    {
        >= 90 => Color.FromArgb("#EF4444"),
        >= 70 => Color.FromArgb("#F59E0B"),
        _ => Color.FromArgb("#7C5CFF"),
    };

    public string Expiry => L.N(M.IsUnlimitedExpiry == true ? L.Get("Sub_NeverExpires") : M.ExpireUtc is null ? L.Get("Sub_ExpiryUnknown") : L.F("Sub_ExpiresIn", Fmt.Remaining(M.TimeRemaining(DateTimeOffset.UtcNow))));

    public string Footer => L.N(L.F("Sub_Footer", Model.ProfileCount, Fmt.When(Model.LastUpdateUtc)));

    public string? LastError => Model.LastError;

    public bool HasError => !string.IsNullOrEmpty(Model.LastError);

    public string? Announcement => M.Announcement;

    public bool HasAnnouncement => !string.IsNullOrWhiteSpace(M.Announcement);

    partial void OnModelChanged(Subscription value) => OnPropertyChanged(string.Empty);
}

public sealed partial class SubscriptionsViewModel : ObservableObject
{
    private readonly MobileServices _s;

    [ObservableProperty] private string _newUrl = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string? _message;

    public SubscriptionsViewModel(MobileServices services)
    {
        _s = services;
        services.SubscriptionService.Updated += (_, r) => Fmt.OnUi(() => _ = ReloadOneAsync(r.SubscriptionId));
        services.SubscriptionService.AccountChecked += (_, id) => Fmt.OnUi(() => _ = ReloadOneAsync(id));
    }

    public ObservableCollection<SubscriptionItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public async Task LoadAsync()
    {
        await _s.InitializeAsync();
        var subs = await _s.Subscriptions.GetAllAsync(_s.Lifetime);
        Items.Clear();
        foreach (var sub in subs)
        {
            Items.Add(new SubscriptionItem(sub));
        }

        OnPropertyChanged(nameof(IsEmpty));
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
            Items.Add(new SubscriptionItem(sub));
        }
        else
        {
            item.Model = sub;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (Clipboard.Default.HasText && await Clipboard.Default.GetTextAsync() is { } text)
        {
            NewUrl = text.Trim();
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var url = NewUrl.Trim();
        if (url.Length == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Message = L.Get("Sub_Adding");
        try
        {
            var sub = await _s.SubscriptionService.AddAsync(url, null, null, _s.Lifetime);
            await ReloadOneAsync(sub.Id);
            NewUrl = string.Empty;
            Message = sub.LastError is null
                ? L.N(L.F("Sub_Added", sub.Name, sub.ProfileCount))
                : L.F("Sub_AddedFailed", sub.Name, sub.LastError);
        }
        catch (Exception ex) when (ex is SubscriptionException or Subscriptions.Fetching.FetchException or InvalidOperationException)
        {
            Message = L.Get("Sub_AddFailed") + " " + SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        IsRefreshing = true;
        try
        {
            foreach (var item in Items.ToList())
            {
                await RefreshOneAsync(item);
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task RefreshOneAsync(SubscriptionItem? item)
    {
        if (item is null || item.IsRefreshing)
        {
            return;
        }

        item.IsRefreshing = true;
        try
        {
            var r = await _s.SubscriptionService.RefreshAsync(item.Id, _s.Lifetime);
            Message = r.Succeeded
                ? L.N(L.F("Sub_Updated", item.Name, r.Added, r.Changed, r.Removed))
                : L.F("Sub_UpdateFailed", item.Name, r.Error);
            await ReloadOneAsync(item.Id);
        }
        finally
        {
            item.IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task MoreAsync(SubscriptionItem? item)
    {
        if (item is null)
        {
            return;
        }

        var actions = new List<(string Key, string Label, string Icon, bool Danger)>
        {
            ("update", L.Get("Sub_UpdateNow"), "refresh", false),
            ("check", L.Get("Sub_CheckDevices"), "devices", false),
            ("sync", L.Get("Sub_SendToDevice"), "sync", false),
            ("rename", L.Get("Act_Rename"), "edit", false),
        };
        if (!item.IsOfficial)
        {
            actions.Add((item.SendsDeviceId ? "idoff" : "idon", L.Get(item.SendsDeviceId ? "Sub_StopDeviceId" : "Sub_SendDeviceId"), "fingerprint", false));
        }

        actions.Add(("delete", L.Get("Act_Delete"), "trash", true));
        var choice = await Controls.Sheets.ActionsAsync(item.Name, [.. actions]);
        switch (choice)
        {            case "update":
                await RefreshOneAsync(item);
                break;
            case "check":
                item.IsRefreshing = true;
                try
                {
                    await _s.SubscriptionService.CheckAccountAsync(item.Id, _s.Lifetime);
                    await ReloadOneAsync(item.Id);
                }
                finally
                {
                    item.IsRefreshing = false;
                }

                break;
            case "sync":
                await Shell.Current.GoToAsync("ecosystem");
                break;
            case "idon" or "idoff":
                await _s.SubscriptionService.SetSendDeviceIdAsync(item.Id, choice == "idon", _s.Lifetime);
                await ReloadOneAsync(item.Id);
                Message = L.Get(choice == "idon" ? "Sub_DeviceIdOnMsg" : "Sub_DeviceIdOffMsg");
                break;
            case "rename":
                var name = await Shell.Current.DisplayPromptAsync(L.Get("Act_Rename"), L.Get("Sub_NamePrompt"), L.Get("Ok"), L.Get("Cancel"), initialValue: item.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    await _s.SubscriptionService.RenameAsync(item.Id, name, _s.Lifetime);
                    await ReloadOneAsync(item.Id);
                }

                break;
            case "delete":
                if (await Shell.Current.DisplayAlertAsync(L.Get("Act_Delete"), L.F("Sub_DeleteConfirm", item.Name, item.Model.ProfileCount), L.Get("Act_Delete"), L.Get("Cancel")))
                {
                    await _s.SubscriptionService.DeleteAsync(item.Id, _s.Lifetime);
                    Items.Remove(item);
                    OnPropertyChanged(nameof(IsEmpty));
                }

                break;
        }
    }
}
