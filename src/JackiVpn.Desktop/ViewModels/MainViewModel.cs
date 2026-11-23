using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JackiVpn.Application.Notifications;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Desktop.ViewModels;

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);

public sealed partial class ToastItem : ObservableObject
{
    [ObservableProperty]
    private bool _isClosing;

    public required string Title { get; init; }
    public required string Message { get; init; }
    public bool IsWarning { get; init; }
    public DateTime At { get; init; } = DateTime.Now;
    public string Time => At.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private NavItem? _selected;

    [ObservableProperty]
    private string _statusLine = "Not connected";

    [ObservableProperty]
    private ConnectionState _state;

    public MainViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        services.Accounts.Changed += (_, _) => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(ActiveAccount));
            OnPropertyChanged(nameof(ActiveName)); OnPropertyChanged(nameof(ActivePicture));
            OnPropertyChanged(nameof(ProfileTip));
        });
        services.Ibox.SignInChanged += (_, _) => Ui.Post(() => { OnPropertyChanged(nameof(IboxSignedIn)); OnPropertyChanged(nameof(ActiveName)); OnPropertyChanged(nameof(ActivePicture)); });
        services.Ibox.AvatarChanged += (_, _) => Ui.Post(() => OnPropertyChanged(nameof(ActivePicture)));
        Dashboard = new DashboardViewModel(services, dialogs, this);
        Servers = new ServersViewModel(services, dialogs);
        Subscriptions = new SubscriptionsViewModel(services, dialogs);
        Usage = new UsageViewModel(services, dialogs);
        Routing = new RoutingViewModel(services, dialogs);
        Dns = new DnsViewModel(
            services.Settings,
            services.DnsFinder,
            services.Rescue.CanProbeDirectly,
            () => services.RoutingWatcher.ApplyNowAsync("DNS changed"),
            () => services.Orchestrator.State == Domain.Connection.ConnectionState.Connected,
            services.AdapterDns);
        Network = new NetworkViewModel(services, dialogs);
        Advanced = new AdvancedViewModel(services);
        Logs = new LogsViewModel(services, dialogs);
        Settings = new SettingsViewModel(services);
        About = new AboutViewModel(services);
        Ecosystem = new EcosystemViewModel(services);
        Update = new UpdateViewModel(services);

        Pages =
        [
            new NavItem("Home", "", new WorkspaceViewModel(new("Connection", Dashboard), new("Usage", Usage))),
            new NavItem("Servers", "", Servers),
            new NavItem("Routing", "", new WorkspaceViewModel(new("Rules", Routing), new("DNS", Dns), new("Network", Network), new("Workplace", new Views.UsageView(true) { DataContext = Usage }))),
            new NavItem("Settings", "", new WorkspaceViewModel(new("General", Settings), new("Advanced", Advanced), new("Logs", Logs), new("Sync devices", Ecosystem), new("About", About))),
        ];
        _selected = Pages[0];

        services.Notifications.Raised += (_, n) => Ui.Post(() => ShowToast(n));
        services.Orchestrator.StatusChanged += (_, s) => Ui.Post(() =>
        {
            State = s.State;
            StatusLine = s.State switch
            {
                ConnectionState.Connected => $"Connected · {s.ProfileName}",
                ConnectionState.Faulted => "Connection problem",
                _ => s.State.ToString(),
            };
            (Tagline, PillText) = s.State switch
            {
                ConnectionState.Connected => ("Secure • Fast • Stable", "VPN Connected"),
                ConnectionState.Faulted => ("Connection problem — open the dashboard", "VPN Error"),
                ConnectionState.Disconnected => ("Not protected", "VPN Off"),
                ConnectionState.Disconnecting => ("Disconnecting…", "Stopping…"),
                _ => ("Connecting…", "Connecting…"),
            };
        });
    }

    public ObservableCollection<NavItem> Pages { get; }

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public DashboardViewModel Dashboard { get; }
    public ServersViewModel Servers { get; }
    public SubscriptionsViewModel Subscriptions { get; }
    public UsageViewModel Usage { get; }
    public RoutingViewModel Routing { get; }
    public DnsViewModel Dns { get; }
    public NetworkViewModel Network { get; }
    public AdvancedViewModel Advanced { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }
    public EcosystemViewModel Ecosystem { get; }

    public UpdateViewModel Update { get; }

    public void Navigate(ObservableObject page)
    {
        if (ReferenceEquals(page, Subscriptions)) { ManageSubscriptionsCommand.Execute(null); return; }
        foreach (var p in Pages)
        {
            if (ReferenceEquals(p.Page, page))
            {
                Selected = p;
            }
            else if (p.Page is WorkspaceViewModel workspace)
            {
                foreach (var tab in workspace.Tabs)
                {
                    if (ReferenceEquals(tab.Page, page))
                    {
                        workspace.SelectedTab = tab;
                        Selected = p;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>The main window is on screen or not (minimized / tray): pages pause their live updates.</summary>
    public void SetOnScreen(bool shown) => Dashboard.SetOnScreen(shown);

    public async Task InitializeAsync()
    {
        await Servers.LoadAsync();
        await Subscriptions.LoadAsync();
        await Dashboard.InitializeAsync();
        await Network.RefreshServiceAsync();
        Ecosystem.Start();
    }

    [ObservableProperty]
    private string _tagline = "Not protected";

    [ObservableProperty]
    private string _pillText = "VPN Off";

    [ObservableProperty]
    private int _unreadNotices;

    [ObservableProperty]
    private bool _noticesOpen;

    /// <summary>Recent notifications (newest first), shown under the bell.</summary>
    public ObservableCollection<ToastItem> Notices { get; } = [];

    partial void OnNoticesOpenChanged(bool value)
    {
        if (value)
        {
            UnreadNotices = 0;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleNotices() => NoticesOpen = !NoticesOpen;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearNotices()
    {
        Notices.Clear();
        UnreadNotices = 0;
    }

    /// <summary>The rail's profile button: switch, add and edit local profiles.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenProfiles() => new Views.ProfilesWindow(_services).Show();

    /// <summary>The first-start tour (also from About); ending it with nothing set up opens Subscriptions.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public Task ShowTourAsync()
    {
        Views.EasySetupView.Show(_services, this);
        return Task.CompletedTask;
    }

    /// <summary>The active profile, for the rail avatar.</summary>
    public Application.Accounts.LocalAccount? ActiveAccount => _services.Accounts.Active;

    public string ActiveName => _services.Ibox.SignedIn && _services.Ibox.User is { } user ? (string.IsNullOrWhiteSpace(user.FirstName) ? user.Display : user.FirstName) : ActiveAccount?.Name ?? "Profile";

    public string? ActivePicture => _services.Ibox.PhotoPath ?? (ActiveAccount is { } a ? _services.Accounts.PicturePath(a) : null);

    public string ProfileTip => "Profile: " + ActiveName;

    /// <summary>The top bar's light/dark switch (leaves "System" for the shown opposite).</summary>
    private AppTheme _lastDarkTheme = AppTheme.Gray;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleTheme()
    {
        if (!Services.ThemeManager.IsLight)
        {
            _lastDarkTheme = _services.Settings.Current.Ui.Theme is AppTheme.Black or AppTheme.Blue ? _services.Settings.Current.Ui.Theme : AppTheme.Gray;
        }
        var next = Services.ThemeManager.IsLight ? _lastDarkTheme : AppTheme.Light;
        _services.Settings.Update(x => x with { Ui = x.Ui with { Theme = next } });
        OnPropertyChanged(nameof(ThemeGlyph));
    }

    /// <summary>Sun on the dark theme (switch to light), moon on the light theme.</summary>
    public string ThemeGlyph => Services.ThemeManager.IsLight ? ((char)0xE708).ToString() : ((char)0xE706).ToString();

    public void RefreshTheme() => OnPropertyChanged(nameof(ThemeGlyph));

    public void ShowToast(string title, string message, bool warning = false) =>
        ShowToast(new AppNotification(warning ? NotificationCategory.ConnectionFailed : NotificationCategory.Connected, title, message));

    private void ShowToast(AppNotification n)
    {
        var item = new ToastItem
        {
            Title = n.Title,
            Message = n.Message,
            IsWarning = n.Category is NotificationCategory.ConnectionFailed or NotificationCategory.CoreCrashed
                or NotificationCategory.KillSwitchActivated or NotificationCategory.SubscriptionExpired or NotificationCategory.TrafficAlmostFinished,
        };
        Notices.Insert(0, item);
        while (Notices.Count > 30)
        {
            Notices.RemoveAt(Notices.Count - 1);
        }

        if (!NoticesOpen)
        {
            UnreadNotices++;
        }

        Toasts.Add(item);
        while (Toasts.Count > 4)
        {
            Toasts.RemoveAt(0);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(item.IsWarning ? 8 : 5) };
        timer.Tick += (_, _) =>
        {
            if (!item.IsClosing)
            {
                // Fade out first (the template animates IsClosing), then remove.
                item.IsClosing = true;
                timer.Interval = TimeSpan.FromMilliseconds(Views.Motion.Reduced ? 0 : 260);
                return;
            }

            timer.Stop();
            Toasts.Remove(item);
        };
        timer.Start();
    }

    public bool ReduceMotion => Views.Motion.Reduced;

    /// <summary>The user asked for the compact Mini view (true) or the full Pro window (false); the app switches.</summary>
    public event EventHandler<bool>? ViewModeRequested;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowMini() => ViewModeRequested?.Invoke(this, true);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowPro() => ViewModeRequested?.Invoke(this, false);

    /// <summary>Signed in with Telegram: only then the music and subscription buttons show.</summary>
    public bool IboxSignedIn => _services.Ibox.SignedIn;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ManageSubscriptionsAsync()
    {
        var view = new Views.SubscriptionsView { DataContext = Subscriptions };
        Views.MainWindow.Current?.OpenDrawer("Subscriptions", view, width: 1000);
        await Subscriptions.LoadAsync();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenSubscription() => Views.AccountWindow.Open(_services.Ibox, Views.AccountWindow.SubscriptionTab);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenMusic() => Views.AccountWindow.Open(_services.Ibox, Views.AccountWindow.MusicTab);
}
