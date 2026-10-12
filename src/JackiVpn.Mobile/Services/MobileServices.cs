using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Notifications;
using JackiVpn.Application.Profiles;
using JackiVpn.Application.Routing;
using JackiVpn.Application.Sessions;
using JackiVpn.Application.Settings;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Settings;
using JackiVpn.Infrastructure.Logging;
using JackiVpn.Infrastructure.Persistence;
using JackiVpn.Mobile.Platforms.Android;
using JackiVpn.Routing;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.Services;

/// <summary>
/// Composition root of the Android app. The same application core as Windows (parsing, PasarGuard, smart
/// selection, handshake tuning, orchestrator); only the platform edges differ: Android Keystore for secrets,
/// VpnService + tun2socks for capture, and the core executable shipped inside the APK.
/// </summary>
public sealed class MobileServices : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RedactingFileLoggerProvider _logProvider;
    private readonly SafeHttpFetcher _fetcher;
    private AndroidNetworkMonitor? _network;

    public MobileServices(global::Android.Content.Context context)
    {
        Instance = this;
        Context = context;
        DataDir = FileSystem.AppDataDirectory;
        LogsDir = Path.Combine(DataDir, "logs");
        foreach (var dir in new[] { LogsDir, Path.Combine(DataDir, "state"), Path.Combine(DataDir, "rulesets"), Path.Combine(DataDir, "run") })
        {
            Directory.CreateDirectory(dir);
        }

        LogBuffer = new LogBuffer(3000);
        _logProvider = new RedactingFileLoggerProvider(new LoggingOptions { Directory = LogsDir, FilePrefix = "android" }, LogBuffer);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_logProvider).AddProvider(new LogcatLoggerProvider()));
        Logger = LoggerFactory.CreateLogger("JackiVpn.Android");

        Database = new JackiDatabase(Path.Combine(DataDir, "jackivpn.db"));
        var protector = new AndroidKeystoreSecretProtector();
        Profiles = new SqliteProfileRepository(Database, protector);
        Subscriptions = new SqliteSubscriptionRepository(Database, protector);
        HealthHistory = new SqliteHealthHistoryRepository(Database);
        ConnectionHistory = new SqliteConnectionHistoryRepository(Database);
        Settings = new SettingsStore(new SqliteSettingsRepository(Database), LoggerFactory.CreateLogger("JackiVpn.Settings"));
        Func<AppSettings> settings = () => Settings.Current;

        Catalog = AndroidCores.CreateCatalog(context, DataDir);
        SingBox = new SingBoxCoreEngine(Catalog, LoggerFactory.CreateLogger<SingBoxCoreEngine>());
        Xray = new XrayCoreEngine(Catalog, LoggerFactory.CreateLogger<XrayCoreEngine>());
        var workDir = Path.Combine(DataDir, "run");

        Notifications = new NotificationService(new AndroidNotificationSink(context), () => Settings.Current.Notifications, LoggerFactory.CreateLogger("JackiVpn.Notifications"));
        Disconnects = new DisconnectTracker();
        Insights = new InsightStore(Path.Combine(DataDir, "state", "insights.json"));
        Func<string> networkKey = () => AndroidNetworkKey.Current(context);
        Tuner = new ConnectionTuner(Catalog, SingBox, Xray, HealthHistory, Insights, settings, workDir, LoggerFactory.CreateLogger("JackiVpn.Tuning")) { NetworkKey = networkKey };
        Health = new HealthCheckService(Catalog, SingBox, Xray, HealthHistory, settings, Disconnects.Recent, workDir, LoggerFactory.CreateLogger("JackiVpn.Health"))
        {
            PrepareProfile = Tuner.PrepareForProbe,
        };
        Workplace = new Application.Workplace.WorkplaceService(new AndroidNetworkFacts(context), settings, LoggerFactory.CreateLogger("JackiVpn.Workplace"));
        // The app's own sockets are excluded from the VPN on Android, so direct probing works while connected too.
        Rescue = new Application.Connectivity.EndpointRescue(Tuner, Insights, settings, networkKey, LoggerFactory.CreateLogger("JackiVpn.Rescue"));
        DnsFinder = new Application.Connectivity.DnsFinder(Path.Combine(DataDir, "state", "dns-choice.json"), networkKey, LoggerFactory.CreateLogger("JackiVpn.Dns"))
        {
            Extra = () => Settings.Current.Dns.Custom.Select(Application.Connectivity.DnsCandidate.From),
        };
        LiveProbe = new HttpLiveProbe();
        SpeedTest = new SpeedTestService();

        Parser = new ConfigParserPipeline();
        _fetcher = new SafeHttpFetcher();
        SubscriptionService = new SubscriptionService(Subscriptions, Profiles, () => _fetcher, Parser, settings, LoggerFactory.CreateLogger("JackiVpn.Subscriptions"), Notifications,
            device: () => Settings.Current.Device.Hwid is { } hwid
                ? new Domain.Subscriptions.DeviceIdentity(hwid, "Android", global::Android.OS.Build.VERSION.Release ?? string.Empty, PhoneModel)
                : null);
        ProfileService = new ProfileService(Profiles, Parser);
        RuleSets = new RuleSetManager(Path.Combine(DataDir, "rulesets"), new RuleSetDownloader(() => _fetcher), LoggerFactory.CreateLogger("JackiVpn.RuleSets"))
        {
            SeedDirectory = Path.Combine(DataDir, "rulesets-seed"),
        };

        var plans = new SessionPlanFactory(Catalog);
        var sessionLogger = LoggerFactory.CreateLogger("JackiVpn.Session");
        Background = new Application.Background.BackgroundPolicy(new AndroidDeviceConditions(context), UserBusy, LoggerFactory.CreateLogger("JackiVpn.Background"));
        SubscriptionService.Background = Background;
        Orchestrator = new ConnectionOrchestrator(new OrchestratorDependencies
        {
            Background = Background,
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
            Logger = LoggerFactory.CreateLogger("JackiVpn.Connection"),
            NetworkKey = networkKey,
            Traffic = () => Traffic?.CurrentPolicy(),
            Limits = () => Traffic?.CurrentLimits() ?? Domain.Usage.TrafficLimits.None,
            Rescue = Rescue,
            BestDns = () => DnsFinder.BestForNetwork(),
            // Tun = VPN (all or selected apps); SystemProxy = proxy-only mode (no VPN).
            BackendFactory = mode => new AndroidVpnBackend(
                plans,
                SingBox,
                Xray,
                () => new SupervisorRunner(new CoreSupervisor(Catalog, SingBox, Xray, workDir, sessionLogger)),
                context,
                () => IsBackground,
                sessionLogger,
                proxyOnly: mode == Domain.Connection.ConnectionMode.SystemProxy),
        });
        JackiVpnService.Revoked += (_, _) => _ = Orchestrator.DisconnectAsync();
        Orchestrator.NetworkChanged += (_, _) =>
        {
            _ = SubscriptionService.RefreshAfterNetworkChangeAsync(Lifetime);
            _ = Task.Run(Workplace.Refresh);
            SearchDnsIfNeeded();
        };

        UsageBook = new Application.Usage.UsageBook(Path.Combine(DataDir, "state", "usage.json"));
        Application.Usage.UsageMonitor? usage = null;
        // Android counts tunnelled bytes twice (tun0 and the real network): the core's own totals are taken off.
        var nics = new AndroidNicCounters(() => usage is null ? (0, 0) : (usage.CoreDownTotal, usage.CoreUpTotal));
        usage = new Application.Usage.UsageMonitor(UsageBook, nics, Orchestrator.GetConnectionsAsync, settings, Notifications, LoggerFactory.CreateLogger("JackiVpn.Usage"))
        {
            Interval = TimeSpan.FromSeconds(3),
            Pace = i => Background.PowerSaver ? i * 2 : i,
            Persian = () => Localization.L.IsPersian,
            WorkplaceDailyCapMb = () => Workplace.Active?.Rules.DailyCapMb ?? 0,
            InternalDomains = () => Workplace.Active?.InternalDomains ?? [],
        };
        Usage = usage;
        // No per-app process names on Android (one VPN process carries every app): domain and class rules only.
        Traffic = new Application.Traffic.TrafficController(Orchestrator, settings, Workplace, Usage, LoggerFactory.CreateLogger("JackiVpn.Traffic")) { ProcessAware = false };
        RoutingWatcher = new Application.Routing.RoutingWatcher(Settings, Orchestrator, LoggerFactory.CreateLogger("JackiVpn.Routing"));
        var updateLog = LoggerFactory.CreateLogger("JackiVpn.Updates");
        Updates = new Infrastructure.Updates.UpdateMonitor(new Infrastructure.Updates.AppUpdateService(updateLog), () => Settings.Current.Updates,
            Version.TryParse(AppInfo.Current.VersionString, out var v) ? v : new Version(0, 0, 0), "android", CurrentAbi, updateLog)
        {
            WaitForUpkeep = ct => Background.WaitUntilAllowedAsync(Application.Background.BackgroundWork.Upkeep, ct),
        };
        Activity = new ActivityMonitor(this);
        Orchestrator.StatusChanged += (_, st) => StatusSurfaces.Update(context, st);
        Orchestrator.StatusChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => _island?.Update(Orchestrator.Status, IslandMode()));
        Traffic.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() => _island?.Update(Orchestrator.Status, IslandMode()));
        Settings.Changed += (_, _) => SyncIsland();
    }

    /// <summary>The APK flavour this process runs (the update feed has one APK per ABI).</summary>
    public static string CurrentAbi => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64-v8a",
        System.Runtime.InteropServices.Architecture.Arm => "armeabi-v7a",
        _ => "x86_64",
    };

    public Infrastructure.Updates.UpdateMonitor Updates { get; }

    public ActivityMonitor Activity { get; }

    public Application.Workplace.WorkplaceService Workplace { get; }
    public Application.Connectivity.EndpointRescue Rescue { get; }
    public Application.Connectivity.DnsFinder DnsFinder { get; }

    /// <summary>Looks for the best DNS of a network not searched in the last day (the app itself is outside the VPN).</summary>
    public void SearchDnsIfNeeded()
    {
        if (DnsFinder.NeedsSearch())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), Lifetime).ConfigureAwait(false);
                    await DnsFinder.SearchAsync(Lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }
    public Application.Usage.UsageBook UsageBook { get; }
    public Application.Usage.UsageMonitor Usage { get; }
    public Application.Traffic.TrafficController Traffic { get; }

    /// <summary>Applies routing changes to the running connection.</summary>
    public Application.Routing.RoutingWatcher RoutingWatcher { get; }

    /// <summary>"Samsung SM-S911B" — shown to the user's own panel and devices, never the device's user name.</summary>
    public static string PhoneModel => global::Android.OS.Build.Manufacturer + " " + global::Android.OS.Build.Model;

    /// <summary>The app is in the background or the screen is off (battery saving).</summary>
    public bool IsBackground { get; private set; }

    /// <summary>When the app's own work may run: power saver, metered network, background, the user's traffic.</summary>
    public Application.Background.BackgroundPolicy Background { get; }

    /// <summary>A game or stream is on, or the tunnel is busy carrying the user's traffic: maintenance waits.</summary>
    private bool UserBusy()
    {
        var s = Orchestrator.Status;
        return s.State == Domain.Connection.ConnectionState.Connected
            && (Domain.Connection.ActivityRules.IsQuiet(s.Activity) || (s.Traffic?.DownloadBytesPerSecond ?? 0) > ConnectionOrchestrator.BusyBytesPerSecond);
    }

    public void SetForeground(bool foreground)
    {
        IsBackground = !foreground;
        Background.Hidden = !foreground;
        Background.Refresh();
        Orchestrator.SetBackground(!foreground);
        // Battery: usage is sampled every 15 s and orbs stop drawing while nobody looks.
        Usage.Interval = TimeSpan.FromSeconds(foreground ? 3 : 15);
        Controls.OrbView.AppVisible = foreground;
        SyncIsland();
    }

    private IslandOverlay? _island;

    /// <summary>The island settings page is open: show the island even inside IBOX, to place it live.</summary>
    public bool IslandPreview { get; set; }

    /// <summary>The dynamic island follows its setting, the permission and (optionally) whether IBOX itself is open.</summary>
    public void SyncIsland()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var s = Settings.Current.Ui.Island;
            var want = s.Enabled && IslandOverlay.CanShow(Context) && (!s.HideInApp || IsBackground || IslandPreview);
            _island ??= CreateIsland();
            if (want && !_island.IsShowing)
            {
                _island.Show(s);
                _island.Update(Orchestrator.Status, IslandMode());
            }
            else if (!want && _island.IsShowing)
            {
                _island.Hide();
            }
            else if (want)
            {
                _island.Apply(s);
            }
        });
    }

    private Domain.Visuals.VisualMode IslandMode() =>
        Orchestrator.Status.State == Domain.Connection.ConnectionState.Connected ? Domain.Visuals.ModePalette.Of(Orchestrator.Status.Activity, Traffic.Mode) : Domain.Visuals.VisualMode.Normal;

    private IslandOverlay CreateIsland()
    {
        var island = new IslandOverlay(Context);
        island.OpenRequested += (_, _) =>
        {
            var launch = Context.PackageManager?.GetLaunchIntentForPackage(Context.PackageName!);
            if (launch is not null)
            {
                launch.AddFlags(global::Android.Content.ActivityFlags.NewTask | global::Android.Content.ActivityFlags.ReorderToFront);
                Context.StartActivity(launch);
            }
        };
        island.Moved += (_, p) => Settings.Update(s => s with { Ui = s.Ui with { Island = s.Ui.Island with { X = p.X, Y = p.Y } } });
        return island;
    }

    /// <summary>The services, also from components Android starts on its own (widget, tile, always-on VPN).</summary>
    public static MobileServices? Resolve() =>
        Instance ?? IPlatformApplication.Current?.Services.GetService(typeof(MobileServices)) as MobileServices;

    /// <summary>For components Android creates itself (the VpnService started by Always-on VPN).</summary>
    public static MobileServices? Instance { get; private set; }

    public global::Android.Content.Context Context { get; }
    public string DataDir { get; }
    public string LogsDir { get; }
    public LogBuffer LogBuffer { get; }
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
    public NotificationService Notifications { get; }
    public DisconnectTracker Disconnects { get; }
    public InsightStore Insights { get; }
    public ConnectionTuner Tuner { get; }
    public HealthCheckService Health { get; }
    public HttpLiveProbe LiveProbe { get; }
    public SpeedTestService SpeedTest { get; }
    public ConfigParserPipeline Parser { get; }
    public SubscriptionService SubscriptionService { get; }
    public ProfileService ProfileService { get; }
    public RuleSetManager RuleSets { get; }
    public ConnectionOrchestrator Orchestrator { get; }
    public CancellationToken Lifetime => _lifetime.Token;

    private int _initialized;

    /// <summary>Loads settings and history; safe to call more than once.</summary>
    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        await Settings.LoadAsync(Lifetime).ConfigureAwait(false);
        await CopySeedRuleSetsAsync().ConfigureAwait(false);
        if (Settings.Current.Routing is { Rules.Count: 0, Preset: Domain.Routing.RoutingPreset.Rule or Domain.Routing.RoutingPreset.IranFriendly } routing)
        {
            var preset = RoutingPresets.Build(routing.Preset);
            Settings.Update(s => s with { Routing = s.Routing with { Rules = preset.Rules, RuleSets = preset.RuleSets, Final = preset.Final } });
        }

        await Health.LoadAsync(Lifetime).ConfigureAwait(false);
        _network = new AndroidNetworkMonitor(Context, Orchestrator);
        _network.Start();
        _ = Task.Run(Workplace.Refresh);
        Usage.Start();
        SyncIsland();
        SearchDnsIfNeeded();
        _ = Task.Run(async () =>
        {
            try
            {
                await SubscriptionService.RunAutoUpdateLoopAsync(TimeSpan.FromMinutes(1), Lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await SubscriptionService.RunAccountMonitorAsync(Lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
        _ = Task.Run(() => Updates.RunAsync(Lifetime));
    }

    /// <summary>Rule-sets shipped in the APK are copied out once so the first connection never waits for a download.</summary>
    private async Task CopySeedRuleSetsAsync()
    {
        var dir = Path.Combine(DataDir, "rulesets-seed");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "geoip-ir.srs", "geosite-category-ir.srs", "geosite-category-ads-all.srs" })
        {
            var target = Path.Combine(dir, name);
            try
            {
                await using var source = await FileSystem.OpenAppPackageFileAsync("rulesets/" + name).ConfigureAwait(false);
                await using var output = File.Create(target);
                await source.CopyToAsync(output).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                Logger.LogDebug("Shipped rule-set {Name} not copied: {Error}", name, ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Usage.DisposeAsync().ConfigureAwait(false);
        await Orchestrator.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Settings.FlushAsync().ConfigureAwait(false);
        _fetcher.Dispose();
        LiveProbe.Dispose();
        LoggerFactory.Dispose();
        _logProvider.Dispose();
        _lifetime.Dispose();
    }
}
