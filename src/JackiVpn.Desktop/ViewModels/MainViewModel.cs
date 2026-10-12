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
        Dashboard = new DashboardViewModel(services, dialogs, this);
        Servers = new ServersViewModel(services, dialogs);
        Subscriptions = new SubscriptionsViewModel(services, dialogs);
        Usage = new UsageViewModel(services, dialogs);
        Routing = new RoutingViewModel(services, dialogs);
        Dns = new DnsViewModel(services.Settings, services.DnsFinder, services.Rescue.CanProbeDirectly);
        Network = new NetworkViewModel(services, dialogs);
        Advanced = new AdvancedViewModel(services);
        Logs = new LogsViewModel(services, dialogs);
        Settings = new SettingsViewModel(services);
        About = new AboutViewModel(services);
        Ecosystem = new EcosystemViewModel(services);
        Update = new UpdateViewModel(services);

        Pages =
        [
            new NavItem("Dashboard", "", Dashboard),
            new NavItem("Servers", "", Servers),
            new NavItem("Subscriptions", "", Subscriptions),
            new NavItem("Usage", ((char)0xE9D9).ToString(), Usage),
            new NavItem("Routing", "", Routing),
            new NavItem("DNS", "", Dns),
            new NavItem("Network", "", Network),
            new NavItem("Advanced", "", Advanced),
            new NavItem("Logs", "", Logs),
            new NavItem("Settings", "", Settings),
            new NavItem("About", "", About),
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
        foreach (var p in Pages)
        {
            if (ReferenceEquals(p.Page, page))
            {
                Selected = p;
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

    /// <summary>The top bar's light/dark switch (leaves "System" for the shown opposite).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleTheme()
    {
        var next = Services.ThemeManager.IsLight ? AppTheme.Dark : AppTheme.Light;
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
}
