using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using JackiVpn.Application.Connection;
using JackiVpn.Desktop.Composition;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Desktop.Views;
using JackiVpn.Domain.Settings;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JackiVpn.Desktop;

public partial class App : System.Windows.Application
{
    // One instance per data folder: a copy pointed at another folder (JACKIVPN_DATA_ROOT, used for testing) is
    // separate and never activates or blocks the installed app.
    private static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("JACKIVPN_DATA_ROOT") is { Length: > 0 } root
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12]
        : string.Empty;

    private static readonly string MutexName = @"Local\JackiVpn.Desktop.SingleInstance" + InstanceSuffix;
    private static readonly string ActivateEventName = @"Local\JackiVpn.Desktop.Activate" + InstanceSuffix;

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private AppServices? _services;
    private TrayService? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private ActivityDetector? _activity;
    private int _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ArgumentNullException.ThrowIfNull(e);

        // Emergency network restore: its own short-lived process, so it works even if the main window hangs.
        if (e.Args.Contains(EmergencyRestore.Argument, StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var lines = await EmergencyRestore.RunAsync();
            MessageBox.Show("Emergency network restore finished:\n\n• " + string.Join("\n• ", lines) + "\n\nOpen IBOX again to reconnect.",
                "IBOX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Single instance: a second launch just brings the running window to the front.
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
            {
                if (Volatile.Read(ref _exiting) != 0)
                {
                    return;
                }

                Dispatcher.BeginInvoke(ShowWindow);
            }
        })
        { IsBackground = true, Name = "Activation listener" }.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _services?.Logger.LogWarning("Unobserved task exception: {Error}", SecretRedactor.Redact(args.Exception.GetBaseException().Message));
            args.SetObserved();
        };

        ThemeManager.Apply(AppTheme.System);
        try
        {
            await StartAsync(e.Args.Contains(StartupManager.MinimizedArgument, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services?.Logger.LogCritical("Startup failed: {Error}", SecretRedactor.Redact(ex.ToString()));
            MessageBox.Show("IBOX could not start:\n\n" + SecretRedactor.Redact(ex.Message), "IBOX", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private async Task StartAsync(bool launchedMinimized)
    {
        _tray = new TrayService(() => _window is { IsVisible: true, IsActive: true } && _window.WindowState != WindowState.Minimized);
        _services = new AppServices(_tray);
        await _services.Settings.LoadAsync(CancellationToken.None);
        await _services.Accounts.InitializeAsync(CancellationToken.None);

        // First run (or settings from before presets were materialized): write the selected preset's rules
        // into the editable rule list. A user who removed every rule has Preset = Custom and is left alone.
        if (_services.Settings.Current.Routing is { Rules.Count: 0, Preset: Domain.Routing.RoutingPreset.Rule or Domain.Routing.RoutingPreset.IranFriendly } routing)
        {
            var preset = Routing.RoutingPresets.Build(routing.Preset);
            _services.Settings.Update(s => s with { Routing = s.Routing with { Rules = preset.Rules, RuleSets = preset.RuleSets, Final = preset.Final } });
        }

        var settings = _services.Settings.Current;
        Motion.Reduced = settings.Ui.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        ThemeManager.Apply(settings.Ui.Theme);
        _services.ApplyLogLevel(settings.Logging.MinimumLevel);
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        _services.Logger.LogInformation("{Product} {Version} starting (OS {Os}).", Domain.ProductInfo.DisplayName, version, Environment.OSVersion.VersionString);

        // Undo anything a previous, uncleanly terminated session left behind (system proxy, orphaned cores).
        var recovered = false;
        try
        {
            var report = await _services.UserRecovery.RecoverAsync(_services.Lifetime);
            recovered = report.WasDirty;
            foreach (var a in report.Actions)
            {
                _services.Logger.Log(a.Success ? LogLevel.Information : LogLevel.Error, "Startup recovery: {Step}: {Detail}", a.Step, a.Detail);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogError("Startup recovery failed: {Error}", SecretRedactor.Redact(ex.Message));
        }

        WheelScrolling.Install();
        _vm = new MainViewModel(_services, new DialogService());
        _window = new MainWindow(_vm) { CloseToTray = () => _services.Settings.Current.Ui.CloseToTray };
        MainWindow = _window;
        _window.Closed += (_, _) => _ = ExitAsync();
        _window.OnScreenChanged += (_, shown) => OnMainWindowOnScreen(shown);
        ThemeManager.Changed += (_, _) => _vm?.RefreshTheme();
        _services.Orchestrator.StatusChanged += (_, _) => Dispatcher.BeginInvoke(UpdateEfficiencyMode);
        OnMainWindowOnScreen(false); // until the window is shown (it may start in the tray)

        _tray.OpenRequested += (_, _) => ShowWindow();
        _tray.ToggleRequested += (_, _) => _vm.Dashboard.ToggleCommand.Execute(null);
        _tray.ExitRequested += (_, _) => _ = ExitAsync();
        _services.Orchestrator.StatusChanged += (_, s) => Dispatcher.BeginInvoke(() => _tray.Update(s));
        _services.Settings.Changed += (_, s) => Dispatcher.BeginInvoke(() =>
        {
            Motion.Reduced = s.Ui.ReduceMotion || !SystemParameters.ClientAreaAnimation;
            if (s.Ui.Theme != ThemeManager.Current)
            {
                ThemeManager.Apply(s.Ui.Theme);
            }
        });
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SessionEnding += OnSessionEnding;

        if (!(launchedMinimized || settings.Startup.StartMinimized))
        {
            _window.Show();
        }

        await _vm.InitializeAsync();
        _services.StartBackground();
        SetupNotch();
        _activity = new ActivityDetector(_services.Orchestrator, _services.LoggerFactory.CreateLogger("JackiVpn.Activity")) { GameChanged = _services.Traffic.ReportGame };
        _activity.Start();
        if (recovered)
        {
            _vm.ShowToast("Network restored", "Settings changed by an earlier session that did not exit cleanly were restored.", warning: true);
        }

        if (await TryAdoptServiceTunnelAsync())
        {
            _vm.ShowToast("Still connected", "The VPN kept running while the app was closed; it is shown and monitored again.");
            return;
        }

        if (settings.Startup.AutoConnect)
        {
            if (settings.Connection.Mode != Domain.Connection.ConnectionMode.Tun || await _services.Service.RefreshAsync(_services.Lifetime) == ServiceAvailability.Running)
            {
                await _services.Orchestrator.ConnectAsync(new ConnectRequest(), _services.Lifetime);
            }
        }
    }

    private bool _efficiencyMode;
    private CancellationTokenSource? _hiddenCts;

    /// <summary>
    /// The window left or came back to the screen: live polling (ping, usage, clocks) slows down or stops, optional
    /// work waits, and after a while hidden the memory the UI no longer needs is given back.
    /// </summary>
    private void OnMainWindowOnScreen(bool shown)
    {
        if (_services is null)
        {
            return;
        }

        _services.Background.Hidden = !shown;
        _services.Orchestrator.SetBackground(!shown);
        _vm?.SetOnScreen(shown);
        UpdateEfficiencyMode();
        _hiddenCts?.Cancel();
        _hiddenCts = null;
        if (!shown)
        {
            var cts = _hiddenCts = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromSeconds(15), cts.Token).ContinueWith(
                _ =>
                {
                    // One full compacting collection: pages, images and caches of the hidden UI go back to Windows.
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                },
                cts.Token,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Windows efficiency mode for this process while it is hidden — except while the in-app traffic relay carries
    /// the user's traffic (System Proxy mode), which must never be slowed.
    /// </summary>
    private void UpdateEfficiencyMode()
    {
        if (_services is null)
        {
            return;
        }

        var status = _services.Orchestrator.Status;
        var relayInApp = !Domain.Connection.ConnectionStateRules.IsIdle(status.State) && _services.Settings.Current.Connection.Mode == Domain.Connection.ConnectionMode.SystemProxy;
        var on = _services.Background.Hidden && !relayInApp;
        if (on != _efficiencyMode)
        {
            _efficiencyMode = on;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            JackiVpn.Cores.Hosting.ProcessTuning.SetEfficiencyMode(self, on, _services.Logger);
            _services.Logger.LogDebug("Efficiency mode {State}.", on ? "on" : "off");
        }
    }

    private Views.NotchWindow? _notch;
    private Services.MediaSessionSource? _media;

    /// <summary>The notch follows its setting (Settings page or tray menu) and mirrors the connection.</summary>
    private void SetupNotch()
    {
        if (_services is null || _tray is null || _vm is null)
        {
            return;
        }

        var services = _services;
        void Sync(Domain.Settings.NotchSettings n)
        {
            _tray!.SetNotchChecked(n.Enabled);
            if (n.Enabled && _notch is null)
            {
                _notch = new Views.NotchWindow();
                _notch.OpenRequested += (_, _) => ShowWindow();
                _notch.ToggleRequested += (_, _) => _vm!.Dashboard.ToggleCommand.Execute(null);
                _notch.CustomizeRequested += (_, _) =>
                {
                    ShowWindow();
                    _vm!.Navigate(_vm.Settings);
                };
                _notch.HideRequested += (_, _) => SetNotch(false);
                _notch.Moved += (_, x) => services.Settings.Update(s => s with { Ui = s.Ui with { Notch = s.Ui.Notch with { Position = x } } });
                if (_media is null)
                {
                    // Now playing: read from Windows' media session only while the notch is on.
                    _media = new Services.MediaSessionSource();
                    _media.Changed += (_, item) => Dispatcher.BeginInvoke(() => _notch?.SetNowPlaying(item));
                    _media.Start();
                }

                _notch.MediaCommand += (_, c) => _ = c switch
                {
                    < 0 => _media!.PreviousAsync(),
                    > 0 => _media!.NextAsync(),
                    _ => _media!.PlayPauseAsync(),
                };
                _notch.SetNowPlaying(_media.Current);
                _notch.Apply(n);
                _notch.Update(services.Orchestrator.Status, Domain.Visuals.ModePalette.Of(services.Orchestrator.Status.Activity, services.Traffic.Mode));
                _notch.Show();
            }
            else if (!n.Enabled && _notch is not null)
            {
                _notch.Close();
                _notch = null;
            }
            else
            {
                _notch?.Apply(n);
            }
        }

        void SetNotch(bool on) => services.Settings.Update(s => s with { Ui = s.Ui with { Notch = s.Ui.Notch with { Enabled = on } } });
        void Mirror() => _notch?.Update(services.Orchestrator.Status, Domain.Visuals.ModePalette.Of(services.Orchestrator.Status.Activity, services.Traffic.Mode));

        _tray.NotchToggled += (_, on) => SetNotch(on);
        services.Settings.Changed += (_, s) => Dispatcher.BeginInvoke(() => Sync(s.Ui.Notch));
        services.Settings.Changed += (_, _) => Dispatcher.BeginInvoke(Mirror);
        services.Orchestrator.StatusChanged += (_, _) => Dispatcher.BeginInvoke(Mirror);
        services.Traffic.Changed += (_, _) => Dispatcher.BeginInvoke(Mirror);
        Sync(services.Settings.Current.Ui.Notch);

        // Visual check of a test copy only (own data folder): cycles the notch through the states.
        if (InstanceSuffix.Length > 0 && Environment.GetEnvironmentVariable("JACKIVPN_NOTCH_DEMO") == "1")
        {
            var step = 0;
            var demo = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            demo.Tick += (_, _) =>
            {
                var (state, mode) = (step++ % 7) switch
                {
                    0 => (Domain.Connection.ConnectionState.Connecting, Domain.Visuals.VisualMode.Normal),
                    1 => (Domain.Connection.ConnectionState.Connected, Domain.Visuals.VisualMode.Normal),
                    2 => (Domain.Connection.ConnectionState.Connected, Domain.Visuals.VisualMode.Gaming),
                    3 => (Domain.Connection.ConnectionState.Connected, Domain.Visuals.VisualMode.Eco),
                    4 => (Domain.Connection.ConnectionState.Connected, Domain.Visuals.VisualMode.Streaming),
                    5 => (Domain.Connection.ConnectionState.Connected, Domain.Visuals.VisualMode.Work),
                    _ => (Domain.Connection.ConnectionState.Disconnected, Domain.Visuals.VisualMode.Normal),
                };
                _notch?.Update(new Domain.Connection.ConnectionStatus
                {
                    State = state,
                    ProfileName = "🇩🇪 Germany · Reality",
                    ExitCountryName = "Germany",
                    ExitCountryCode = "DE",
                    LatencyMs = 84,
                    Mode = Domain.Connection.ConnectionMode.Tun,
                    ConnectedSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-17),
                }, mode);
                _notch?.DemoExpand(step % 7 == 4);
                if (step > 2 && _notch is not null)
                {
                    _notch.SuppressSound = true;
                }
            };
            demo.Start();
        }
    }

    /// <summary>Re-attach to a TUN session the service kept running (app closed or crashed while connected).</summary>
    private async Task<bool> TryAdoptServiceTunnelAsync()
    {
        // A test copy on its own data folder never takes over the installed app's tunnel.
        if (_services is null || InstanceSuffix.Length > 0 || _services.Service.QueryScm() != ServiceAvailability.Running)
        {
            return false;
        }

        var backend = new ServiceTunnelBackend(_services.Service.ConnectNewClientAsync, _services.LoggerFactory.CreateLogger("JackiVpn.Session"));
        try
        {
            var status = await backend.AttachAsync(_services.Lifetime);
            if (await _services.Orchestrator.TryAdoptAsync(backend, status, _services.Lifetime))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is Ipc.ServiceUnavailableException or Ipc.IpcRemoteException or OperationCanceledException)
        {
            _services.Logger.LogDebug("No running tunnel to re-attach to: {Error}", ex.Message);
        }

        await backend.DisposeAsync();
        return false;
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _services?.NetworkMonitor.NotifyResume();
        }
    }

    /// <summary>Windows logoff / shutdown: tear down synchronously so the system proxy is restored before we die.</summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            Task.Run(() => _services.Orchestrator.DisconnectAsync()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException ex)
        {
            _services.Logger.LogError("Disconnect during session end failed: {Error}", ex.GetBaseException().Message);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Logger.LogError("Unhandled UI exception: {Error}", SecretRedactor.Redact(e.Exception.ToString()));
        _vm?.ShowToast("Unexpected error", SecretRedactor.Redact(e.Exception.Message), warning: true);
        e.Handled = true;
    }

    /// <summary>
    /// Last chance before the process dies: never leave the system proxy pointing at a dead port unless the
    /// user asked for fail-closed behaviour (kill switch). Cores die with us (job object).
    /// </summary>
    private void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            _services?.Logger.LogCritical("Fatal exception: {Error}", SecretRedactor.Redact(e.ExceptionObject.ToString() ?? "?"));
            if (_services is { } s && !s.Settings.Current.KillSwitch.Enabled)
            {
                s.SystemProxy.Restore();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing more can be done; the journal makes the next start restore the proxy.
        }
    }

    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
        {
            return;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _activity?.Dispose();
        _notch?.Close();
        _notch = null;
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Hide();
        }

        try
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Logged by the components; the journal covers anything left half-done.
            System.Diagnostics.Debug.WriteLine(ex);
        }

        _tray?.Dispose();
        _window?.Close();
        _activate?.Set();
        _activate?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Shutdown();
    }
}
