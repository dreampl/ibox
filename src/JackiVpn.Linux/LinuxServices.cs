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
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Infrastructure.Logging;
using JackiVpn.Infrastructure.Persistence;
using JackiVpn.Routing;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Linux;

/// <summary>Composition root shared by the command line and the desktop window (same core as Windows/Android).</summary>
public sealed class LinuxServices : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;

    public LinuxServices(INotificationSink sink, Func<bool> setDesktopProxy)
    {
        // Under sudo, keep using the invoking user's data (handed back to them in DisposeAsync).
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        RunningUnderSudo = sudoUser is { Length: > 0 } && Environment.UserName == "root";
        var home = RunningUnderSudo ? Path.Combine("/home", sudoUser!) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DataDir = Path.Combine(home, ".local", "share", "jackivpn");
        foreach (var dir in new[] { DataDir, LogsDir, Path.Combine(DataDir, "state"), Path.Combine(DataDir, "rulesets"), Path.Combine(DataDir, "run") })
        {
            Directory.CreateDirectory(dir);
        }

        LogBuffer = new LogBuffer(3000);
        var logProvider = new RedactingFileLoggerProvider(new LoggingOptions { Directory = LogsDir, FilePrefix = "linux" }, LogBuffer);
        _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logProvider));
        Logger = _loggerFactory.CreateLogger("JackiVpn.Linux");

        var database = new JackiDatabase(Path.Combine(DataDir, "jackivpn.db"));
        var protector = new KeyFileSecretProtector(Path.Combine(DataDir, "secret.key"));
        Profiles = new SqliteProfileRepository(database, protector);
        Subscriptions = new SqliteSubscriptionRepository(database, protector);
        var healthHistory = new SqliteHealthHistoryRepository(database);
        Settings = new SettingsStore(new SqliteSettingsRepository(database), _loggerFactory.CreateLogger("JackiVpn.Settings"));
        Func<AppSettings> settings = () => Settings.Current;
        Accounts = new Application.Accounts.LocalAccountService(Path.Combine(DataDir, "profiles"), protector, Settings, _loggerFactory.CreateLogger("JackiVpn.Profiles"));

        var coresDir = Path.Combine(AppContext.BaseDirectory, "cores");
        var catalog = new CoreBinaryCatalog(coresDir, Path.Combine(coresDir, "manifest.json"));
        var singBox = new SingBoxCoreEngine(catalog, _loggerFactory.CreateLogger<SingBoxCoreEngine>());
        var xray = new XrayCoreEngine(catalog, _loggerFactory.CreateLogger<XrayCoreEngine>());
        var workDir = Path.Combine(DataDir, "run");
        var notifications = new NotificationService(sink, () => Settings.Current.Notifications, _loggerFactory.CreateLogger("JackiVpn.Notifications"));
        var disconnects = new DisconnectTracker();
        var insights = Insights = new InsightStore(Path.Combine(DataDir, "state", "insights.json"));
        GnomeProxy.MarkerPath = Path.Combine(DataDir, "state", "desktop-proxy.mode");
        Workplace = new Application.Workplace.WorkplaceService(new LinuxNetworkFacts(), settings, _loggerFactory.CreateLogger("JackiVpn.Workplace"));
        Func<string> networkKey = () => Workplace.Facts.Key is { Length: > 0 } key ? key : "default";
        var tuner = new ConnectionTuner(catalog, singBox, xray, healthHistory, insights, settings, workDir, _loggerFactory.CreateLogger("JackiVpn.Tuning")) { NetworkKey = networkKey };
        Health = new HealthCheckService(catalog, singBox, xray, healthHistory, settings, disconnects.Recent, workDir, _loggerFactory.CreateLogger("JackiVpn.Health"))
        {
            PrepareProfile = tuner.PrepareForProbe,
        };
        var fetcher = new SafeHttpFetcher();
        var parser = new ConfigParserPipeline();
        SubscriptionService = new SubscriptionService(Subscriptions, Profiles, () => fetcher, parser, settings, _loggerFactory.CreateLogger("JackiVpn.Subscriptions"), notifications);
        ProfileService = new ProfileService(Profiles, parser);

        var ruleSets = new RuleSetManager(Path.Combine(DataDir, "rulesets"), new RuleSetDownloader(() => fetcher), _loggerFactory.CreateLogger("JackiVpn.RuleSets"))
        {
            SeedDirectory = Path.Combine(AppContext.BaseDirectory, "rulesets-seed"),
        };
        var plans = new SessionPlanFactory(catalog);
        var sessionLogger = _loggerFactory.CreateLogger("JackiVpn.Session");
        Rescue = new Application.Connectivity.EndpointRescue(tuner, insights, settings, networkKey, _loggerFactory.CreateLogger("JackiVpn.Rescue"))
        {
            // In TUN mode while connected the app's own sockets use the tunnel: probing then would measure the tunnel.
            CanProbeDirectly = () => Orchestrator!.State != Domain.Connection.ConnectionState.Connected || Settings.Current.Connection.Mode != Domain.Connection.ConnectionMode.Tun,
        };
        DnsFinder = new Application.Connectivity.DnsFinder(Path.Combine(DataDir, "state", "dns-choice.json"), networkKey, _loggerFactory.CreateLogger("JackiVpn.Dns"))
        {
            Extra = () => Settings.Current.Dns.Custom.Select(Application.Connectivity.DnsCandidate.From),
        };
        AdapterDns = new Application.Connectivity.AdapterDnsService(new LinuxAdapterDns(), Path.Combine(DataDir, "state", "adapter-dns-original.json"), _loggerFactory.CreateLogger("JackiVpn.AdapterDns"));
        Orchestrator = new ConnectionOrchestrator(new OrchestratorDependencies
        {
            NetworkKey = networkKey,
            BestDns = () => DnsFinder.BestForNetwork(),
            Traffic = () => Traffic?.CurrentPolicy(),
            Limits = () => Traffic?.CurrentLimits() ?? Domain.Usage.TrafficLimits.None,
            Rescue = Rescue,
            Settings = settings,
            Profiles = Profiles,
            Subscriptions = Subscriptions,
            History = new SqliteConnectionHistoryRepository(database),
            Health = Health,
            RuleSets = ruleSets,
            Tuner = tuner,
            Insights = insights,
            Notifications = notifications,
            LiveProbe = new HttpLiveProbe(),
            Disconnects = disconnects,
            Logger = _loggerFactory.CreateLogger("JackiVpn.Connection"),
            BackendFactory = mode => new LinuxBackend(plans, singBox, xray,
                () => new SupervisorRunner(new CoreSupervisor(catalog, singBox, xray, workDir, sessionLogger)),
                mode, setDesktopProxy(), sessionLogger),
        });

        UsageBook = new Application.Usage.UsageBook(Path.Combine(DataDir, "state", "usage.json"));
        Usage = new Application.Usage.UsageMonitor(UsageBook, new Application.Usage.DotNetNicCounters(), Orchestrator.GetConnectionsAsync, settings, notifications, _loggerFactory.CreateLogger("JackiVpn.Usage"))
        {
            WorkplaceDailyCapMb = () => Workplace.Active?.Rules.DailyCapMb ?? 0,
            InternalDomains = () => Workplace.Active?.InternalDomains ?? [],
        };
        Traffic = new Application.Traffic.TrafficController(Orchestrator, settings, Workplace, Usage, _loggerFactory.CreateLogger("JackiVpn.Traffic"));
        RoutingWatcher = new Application.Routing.RoutingWatcher(Settings, Orchestrator, _loggerFactory.CreateLogger("JackiVpn.Routing"));
        Agent = new Application.Agent.AgentPipeServer(
            new Application.Agent.AgentController(new Application.Agent.AgentDependencies
            {
                Settings = Settings,
                Orchestrator = Orchestrator,
                Profiles = Profiles,
                Health = Health.Snapshot,
                CountryOf = fp => Insights.Get(fp)?.Exit?.CountryCode,
                DnsFinder = DnsFinder,
                SpeedTest = SpeedTest,
                Logger = _loggerFactory.CreateLogger("JackiVpn.Agent"),
            }),
            () => Settings.Current.Agent,
            _loggerFactory.CreateLogger("JackiVpn.Agent"));
        RuleSets = ruleSets;
    }

    public Application.Workplace.WorkplaceService Workplace { get; }

    public Application.Connectivity.EndpointRescue Rescue { get; }

    public Application.Usage.UsageBook UsageBook { get; }

    public Application.Usage.UsageMonitor Usage { get; }

    public Application.Traffic.TrafficController Traffic { get; }

    /// <summary>Finds the fastest clean resolver of each network (smart DNS).</summary>
    public Application.Connectivity.DnsFinder DnsFinder { get; }

    /// <summary>DNS servers of the network adapters (NetworkManager).</summary>
    public Application.Connectivity.AdapterDnsService AdapterDns { get; }

    /// <summary>Applies routing changes to the running connection.</summary>
    public Application.Routing.RoutingWatcher RoutingWatcher { get; }

    /// <summary>Control by the command line and AI agents (MCP), when the user turns it on (desktop window only).</summary>
    public Application.Agent.AgentPipeServer Agent { get; }

    public RuleSetManager RuleSets { get; }

    public string DataDir { get; }

    public string LogsDir => Path.Combine(DataDir, "logs");

    public bool RunningUnderSudo { get; }

    public ILogger Logger { get; }

    public LogBuffer LogBuffer { get; }

    public SpeedTestService SpeedTest { get; } = new();

    /// <summary>What the desktop's media players are playing (MPRIS), for the notch.</summary>
    public Application.Media.INowPlayingSource NowPlaying { get; } = new MprisNowPlaying();

    /// <summary>What IBOX learned per server (last exit country, working fixes).</summary>
    public InsightStore Insights { get; }

    public SqliteProfileRepository Profiles { get; }

    public SqliteSubscriptionRepository Subscriptions { get; }

    public SettingsStore Settings { get; }

    /// <summary>Local profiles: each person's own (encrypted) settings.</summary>
    public Application.Accounts.LocalAccountService Accounts { get; }

    public HealthCheckService Health { get; }

    public SubscriptionService SubscriptionService { get; }

    public ProfileService ProfileService { get; }

    public ConnectionOrchestrator Orchestrator { get; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await Settings.LoadAsync(ct).ConfigureAwait(false);
        await Accounts.InitializeAsync(ct).ConfigureAwait(false);
        if (Settings.Current.Routing is { Rules.Count: 0, Preset: Domain.Routing.RoutingPreset.Rule or Domain.Routing.RoutingPreset.IranFriendly } routing)
        {
            var preset = RoutingPresets.Build(routing.Preset);
            Settings.Update(s => s with { Routing = s.Routing with { Rules = preset.Rules, RuleSets = preset.RuleSets, Final = preset.Final } });
        }

        await Health.LoadAsync(ct).ConfigureAwait(false);
        GnomeProxy.RecoverStale(Logger);
        _ = Task.Run(Workplace.Refresh, CancellationToken.None);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => _ = Task.Run(Workplace.Refresh, CancellationToken.None);
        Usage.Start();
    }

    /// <summary>Background DNS search for a network without a fresh result (desktop window only, never through the tunnel).</summary>
    public void SearchDnsIfNeeded()
    {
        if (DnsFinder.NeedsSearch() && Rescue.CanProbeDirectly())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                    await DnsFinder.SearchAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Logger.LogDebug("Background DNS search ended: {Error}", ex.Message);
                }
            });
        }
    }

    /// <summary>Servers ordered by subscription (manual imports last), as the list shows them.</summary>
    public async Task<IReadOnlyList<ProxyProfile>> ServersAsync(CancellationToken ct) =>
        (await Profiles.GetAllAsync(ct).ConfigureAwait(false)).OrderBy(p => p.Origin.SubscriptionId ?? "~", StringComparer.Ordinal).ToList();

    public async Task<Dictionary<string, string>> SubscriptionNamesAsync(CancellationToken ct) =>
        (await Subscriptions.GetAllAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await Accounts.SaveActiveAsync(CancellationToken.None).ConfigureAwait(false);
        await Agent.DisposeAsync().ConfigureAwait(false);
        await Usage.DisposeAsync().ConfigureAwait(false);
        await Orchestrator.DisposeAsync().ConfigureAwait(false);
        _loggerFactory.Dispose();
        if (RunningUnderSudo && Environment.GetEnvironmentVariable("SUDO_UID") is { } uid && Environment.GetEnvironmentVariable("SUDO_GID") is { } gid)
        {
            using var chown = System.Diagnostics.Process.Start("chown", ["-R", $"{uid}:{gid}", DataDir]);
            chown?.WaitForExit(10000);
        }
    }
}
