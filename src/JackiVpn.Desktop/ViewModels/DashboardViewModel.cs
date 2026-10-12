using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Connection;
using JackiVpn.Desktop.Composition;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.Desktop.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private const int ChartPoints = 60;
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private readonly double[] _down = new double[ChartPoints];
    private readonly double[] _up = new double[ChartPoints];
    private DateTimeOffset? _since;
    private double _smoothDown;
    private double _smoothUp;
    private CancellationTokenSource? _speedCts;

    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private string _statusTitle = "Not connected";
    [ObservableProperty] private string _statusDetail = "Tap to connect";
    [ObservableProperty] private string _serverName = "—";
    [ObservableProperty] private string _protocol = "—";
    [ObservableProperty] private string _coreText = "—";
    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private string _duration = "00:00";
    [ObservableProperty] private string _uploadSpeed = "0 B/s";
    [ObservableProperty] private string _downloadSpeed = "0 B/s";
    [ObservableProperty] private string _uploadTotal = "0 B";
    [ObservableProperty] private string _downloadTotal = "0 B";
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private bool _killSwitchEngaged;
    [ObservableProperty] private bool _jackiSmartActive;
    [ObservableProperty] private string? _serviceBanner;
    [ObservableProperty] private string? _proxyBanner;
    [ObservableProperty] private bool _canFixProxy;
    [ObservableProperty] private string _selectionText = "Smart: best server";
    [ObservableProperty] private string? _selectionReason;

    // Live latency (keep-alive round trip through the tunnel)
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string? _activityText;
    [ObservableProperty] private ActivityProfile _activity;

    /// <summary>Colour of the connected button: detected activity, or the traffic mode (Eco, Work, …) when one is on.</summary>
    [ObservableProperty] private Domain.Visuals.VisualMode _mode;
    [ObservableProperty] private bool _isGaming;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _latencyTip = "Round trip through the VPN on a kept-alive connection (like ping)";
    [ObservableProperty] private int _latencyLevel = -1;

    // Where traffic exits
    [ObservableProperty] private string? _countryCode;
    [ObservableProperty] private string? _countryName;
    [ObservableProperty] private string? _locationDetail;
    [ObservableProperty] private string? _publicIp;

    // Chart
    [ObservableProperty] private double[] _downSeries = new double[ChartPoints];
    [ObservableProperty] private double[] _upSeries = new double[ChartPoints];

    // Subscription plan
    [ObservableProperty] private string? _planName;
    [ObservableProperty] private string _planUsage = Format.Unknown;
    [ObservableProperty] private double? _planPercent;
    [ObservableProperty] private string _planExpiry = Format.Unknown;

    // Speed test
    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private double _gaugeValue;
    [ObservableProperty] private string _speedPhase = "Ready";
    [ObservableProperty] private string _speedDown = "—";
    [ObservableProperty] private string _speedUp = "—";
    [ObservableProperty] private string _speedPing = "—";
    [ObservableProperty] private string _speedLoadedPing = "—";
    [ObservableProperty] private string _speedNote = "Measures through the VPN when connected (speed.cloudflare.com, 4 streams).";

    // Map, location button and server card
    [ObservableProperty] private double? _exitLatitude;
    [ObservableProperty] private double? _exitLongitude;
    [ObservableProperty] private string? _city;
    [ObservableProperty] private string? _serverFlag;
    [ObservableProperty] private string _serverTitle = "Smart connect";
    [ObservableProperty] private string _serverSubtitle = "Best server picked when you connect";
    [ObservableProperty] private string _serverProtocol = "Auto";
    [ObservableProperty] private string? _planStatus;
    [ObservableProperty] private string _planQuota = "—";
    [ObservableProperty] private bool _planOk = true;
    private string? _selectedName;
    private string? _selectedCountry;
    private string? _selectedProtocol;

    /// <summary>"TUN" or "Proxy" (server card tile).</summary>
    public string ModeTile => IsTunMode ? "TUN" : "Proxy";

    /// <summary>What the tunnel covers (server card tile).</summary>
    public string CoverageTile => IsTunMode ? "All apps" : "Proxy apps";

    /// <summary>The location button: the exit when connected, the chosen server's last known exit otherwise.</summary>
    public string LocationTitle => IsConnected ? CountryName ?? ServerName : _selectedCountry is { } cc ? Application.Connection.HttpLiveProbe.CountryName(cc) ?? cc : "Choose location";

    public string LocationSubtitle => IsConnected ? City ?? LocationDetail ?? PublicIp ?? "Locating…" : _selectedName ?? "Smart: fastest server";

    public string? LocationFlag => IsConnected ? CountryCode : _selectedCountry;

    [RelayCommand]
    private void OpenSubscriptions() => _main.Navigate(_main.Subscriptions);

    /// <summary>Server card and location button follow the connection or the selection.</summary>
    private void RefreshServerCard()
    {
        if (IsConnected)
        {
            ServerFlag = CountryCode;
            ServerTitle = CountryName ?? ServerName;
            ServerSubtitle = string.Join(" · ", new[] { City, ServerName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            ServerProtocol = Protocol;
        }
        else
        {
            ServerFlag = _selectedCountry;
            ServerTitle = _selectedName ?? "Smart connect";
            ServerSubtitle = _selectedName is null ? "Best server picked when you connect" : "Selected server";
            ServerProtocol = _selectedProtocol ?? "Auto";
        }

        OnPropertyChanged(nameof(LocationTitle));
        OnPropertyChanged(nameof(LocationSubtitle));
        OnPropertyChanged(nameof(LocationFlag));
        OnPropertyChanged(nameof(ModeTile));
        OnPropertyChanged(nameof(CoverageTile));
    }

    public DashboardViewModel(AppServices services, IDialogService dialogs, MainViewModel main)
    {
        _s = services;
        _dialogs = dialogs;
        _main = main;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Duration = _since is { } t ? Format.Clock(DateTimeOffset.UtcNow - t) : "00:00";
        _proxyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _proxyTimer.Tick += (_, _) => _ = CheckSystemProxyAsync();
        _proxyTimer.Start();
        services.Orchestrator.StatusChanged += (_, st) => Ui.Post(() =>
        {
            // Hidden: only real state changes are applied; the per-second traffic ticks wait for the window.
            if (!_onScreen && st.State == State)
            {
                _pending = st;
                return;
            }

            _pending = null;
            Apply(st);
        });
        services.Settings.Changed += (_, _) => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(SmartMode));
            OnPropertyChanged(nameof(IsTunMode));
            OnPropertyChanged(nameof(IsProxyMode));
            OnPropertyChanged(nameof(ReduceMotion));
            _ = RefreshSelectionAsync();
        });
        services.SubscriptionService.Updated += (_, _) => Ui.Post(() => _ = RefreshPlanAsync());
        services.Traffic.Changed += (_, _) => Ui.Post(() =>
        {
            if (IsConnected)
            {
                Mode = Domain.Visuals.ModePalette.Of(Activity, _s.Traffic.Mode);
            }
        });
    }

    private readonly DispatcherTimer _proxyTimer;
    private bool _onScreen = true;
    private ConnectionStatus? _pending;

    /// <summary>The window is on screen or not: the clock, the proxy check and live numbers pause while hidden.</summary>
    public void SetOnScreen(bool shown)
    {
        if (_onScreen == shown)
        {
            return;
        }

        _onScreen = shown;
        if (shown)
        {
            _proxyTimer.Start();
            if (_pending is { } st)
            {
                _pending = null;
                Apply(st);
            }

            if (_since is not null)
            {
                _clock.Start();
            }

            _ = CheckSystemProxyAsync();
        }
        else
        {
            _proxyTimer.Stop();
            _clock.Stop();
        }
    }

    public ObservableCollection<string> Warnings { get; } = [];

    public bool IsIdle => ConnectionStateRules.IsIdle(State);

    public bool IsBusy => !IsIdle && State != ConnectionState.Connected;

    public bool IsConnected => State == ConnectionState.Connected;

    public bool HasLocation => CountryCode is not null || PublicIp is not null;

    public bool ReduceMotion => _main.ReduceMotion;

    public bool SmartMode
    {
        get => _s.Settings.Current.Connection.SmartMode;
        set => _s.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = value } });
    }

    public bool IsTunMode
    {
        get => _s.Settings.Current.Connection.Mode == ConnectionMode.Tun;
        set
        {
            if (value)
            {
                _ = ChangeModeAsync(ConnectionMode.Tun);
            }
        }
    }

    public bool IsProxyMode
    {
        get => _s.Settings.Current.Connection.Mode == ConnectionMode.SystemProxy;
        set
        {
            if (value)
            {
                _ = ChangeModeAsync(ConnectionMode.SystemProxy);
            }
        }
    }

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsConnected));
    }

    partial void OnCountryCodeChanged(string? value) => OnPropertyChanged(nameof(HasLocation));

    partial void OnPublicIpChanged(string? value) => OnPropertyChanged(nameof(HasLocation));

    public async Task InitializeAsync()
    {
        Apply(_s.Orchestrator.Status);
        await RefreshSelectionAsync();
        await RefreshPlanAsync();
        await CheckSystemProxyAsync();
    }

    /// <summary>
    /// Read-only look at the Windows proxy. A dead loopback proxy (another VPN app closed without restoring it)
    /// breaks every proxy-aware app — including Claude, which copies the address into its agent at start.
    /// </summary>
    private async Task CheckSystemProxyAsync()
    {
        try
        {
            var status = _s.Orchestrator.Status;
            var ours = status.State == ConnectionState.Connected && status.Mode == ConnectionMode.SystemProxy ? status.LocalProxyPort : null;
            var finding = await Networking.SystemProxy.SystemProxyInspector.InspectAsync(_s.WinInet, ours, _s.Lifetime);
            (ProxyBanner, CanFixProxy) = finding.Health switch
            {
                Networking.SystemProxy.SystemProxyHealth.ForeignDead =>
                    ($"Windows' proxy is set to {finding.Server}, but nothing is running there (probably another VPN app that was closed). " +
                     "Apps that use the system proxy — browsers, Claude, Telegram Desktop — will fail to connect. Turn it off, then restart apps like Claude so they pick up the change.", true),
                Networking.SystemProxy.SystemProxyHealth.ForeignLive when status.State == ConnectionState.Connected && status.Mode == ConnectionMode.Tun =>
                    ($"Windows' proxy points to another program ({finding.Server}). Apps that use the system proxy, including Claude, go through that program instead of this tunnel. " +
                     "Close that program (or turn off its system proxy) and restart Claude for one consistent path.", false),
                _ => ((string?)null, false),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ProxyBanner = null;
        }
    }

    [RelayCommand]
    private async Task FixProxyAsync()
    {
        if (!_dialogs.Confirm("Turn off the stale system proxy",
                "Turn off the Windows manual proxy setting that points to a program that is no longer running? Automatic (PAC) settings and the bypass list are kept.\n\nRestart Claude and other apps that were already open afterwards."))
        {
            return;
        }

        Networking.SystemProxy.SystemProxyInspector.DisableManualProxy(_s.WinInet);
        await CheckSystemProxyAsync();
        _main.ShowToast("Proxy turned off", "Restart apps that were already open (e.g. Claude) so they reconnect.");
    }

    /// <summary>
    /// Connect, disconnect, or — while connecting — cancel. Runs concurrently with itself: the click that cancels
    /// arrives while the connect it cancels is still awaiting.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAsync()
    {
        var state = _s.Orchestrator.State;
        if (state == ConnectionState.Disconnecting)
        {
            return;
        }

        if (!ConnectionStateRules.IsIdle(state))
        {
            await _s.Orchestrator.DisconnectAsync();
            return;
        }

        if (_s.Settings.Current.Connection.Mode == ConnectionMode.Tun && !await EnsureServiceAsync())
        {
            return;
        }

        await _s.Orchestrator.ConnectAsync(new ConnectRequest(), _s.Lifetime);
    }

    /// <summary>TUN needs the privileged service; explain and offer the fix instead of failing later.</summary>
    private async Task<bool> EnsureServiceAsync()
    {
        var state = await _s.Service.RefreshAsync(_s.Lifetime);
        ServiceBanner = state switch
        {
            ServiceAvailability.Running => null,
            ServiceAvailability.NotInstalled => "TUN mode needs the IBOX service, which is not installed. Install it (administrator) on the Network page, or use System Proxy mode.",
            ServiceAvailability.Rejected => "The IBOX service does not accept this copy of the app (updated or moved?). Run Repair Service on the Network page.",
            _ => "The IBOX service is not running. Start it from the Network page (Repair Service), or use System Proxy mode.",
        };
        if (ServiceBanner is not null)
        {
            _main.Navigate(_main.Network);
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void OpenServers() => _main.Navigate(_main.Servers);

    [RelayCommand]
    private void DismissBanner() => ServiceBanner = null;

    [RelayCommand]
    private void CopyIp()
    {
        if (PublicIp is { } ip)
        {
            System.Windows.Clipboard.SetText(ip);
            _main.ShowToast("Copied", $"IP address {ip} copied to the clipboard.");
        }
    }

    [RelayCommand]
    private Task RefreshLocationAsync() => _s.Orchestrator.RefreshLocationAsync(_s.Lifetime);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunSpeedTestAsync()
    {
        if (IsSpeedTesting)
        {
            _speedCts?.Cancel();
            return;
        }

        IsSpeedTesting = true;
        SpeedDown = SpeedUp = SpeedPing = SpeedLoadedPing = "—";
        GaugeValue = 0;
        var status = _s.Orchestrator.Status;
        var mode = status.State == ConnectionState.Connected && status.Mode is { } m ? m : ConnectionMode.Tun;
        var port = status.State == ConnectionState.Connected && mode == ConnectionMode.SystemProxy ? status.LocalProxyPort : null;
        SpeedNote = status.State == ConnectionState.Connected ? $"Through the VPN · {status.ProfileName}" : "Not connected: this measures your direct connection.";
        _speedCts = CancellationTokenSource.CreateLinkedTokenSource(_s.Lifetime);
        var progress = new Progress<SpeedTestProgress>(p =>
        {
            SpeedPhase = p.Phase switch
            {
                SpeedTestPhase.Latency => "Measuring latency…",
                SpeedTestPhase.Download => "Download",
                SpeedTestPhase.Upload => "Upload",
                _ => "Done",
            };
            GaugeValue = p.CurrentMbps;
        });
        try
        {
            var r = await Task.Run(() => _s.SpeedTest.RunAsync(mode, port, progress, _speedCts.Token));
            SpeedDown = Mbps(r.DownloadMbps);
            SpeedUp = Mbps(r.UploadMbps);
            SpeedPing = Format.Ms(r.IdleLatencyMs);
            SpeedLoadedPing = Format.Ms(r.LoadedLatencyMs);
            GaugeValue = r.DownloadMbps ?? 0;
            SpeedPhase = r.Error is null ? "Download" : "Failed";
            if (r.Error is not null)
            {
                SpeedNote = "Speed test failed: " + r.Error;
            }
        }
        catch (OperationCanceledException)
        {
            SpeedPhase = "Cancelled";
            GaugeValue = 0;
        }
        finally
        {
            IsSpeedTesting = false;
            _speedCts?.Dispose();
            _speedCts = null;
        }
    }

    private static string Mbps(double? v) => v is { } x ? x.ToString(x >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " Mbps" : "—";

    private async Task ChangeModeAsync(ConnectionMode mode)
    {
        if (_s.Settings.Current.Connection.Mode == mode)
        {
            return;
        }

        var wasActive = !ConnectionStateRules.IsIdle(_s.Orchestrator.State);
        if (wasActive && !_dialogs.Confirm("Change connection mode", "Reconnect now using the new mode?"))
        {
            OnPropertyChanged(nameof(IsTunMode));
            OnPropertyChanged(nameof(IsProxyMode));
            return;
        }

        _s.Settings.Update(x => x with { Connection = x.Connection with { Mode = mode } });
        if (wasActive)
        {
            await _s.Orchestrator.DisconnectAsync();
            if (mode != ConnectionMode.Tun || await EnsureServiceAsync())
            {
                await _s.Orchestrator.ConnectAsync(new ConnectRequest(), _s.Lifetime);
            }
        }
    }

    private void Apply(ConnectionStatus st)
    {
        var wasConnected = State == ConnectionState.Connected;
        if (st.State != State && st.State is ConnectionState.Connected or ConnectionState.Disconnected)
        {
            _ = CheckSystemProxyAsync();
        }

        State = st.State;
        LastError = st.State == ConnectionState.Connected ? null : st.LastError;
        KillSwitchEngaged = st.KillSwitchEngaged;
        JackiSmartActive = st.JackiSmartConnectActive;
        SelectionReason = st.SelectionReason;
        (StatusTitle, StatusDetail) = st.State switch
        {
            ConnectionState.Disconnected => ("Not connected", "Tap the button to connect"),
            ConnectionState.Preparing => ("Preparing…", "Loading servers and routing rules"),
            ConnectionState.Testing => ("Finding the best server…", "Testing latency and reliability"),
            ConnectionState.Connecting => ("Connecting…", st.ProfileName ?? "Starting the tunnel"),
            ConnectionState.Connected => ("Connected", st.Mode == ConnectionMode.Tun ? "All apps are protected" : "Apps using the system proxy are protected"),
            ConnectionState.Reconnecting => ("Reconnecting…", st.LastError ?? "Restoring the connection"),
            ConnectionState.SwitchingServer => ("Switching server…", "Moving to a healthier server"),
            ConnectionState.Disconnecting => ("Disconnecting…", "Restoring network settings"),
            ConnectionState.Faulted => ("Connection failed", st.KillSwitchEngaged ? "Kill switch is blocking traffic. Disconnect to restore internet." : "Tap to try again"),
            ConnectionState.Recovering => ("Recovering…", "Cleaning up after an unexpected exit"),
            _ => (st.State.ToString(), string.Empty),
        };

        if (st.State == ConnectionState.Connected)
        {
            ServerName = st.ProfileName ?? "—";
            Protocol = Names.Protocol(st.Protocol);
            CoreText = st.Core is { } c ? (c == CoreKind.SingBox ? "sing-box" : "Xray") : "—";
            ModeText = st.Mode == ConnectionMode.Tun ? "TUN" : "Proxy";
            CountryCode = st.ExitCountryCode;
            CountryName = st.ExitCountryName ?? (st.PublicIp is null ? (_s.Settings.Current.Probe.CheckPublicIp ? "Locating…" : null) : "Unknown country");
            LocationDetail = string.Join(" · ", new[] { st.ExitCity, st.ExitIsp }.Where(x => !string.IsNullOrWhiteSpace(x)));
            PublicIp = st.PublicIp;
            City = st.ExitCity;
            ExitLatitude = st.ExitLatitude;
            ExitLongitude = st.ExitLongitude;
            LatencyText = st.LatencyMs is { } ms ? $"{ms:0} ms" + (st.JitterMs is { } j && j >= 1 ? $" ±{j:0}" : string.Empty) : "…";
            LatencyTip = "Round trip through the VPN on a kept-alive connection (median of the last 3, like ping)"
                + (st.JitterMs is { } jt ? $". Jitter {jt:0} ms." : ".");
            Activity = st.Activity;
            Mode = Domain.Visuals.ModePalette.Of(st.Activity, _s.Traffic.Mode);
            IsGaming = st.Activity == ActivityProfile.Gaming;
            IsStreaming = st.Activity == ActivityProfile.Streaming;
            ActivityText = st.Activity switch
            {
                ActivityProfile.Gaming => "🎮 Gaming optimized",
                ActivityProfile.Streaming => "📺 Streaming optimized",
                _ => null,
            };
            LatencyLevel = st.LatencyMs switch
            {
                null => -1,
                < 150 => 0,
                < 300 => 1,
                _ => 2,
            };
            _since = st.ConnectedSinceUtc;
            if (_onScreen)
            {
                _clock.Start();
            }
            if (!wasConnected)
            {
                Array.Clear(_down);
                Array.Clear(_up);
                _smoothDown = _smoothUp = 0;
            }
        }
        else if (ConnectionStateRules.IsIdle(st.State))
        {
            _since = null;
            _clock.Stop();
            Duration = "00:00";
            LatencyText = "—";
            ActivityText = null;
            Activity = ActivityProfile.Normal;
            Mode = Domain.Visuals.VisualMode.Normal;
            IsGaming = IsStreaming = false;
            LatencyLevel = -1;
            CountryCode = null;
            CountryName = null;
            LocationDetail = null;
            PublicIp = null;
            City = null;
            ExitLatitude = ExitLongitude = null;
            ServerName = Protocol = CoreText = ModeText = "—";
        }

        if (st.Traffic is { } t && st.State == ConnectionState.Connected)
        {
            // Exponential smoothing keeps the numbers readable instead of jumping every second.
            _smoothDown = _smoothDown * 0.55 + t.DownloadBytesPerSecond * 0.45;
            _smoothUp = _smoothUp * 0.55 + t.UploadBytesPerSecond * 0.45;
            UploadSpeed = Format.Speed((long)_smoothUp);
            DownloadSpeed = Format.Speed((long)_smoothDown);
            UploadTotal = Format.Bytes(t.UploadBytesTotal);
            DownloadTotal = Format.Bytes(t.DownloadBytesTotal);
            Push(_down, t.DownloadBytesPerSecond);
            Push(_up, t.UploadBytesPerSecond);
            DownSeries = (double[])_down.Clone();
            UpSeries = (double[])_up.Clone();
        }
        else if (st.State != ConnectionState.Connected)
        {
            UploadSpeed = DownloadSpeed = "0 B/s";
        }

        // The status arrives every second: rebuild the warning list only when it changed.
        if (!Warnings.SequenceEqual(st.Warnings))
        {
            Warnings.Clear();
            foreach (var w in st.Warnings)
            {
                Warnings.Add(w);
            }
        }

        RefreshServerCard();
    }

    private static void Push(double[] buffer, double value)
    {
        Array.Copy(buffer, 1, buffer, 0, buffer.Length - 1);
        buffer[^1] = value;
    }

    private async Task RefreshSelectionAsync()
    {
        var c = _s.Settings.Current.Connection;
        if (c.SmartMode)
        {
            SelectionText = "Smart: picks the fastest working server";
            _selectedName = null;
            _selectedCountry = null;
            _selectedProtocol = null;
            RefreshServerCard();
            return;
        }

        var p = c.SelectedProfileId is { } id ? await _s.Profiles.GetAsync(id, _s.Lifetime) : null;
        SelectionText = p is null ? "No server selected — choose one on the Servers page" : "Selected: " + p.DisplayName;
        _selectedName = p?.DisplayName;
        _selectedCountry = p is null ? null : _s.Insights.Get(p.Fingerprint)?.Exit?.CountryCode ?? Domain.Visuals.CountryGuess.FromName(p.DisplayName);
        _selectedProtocol = p is null ? null : Names.Protocol(p.Protocol.ToString());
        RefreshServerCard();
    }

    private async Task RefreshPlanAsync()
    {
        var subs = await _s.Subscriptions.GetAllAsync(_s.Lifetime);
        var sub = subs.FirstOrDefault(x => x.Enabled);
        if (sub is null)
        {
            PlanName = null;
            return;
        }

        var m = sub.Metadata;
        PlanName = sub.Name;
        PlanPercent = m.UsedPercent;
        PlanUsage = m.IsUnlimitedTraffic == true
            ? $"{Format.Bytes(m.UsedBytes)} used · Unlimited"
            : $"{Format.Bytes(m.UsedBytes)} of {Format.Bytes(m.TotalBytes)}";
        PlanExpiry = m.IsUnlimitedExpiry == true ? "Never expires" : m.ExpireUtc is { } e ? $"Expires {Format.When(e)} ({Format.Duration(m.TimeRemaining(DateTimeOffset.UtcNow))})" : "Expiry unknown";
        var status = sub.Metadata.ComputeStatus(DateTimeOffset.UtcNow);
        if (status is SubscriptionStatus.Expired or SubscriptionStatus.Limited)
        {
            PlanExpiry += " · " + status;
        }

        PlanOk = status is not (SubscriptionStatus.Expired or SubscriptionStatus.Limited);
        PlanStatus = PlanOk ? "Active" : status.ToString();
        PlanQuota = m.IsUnlimitedTraffic == true ? "Unlimited"
            : m.TotalBytes is { } total && m.UsedBytes is { } used ? $"{Format.Bytes(Math.Max(0, total - used))} left"
            : "Quota unknown";
    }
}
