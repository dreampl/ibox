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

    public async Task InitializeAsync()
    {
        await Servers.LoadAsync();
        await Subscriptions.LoadAsync();
        await Dashboard.InitializeAsync();
        await Network.RefreshServiceAsync();
        Ecosystem.Start();
    }

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
