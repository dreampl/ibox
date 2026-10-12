using System;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Notifications;
using JackiVpn.Application.Profiles;
using JackiVpn.Application.Routing;
using JackiVpn.Application.Sessions;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Cores;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Infrastructure.Logging;
using JackiVpn.Infrastructure.Persistence;
using JackiVpn.Infrastructure.Updates;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.Monitoring;
using JackiVpn.Networking.Recovery;
using JackiVpn.Networking.SystemProxy;
using JackiVpn.Routing;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging;
using JackiVpn.Application.Settings;

namespace JackiVpn.Desktop.Composition;

/// <summary>
/// Composition root of the desktop process (hand-wired: the graph is small, explicit and easy to audit).
/// Everything here runs as the interactive user; privileged operations go through <see cref="ServiceControl"/>.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RedactingFileLoggerProvider _logProvider;
    private readonly SafeHttpFetcher _fetcher;

    public AppServices(INotificationSink notificationSink)
    {
        Paths = AppPaths.Default();
        SecureFileSystem.CreatePrivateDirectory(Paths.UserDataRoot, PrivateScope.CurrentUser);
        foreach (var dir in new[] { Paths.UserLogsDir, Paths.UserStateDir, Paths.UserRuleSetCacheDir, Paths.UserCoreWorkDir, Paths.DiagnosticsDir })
        {
            SecureFileSystem.CreatePrivateDirectory(dir, PrivateScope.CurrentUser);
        }

        LogBuffer = new LogBuffer(5000);
        LoggingOptions = new LoggingOptions { Directory = Paths.UserLogsDir, FilePrefix = "desktop" };
        _logProvider = new RedactingFileLoggerProvider(LoggingOptions, LogBuffer);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_logProvider));
        Logger = LoggerFactory.CreateLogger("JackiVpn.Desktop");

        Database = new JackiDatabase(Paths.DatabasePath);
        var protector = new DpapiSecretProtector();
        Profiles = new SqliteProfileRepository(Database, protector);
        Subscriptions = new SqliteSubscriptionRepository(Database, protector);
        HealthHistory = new SqliteHealthHistoryRepository(Database);
        ConnectionHistory = new SqliteConnectionHistoryRepository(Database);
        Settings = new SettingsStore(new SqliteSettingsRepository(Database), LoggerFactory.CreateLogger("JackiVpn.Settings"));
        Func<AppSettings> settings = () => Settings.Current;
        Accounts = new Application.Accounts.LocalAccountService(System.IO.Path.Combine(Paths.UserDataRoot, "profiles"), protector, Settings, LoggerFactory.CreateLogger("JackiVpn.Profiles"));

        Catalog = new CoreBinaryCatalog(Paths.CoresDir);
        SingBox = new SingBoxCoreEngine(Catalog, LoggerFactory.CreateLogger<SingBoxCoreEngine>());
        Xray = new XrayCoreEngine(Catalog, LoggerFactory.CreateLogger<XrayCoreEngine>());
        ICoreEngine? xray = Catalog.IsInstalled(CoreKind.Xray) ? Xray : null;

        UserJournal = new NetworkStateJournal(Paths.UserJournalPath);
        WinInet = Sandboxed ? new SandboxProxyApi() : new WinInetProxyApi();
        SystemProxy = new SystemProxyManager(WinInet, UserJournal, LoggerFactory.CreateLogger("JackiVpn.SystemProxy"));
        var recoveryLogger = LoggerFactory.CreateLogger("JackiVpn.Recovery");
        UserRecovery = new RecoveryCoordinator(
            UserJournal,
            [new WinInetRecoveryHandler(SystemProxy), new OrphanCoreHandler(recoveryLogger)],
            [new SuspiciousProxyReport(WinInet, new ConnectionSettings().ProxyBypassList), new DnsFlushStep()],
            recoveryLogger);

        Notifications = new NotificationService(notificationSink, () => Settings.Current.Notifications, LoggerFactory.CreateLogger("JackiVpn.Notifications"));
        Disconnects = new DisconnectTracker();
        Insights = new InsightStore(System.IO.Path.Combine(Paths.UserStateDir, "insights.json"));
        // Scores reflect the connection as it will really be made (fingerprint, tuned handshake, learned fixes):
        // PrepareProfile is set once the tuner exists (below, with the network key).
        Health = new HealthCheckService(Catalog, SingBox, xray, HealthHistory, settings, Disconnects.Recent, Paths.UserCoreWorkDir, LoggerFactory.CreateLogger("JackiVpn.Health"));
        LiveProbe = new HttpLiveProbe();
        SpeedTest = new SpeedTestService();

        Parser = new ConfigParserPipeline();
        _fetcher = new SafeHttpFetcher();
        // "Windows PC" on purpose: the computer name often contains the user's real name.
        SubscriptionService = new SubscriptionService(Subscriptions, Profiles, () => _fetcher, Parser, settings, LoggerFactory.CreateLogger("JackiVpn.Subscriptions"), Notifications,
            device: () => Settings.Current.Device.Hwid is { } hwid ? new Domain.Subscriptions.DeviceIdentity(hwid, "Windows", Environment.OSVersion.Version.ToString(), "Windows PC") : null);
        ProfileService = new ProfileService(Profiles, Parser);
        RuleSets = new RuleSetManager(Paths.UserRuleSetCacheDir, new RuleSetDownloader(() => _fetcher), LoggerFactory.CreateLogger("JackiVpn.RuleSets"))
        {
            SeedDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "rulesets-seed"),
        };
        var updateLog = LoggerFactory.CreateLogger("JackiVpn.Updates");
        Updates = new UpdateMonitor(new AppUpdateService(updateLog), () => Settings.Current.Updates, CurrentVersion, "windows", null, updateLog);

        Service = new ServiceControl(LoggerFactory.CreateLogger("JackiVpn.ServiceControl"));
        AdapterDns = new Application.Connectivity.AdapterDnsService(new Services.WindowsAdapterDnsControl(Service), System.IO.Path.Combine(Paths.UserStateDir, "adapter-dns-original.json"), LoggerFactory.CreateLogger("JackiVpn.AdapterDns"));
        var plans = new SessionPlanFactory(Catalog) { FlushDns = static () => JackiVpn.Networking.Adapters.NetworkInspector.FlushDnsCache() };
        var sessionLogger = LoggerFactory.CreateLogger("JackiVpn.Session");
        NetworkMonitor = new NetworkChangeMonitor(LoggerFactory.CreateLogger("JackiVpn.Network"), Domain.ProductInfo.TunInterfaceName);
        Workplace = new Application.Workplace.WorkplaceService(new WindowsNetworkFacts(), settings, LoggerFactory.CreateLogger("JackiVpn.Workplace"));
        Func<string> networkKey = () => Workplace.Facts.Key is { Length: > 0 } key ? key : "default";
        Tuner = new ConnectionTuner(Catalog, SingBox, xray, HealthHistory, Insights, settings, Paths.UserCoreWorkDir, LoggerFactory.CreateLogger("JackiVpn.Tuning")) { NetworkKey = networkKey };
        Health.PrepareProfile = Tuner.PrepareForProbe;
        Rescue = new Application.Connectivity.EndpointRescue(Tuner, Insights, settings, networkKey, LoggerFactory.CreateLogger("JackiVpn.Rescue"))
        {
            // In TUN mode while connected the app's own sockets use the tunnel: probing then would measure the tunnel.
            CanProbeDirectly = () => Orchestrator!.State != ConnectionState.Connected || Settings.Current.Connection.Mode != ConnectionMode.Tun,
        };
        DnsFinder = new Application.Connectivity.DnsFinder(System.IO.Path.Combine(Paths.UserStateDir, "dns-choice.json"), networkKey, LoggerFactory.CreateLogger("JackiVpn.Dns"))
        {
            Extra = () => Settings.Current.Dns.Custom.Select(Application.Connectivity.DnsCandidate.From),
        };
        Background = new Application.Background.BackgroundPolicy(new Services.WindowsDeviceConditions(), UserBusy, LoggerFactory.CreateLogger("JackiVpn.Background"));
        SubscriptionService.Background = Background;
        Updates.WaitForUpkeep = ct => Background.WaitUntilAllowedAsync(Application.Background.BackgroundWork.Upkeep, ct);
        Orchestrator = new ConnectionOrchestrator(new OrchestratorDependencies
        {
            Background = Background,
            // The notch shows the live ping while the window is hidden.
            PingWhileHidden = () => Settings.Current.Ui.Notch is { Enabled: true, ShowPing: true },
            NetworkKey = networkKey,
            BestDns = () => DnsFinder.BestForNetwork(),
            Traffic = () => Traffic?.CurrentPolicy(),
            Limits = () => Traffic?.CurrentLimits() ?? Domain.Usage.TrafficLimits.None,
            Rescue = Rescue,
            Settings = settings,
            Profiles = Profiles,
            Subscriptions = Subscriptions,
            History = ConnectionHistory,
            Health = Health,
            RuleSets = RuleSets,
            Tuner = Tuner,
            Insights = Insights,
            Notifications = Notifications,
            LiveProbe = LiveProbe,
            Disconnects = Disconnects,
            NetworkMonitor = NetworkMonitor,
            Logger = LoggerFactory.CreateLogger("JackiVpn.Connection"),
            // The sandbox never uses TUN (it would change this PC's network): it runs the proxy backend instead.
            BackendFactory = mode => mode == ConnectionMode.Tun && !Sandboxed
                ? new ServiceTunnelBackend(Service.ConnectNewClientAsync, sessionLogger, SystemProxy)
                : new LocalProxyBackend(
                    plans,
                    SingBox,
                    xray,
                    () => new SupervisorRunner(new CoreSupervisor(Catalog, SingBox, xray, Paths.UserCoreWorkDir, sessionLogger, UserJournal)),
                    SystemProxy,
                    () => Sandboxed ? null : Service.ConnectedClient,
                    sessionLogger),
        });

        UsageBook = new Application.Usage.UsageBook(System.IO.Path.Combine(Paths.UserStateDir, "usage.json"));
        Usage = new Application.Usage.UsageMonitor(UsageBook, new Application.Usage.DotNetNicCounters(), Orchestrator.GetConnectionsAsync, settings, Notifications, LoggerFactory.CreateLogger("JackiVpn.Usage"))
        {
            WorkplaceDailyCapMb = () => Workplace.Active?.Rules.DailyCapMb ?? 0,
            InternalDomains = () => Workplace.Active?.InternalDomains ?? [],
            // Every 2 s on screen; every 8 s hidden, 12 s on power saver (totals stay exact, only the live view is coarser).
            Pace = Background.Stretch,
        };
        Traffic = new Application.Traffic.TrafficController(Orchestrator, settings, Workplace, Usage, LoggerFactory.CreateLogger("JackiVpn.Traffic"));
        RoutingWatcher = new Application.Routing.RoutingWatcher(Settings, Orchestrator, LoggerFactory.CreateLogger("JackiVpn.Routing"));
    }

    public Application.Connectivity.DnsFinder DnsFinder { get; }

    /// <summary>When the app's own work may run: power saver, metered network, hidden window, the user's own traffic.</summary>
    public Application.Background.BackgroundPolicy Background { get; }

    /// <summary>Download rate (bytes/s) above which the user's own traffic needs the line (device-wide adapters).</summary>
    public const long DeviceBusyBytesPerSecond = 512 * 1024;

    /// <summary>A game or stream is on, or the user's traffic is heavy: maintenance waits.</summary>
    private bool UserBusy()
    {
        var status = Orchestrator.Status;
        if (status.State == ConnectionState.Connected
            && (Domain.Connection.ActivityRules.IsQuiet(status.Activity) || (status.Traffic?.DownloadBytesPerSecond ?? 0) > ConnectionOrchestrator.BusyBytesPerSecond))
        {
            return true;
        }

        return Usage.DownPerSecond > DeviceBusyBytesPerSecond;
    }

    /// <summary>Background DNS search for a network without a fresh result (not while the tunnel carries our own traffic).</summary>
    public void SearchDnsIfNeeded()
    {
        if (DnsFinder.NeedsSearch() && Rescue.CanProbeDirectly())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), Lifetime).ConfigureAwait(false);
                    await Background.WaitUntilAllowedAsync(Application.Background.BackgroundWork.Upkeep, Lifetime).ConfigureAwait(false);
                    await DnsFinder.SearchAsync(Lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    public Application.Workplace.WorkplaceService Workplace { get; }
    public Application.Connectivity.EndpointRescue Rescue { get; }
    public Application.Usage.UsageBook UsageBook { get; }
    public Application.Usage.UsageMonitor Usage { get; }
    public Application.Traffic.TrafficController Traffic { get; }

    /// <summary>Applies routing changes to the running connection.</summary>
    public Application.Routing.RoutingWatcher RoutingWatcher { get; }

    public AppPaths Paths { get; }
    public LogBuffer LogBuffer { get; }
    public LoggingOptions LoggingOptions { get; }
    public ILoggerFactory LoggerFactory { get; }
    public ILogger Logger { get; }
    public JackiDatabase Database { get; }
    public SqliteProfileRepository Profiles { get; }
    public SqliteSubscriptionRepository Subscriptions { get; }
    public SqliteHealthHistoryRepository HealthHistory { get; }
    public SqliteConnectionHistoryRepository ConnectionHistory { get; }
    public SettingsStore Settings { get; }
    public CoreBinaryCatalog Catalog { get; }
    public SingBoxCoreEngine SingBox { get; }
    public XrayCoreEngine Xray { get; }
    public NetworkStateJournal UserJournal { get; }

    /// <summary>
    /// Developer sandbox (Debug builds with a separate data root and JACKIVPN_SANDBOX=1): the Windows proxy is an
    /// in-memory stand-in and TUN is refused, so a test copy can connect and cancel without touching this PC's network.
    /// </summary>
    public static bool Sandboxed { get; } =
#if DEBUG
        Environment.GetEnvironmentVariable("JACKIVPN_SANDBOX") == "1" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JACKIVPN_DATA_ROOT"));
#else
        false;
#endif

    public IWinInetProxyApi WinInet { get; }
    public SystemProxyManager SystemProxy { get; }
    public RecoveryCoordinator UserRecovery { get; }
    public NotificationService Notifications { get; }
    public DisconnectTracker Disconnects { get; }
    public HealthCheckService Health { get; }
    public InsightStore Insights { get; }
    public ConnectionTuner Tuner { get; }
    public HttpLiveProbe LiveProbe { get; }
    public SpeedTestService SpeedTest { get; }
    public ConfigParserPipeline Parser { get; }
    public SubscriptionService SubscriptionService { get; }
    public ProfileService ProfileService { get; }
    public RuleSetManager RuleSets { get; }
    public ServiceControl Service { get; }

    /// <summary>Local profiles: each person's own (encrypted) settings.</summary>
    public Application.Accounts.LocalAccountService Accounts { get; }

    /// <summary>DNS servers of the network adapters (changed through the service).</summary>
    public Application.Connectivity.AdapterDnsService AdapterDns { get; }
    public NetworkChangeMonitor NetworkMonitor { get; }
    public ConnectionOrchestrator Orchestrator { get; }
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>App updates (signed feed); checks periodically, installs only when the user says so.</summary>
    public UpdateMonitor Updates { get; }

    public static Version CurrentVersion { get; } = Version.TryParse(
        (typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0], out var v) ? v : new Version(0, 0, 0);

    /// <summary>Background work that starts after the window is up.</summary>
    public void StartBackground()
    {
        NetworkMonitor.Start();
        Orchestrator.NetworkChanged += (_, _) => _ = SubscriptionService.RefreshAfterNetworkChangeAsync(_lifetime.Token);
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, _) => Background.Refresh();
        NetworkMonitor.Changed += (_, _) =>
        {
            Background.Refresh();
            _ = Task.Run(Workplace.Refresh);
            // Network key is known after the workplace refresh; give it a moment, then look for the best DNS here.
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => SearchDnsIfNeeded(), TaskScheduler.Default);
        };
        _ = Task.Run(Workplace.Refresh);
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => SearchDnsIfNeeded(), TaskScheduler.Default);
        Usage.Start();

        _ = Task.Run(() => Updates.RunAsync(_lifetime.Token));
        _ = Task.Run(async () =>
        {
            try
            {
                await SubscriptionService.RunAccountMonitorAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await SubscriptionService.RunAutoUpdateLoopAsync(TimeSpan.FromMinutes(1), _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await HealthHistory.PruneAsync(DateTimeOffset.UtcNow.AddDays(-14), _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.LogDebug("History prune failed: {Error}", ex.Message);
            }
        });
    }

    public void ApplyLogLevel(AppLogLevel level) => _logProvider.MinimumLevel = level switch
    {
        AppLogLevel.Error => LogLevel.Error,
        AppLogLevel.Warning => LogLevel.Warning,
        AppLogLevel.Debug => LogLevel.Debug,
        AppLogLevel.Trace => LogLevel.Trace,
        _ => LogLevel.Information,
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Accounts.SaveActiveAsync(CancellationToken.None).ConfigureAwait(false);
            await Orchestrator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await Usage.DisposeAsync().ConfigureAwait(false);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            NetworkMonitor.Dispose();
            await Service.DisposeAsync().ConfigureAwait(false);
            await Settings.FlushAsync().ConfigureAwait(false);
            _fetcher.Dispose();
            LiveProbe.Dispose();
            LoggerFactory.Dispose();
            _logProvider.Dispose();
            _lifetime.Dispose();
        }
    }
}
