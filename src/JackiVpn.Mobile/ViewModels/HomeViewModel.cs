using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Platforms.Android;
using JackiVpn.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MobileServices _s;
    private readonly IDispatcherTimer _clock;
    private DateTimeOffset? _since;
    private double _smoothDown;
    private double _smoothUp;

    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private ActivityProfile _activity;
    private ConnectionStatus _last = new();
    [ObservableProperty] private string _statusTitle = "Not connected";
    [ObservableProperty] private string _statusDetail = "Tap to connect";
    [ObservableProperty] private string _serverName = "—";
    [ObservableProperty] private string _protocol = "—";
    [ObservableProperty] private string _duration = "00:00";
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private Color _latencyColor = Colors.Gray;
    [ObservableProperty] private string? _countryCode;
    [ObservableProperty] private string? _countryName;
    [ObservableProperty] private string? _locationDetail;
    [ObservableProperty] private string? _publicIp;
    [ObservableProperty] private string _downSpeed = "0 B/s";
    [ObservableProperty] private string _upSpeed = "0 B/s";
    [ObservableProperty] private string _downTotal = "0 B";
    [ObservableProperty] private string _upTotal = "0 B";
    [ObservableProperty] private string? _selectionReason;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string _selectionText = "Smart: fastest working server";
    [ObservableProperty] private bool _jackiSmart;
    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private double _speedFraction;
    [ObservableProperty] private string _speedPhase = "Measure download, upload and latency through the VPN";
    [ObservableProperty] private string _speedDown = "—";
    [ObservableProperty] private string _speedUp = "—";
    [ObservableProperty] private string _speedPing = "—";
    private CancellationTokenSource? _speedCts;

    public HomeViewModel(MobileServices services)
    {
        _s = services;
        _clock = Microsoft.Maui.Controls.Application.Current!.Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) =>
        {
            Duration = _since is { } t ? Fmt.Clock(DateTimeOffset.UtcNow - t) : "00:00";
            Raise(nameof(BigText), BigText);
        };
        services.Orchestrator.StatusChanged += (_, st) => Fmt.OnUi(() => Apply(st));
        services.Settings.Changed += (_, _) => Fmt.OnUi(() =>
        {
            OnPropertyChanged(nameof(SmartMode));
            _ = RefreshSelectionAsync();
        });
        Apply(services.Orchestrator.Status);
        _ = RefreshSelectionAsync();
        services.Traffic.Changed += (_, _) => Fmt.OnUi(Refresh);
        services.Usage.Updated += (_, _) =>
        {
            if (!services.IsBackground)
            {
                var line = UsageText();
                Fmt.OnUi(() => UsageLine = line);
            }
        };
        UsageLine = UsageText();
    }

    [ObservableProperty] private string _usageLine = string.Empty;

    /// <summary>"Today 120 MB · This period 3.2 GB / 20 GB" under the speed numbers.</summary>
    private string UsageText()
    {
        var b = _s.UsageBook.Budget(_s.Settings.Current.Usage);
        var period = Application.Usage.UsageMonitor.Size(b.Used) + (b.Cap is { } cap ? " / " + Application.Usage.UsageMonitor.Size(cap) : string.Empty);
        return L.Get("Usage_Today") + " " + Application.Usage.UsageMonitor.Size(b.Today) + "  ·  " + L.Get("Usage_Period") + " " + period;
    }

    [RelayCommand]
    private static Task OpenUsageAsync() => Shell.Current.GoToAsync("usage");

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>Hero line: the session timer while connected, otherwise the state.</summary>
    public string BigText => IsConnected ? L.N(Duration) : StatusTitle;

    /// <summary>Under the hero: the exit IP while connected, otherwise what to do.</summary>
    public string SubText => IsConnected
        ? (PublicIp is { } ip ? L.F("Home_YourIp", ip) : StatusDetail)
        : StatusDetail;

    public string StatusLine => IsConnected ? L.Get("St_On") : IsBusy ? L.Get("St_Working") : L.Get("St_Protection");

    public bool HasActivity => IsConnected && Activity != ActivityProfile.Normal;

    public string ActivityText => Activity switch
    {
        ActivityProfile.Gaming => L.Get("Act_Gaming"),
        ActivityProfile.Streaming => L.Get("Act_Streaming"),
        _ => string.Empty,
    };

    public string ActivityIcon => Activity == ActivityProfile.Gaming ? "gamepad" : "tv";

    /// <summary>The mode colouring the connected button: detected activity, or the traffic mode (Eco, Work, …) when on.</summary>
    public Domain.Visuals.VisualMode Mode => IsConnected ? Domain.Visuals.ModePalette.Of(Activity, _s.Traffic.Mode) : Domain.Visuals.VisualMode.Normal;

    /// <summary>Connected colour: teal; gaming magenta-red; streaming blue; eco light green; work medium blue.</summary>
    public string AccentGradient => Mode switch
    {
        Domain.Visuals.VisualMode.Gaming => "GamingGradient",
        Domain.Visuals.VisualMode.Streaming => "StreamingGradient",
        Domain.Visuals.VisualMode.Eco => "EcoGradient",
        Domain.Visuals.VisualMode.Work => "WorkGradient",
        _ => "TealGradient",
    };

    public Color AccentColor
    {
        get
        {
            var v = Domain.Visuals.ModePalette.Colors(Mode).Main;
            return Color.FromRgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));
        }
    }

    public Controls.PowerVisual PowerVisual => IsConnected ? Controls.PowerVisual.Connected : IsBusy ? Controls.PowerVisual.Busy : Controls.PowerVisual.Idle;

    public string LocationTitle => IsConnected ? (CountryName ?? ServerName) : SmartMode ? L.Get("Home_AutoServer") : SelectionTitle;

    public string LocationSubtitle => IsConnected
        ? string.Join(" · ", new[] { LocationDetail, ServerName }.Where(x => !string.IsNullOrWhiteSpace(x)))
        : SmartMode ? L.Get("Home_AutoServerDetail") : SelectionDetail;

    public string LocationFlag => IsConnected ? Flag : SmartMode ? "⚡" : "🌐";

    [ObservableProperty] private string _selectionTitle = string.Empty;
    [ObservableProperty] private string _selectionDetail = string.Empty;

    /// <summary>
    /// Raises the derived properties whose value actually changed. Status updates arrive every second with fresh
    /// traffic numbers; re-raising unchanged values would replay the view's state animations on every tick.
    /// </summary>
    private void Refresh()
    {
        Raise(nameof(BigText), BigText);
        Raise(nameof(SubText), SubText);
        Raise(nameof(StatusLine), StatusLine);
        Raise(nameof(HasActivity), HasActivity);
        Raise(nameof(ActivityText), ActivityText);
        Raise(nameof(ActivityIcon), ActivityIcon);
        Raise(nameof(AccentGradient), AccentGradient);
        Raise(nameof(AccentColor), AccentColor);
        Raise(nameof(PowerVisual), PowerVisual);
        Raise(nameof(LocationTitle), LocationTitle);
        Raise(nameof(LocationSubtitle), LocationSubtitle);
        Raise(nameof(LocationFlag), LocationFlag);
    }

    private readonly Dictionary<string, object?> _raised = [];

    private void Raise(string name, object? value)
    {
        if (_raised.TryGetValue(name, out var old) && Equals(old, value))
        {
            return;
        }

        _raised[name] = value;
        OnPropertyChanged(name);
    }

    /// <summary>Re-reads every text after a language switch.</summary>
    public void Relocalize()
    {
        Apply(_s.Orchestrator.Status);
        _ = RefreshSelectionAsync();
    }

    public bool IsBusy => !ConnectionStateRules.IsIdle(State) && State != ConnectionState.Connected;

    public bool HasLocation => CountryCode is not null || PublicIp is not null;

    public string Flag => Fmt.Flag(CountryCode);

    public bool HasError => !string.IsNullOrEmpty(LastError);

    public bool HasSelectionReason => !string.IsNullOrEmpty(SelectionReason);

    public string SpeedButtonText => L.Get(IsSpeedTesting ? "Speed_Stop" : "Speed_Start");

    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnActivityChanged(ActivityProfile value) => Refresh();

    partial void OnSelectionReasonChanged(string? value) => OnPropertyChanged(nameof(HasSelectionReason));

    partial void OnIsSpeedTestingChanged(bool value) => OnPropertyChanged(nameof(SpeedButtonText));

    public bool HasLocationDetail => !string.IsNullOrEmpty(LocationDetail);

    public bool SmartMode
    {
        get => _s.Settings.Current.Connection.SmartMode;
        set => _s.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = value } });
    }

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsBusy));
        Refresh();
    }

    partial void OnCountryCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(Flag));
    }

    partial void OnLocationDetailChanged(string? value) => OnPropertyChanged(nameof(HasLocationDetail));

    partial void OnPublicIpChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLocation));
        Refresh();
    }

    /// <summary>Shows an unexpected failure instead of letting it crash the app (logged redacted).</summary>
    public void ReportUnexpected(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var message = Security.SecretRedactor.Redact(ex.Message);
        _s.Logger.LogError(ex, "Unexpected UI error: {Message}", message);
        LastError = L.Get("Err_Unexpected") + " " + message;
    }

    /// <summary>Concurrent on purpose: a second tap while connecting must cancel/disconnect, not be ignored.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAsync()
    {
        if (!ConnectionStateRules.IsIdle(_s.Orchestrator.State))
        {
            await _s.Orchestrator.DisconnectAsync();
            return;
        }

        if (Platform.CurrentActivity is not { } activity || !await VpnPermission.RequestAsync(activity))
        {
            LastError = L.Get("Err_VpnPermission");
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }

        await _s.InitializeAsync();
        await _s.Orchestrator.ConnectAsync(new ConnectRequest(), _s.Lifetime);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SpeedTestAsync()
    {
        if (IsSpeedTesting)
        {
            _speedCts?.Cancel();
            return;
        }

        var st = _s.Orchestrator.Status;
        if (st.State != ConnectionState.Connected || st.Mode is not { } mode)
        {
            return;
        }

        IsSpeedTesting = true;
        SpeedDown = SpeedUp = SpeedPing = "—";
        _speedCts = CancellationTokenSource.CreateLinkedTokenSource(_s.Lifetime);
        var progress = new Progress<SpeedTestProgress>(p =>
        {
            SpeedFraction = p.Phase switch
            {
                SpeedTestPhase.Latency => 0.05,
                SpeedTestPhase.Download => 0.05 + p.Fraction * 0.55,
                SpeedTestPhase.Upload => 0.6 + p.Fraction * 0.4,
                _ => 1,
            };
            SpeedPhase = p.Phase switch
            {
                SpeedTestPhase.Latency => "Measuring latency…",
                SpeedTestPhase.Download => $"Download {p.CurrentMbps:0.0} Mbps",
                SpeedTestPhase.Upload => $"Upload {p.CurrentMbps:0.0} Mbps",
                _ => "Done",
            };
            if (p.Phase == SpeedTestPhase.Download)
            {
                SpeedDown = $"{p.CurrentMbps:0.0}";
            }
            else if (p.Phase == SpeedTestPhase.Upload)
            {
                SpeedUp = $"{p.CurrentMbps:0.0}";
            }
        });

        try
        {
            var r = await Task.Run(() => _s.SpeedTest.RunAsync(mode, st.LocalProxyPort, progress, _speedCts.Token));
            SpeedDown = r.DownloadMbps is { } d ? $"{d:0.0}" : "—";
            SpeedUp = r.UploadMbps is { } u ? $"{u:0.0}" : "—";
            SpeedPing = r.IdleLatencyMs is { } l ? $"{l:0}" : "—";
            SpeedPhase = r.Error is { } e ? "Test incomplete: " + e : $"Cloudflare {r.Server}" + (r.LoadedLatencyMs is { } ll ? $" · {ll:0} ms under load" : string.Empty);
            SpeedFraction = 1;
        }
        catch (OperationCanceledException)
        {
            SpeedPhase = "Speed test cancelled.";
        }
        finally
        {
            IsSpeedTesting = false;
            _speedCts.Dispose();
            _speedCts = null;
        }
    }

    [RelayCommand]
    private Task ChooseServerAsync() => ServerPickerRequested?.Invoke() ?? Task.CompletedTask;

    /// <summary>The home view shows the server picker sheet.</summary>
    public Func<Task>? ServerPickerRequested { get; set; }

    [RelayCommand]
    private Task RefreshLocationAsync() => _s.Orchestrator.RefreshLocationAsync(_s.Lifetime);

    [RelayCommand]
    private async Task CopyIpAsync()
    {
        if (PublicIp is { } ip)
        {
            await Clipboard.Default.SetTextAsync(ip);
        }
    }

    private void Apply(ConnectionStatus st)
    {
        var wasConnected = State == ConnectionState.Connected;
        State = st.State;
        LastError = st.State is ConnectionState.Connected or ConnectionState.Preparing or ConnectionState.Testing or ConnectionState.Connecting ? null : st.LastError;
        SelectionReason = st.SelectionReason;
        JackiSmart = st.JackiSmartConnectActive;
        _last = st;
        (StatusTitle, StatusDetail) = st.State switch
        {
            ConnectionState.Disconnected => (L.Get("St_Off"), L.Get("St_OffDetail")),
            ConnectionState.Preparing => (L.Get("St_Preparing"), L.Get("St_PreparingDetail")),
            ConnectionState.Testing => (L.Get("St_Testing"), L.Get("St_TestingDetail")),
            ConnectionState.Connecting => (L.Get("St_Connecting"), st.ProfileName ?? L.Get("St_ConnectingDetail")),
            ConnectionState.Connected => (L.Get("St_On"), st.Mode == ConnectionMode.SystemProxy
                ? L.F("St_ProxyOnly", st.LocalProxyPort)
                : L.Get("St_OnDetail")),
            ConnectionState.Reconnecting => (L.Get("St_Reconnecting"), st.LastError ?? L.Get("St_ReconnectingDetail")),
            ConnectionState.SwitchingServer => (L.Get("St_Switching"), L.Get("St_SwitchingDetail")),
            ConnectionState.Disconnecting => (L.Get("St_Disconnecting"), L.Get("St_DisconnectingDetail")),
            ConnectionState.Faulted => (L.Get("St_Failed"), L.Get("St_FailedDetail")),
            _ => (st.State.ToString(), string.Empty),
        };
        Activity = st.State == ConnectionState.Connected ? st.Activity : ActivityProfile.Normal;
        Refresh();

        if (st.State == ConnectionState.Connected)
        {
            ServerName = st.ProfileName ?? "—";
            Protocol = Enum.TryParse<Domain.Profiles.ProxyProtocol>(st.Protocol, out var p) ? Fmt.Protocol(p) : st.Protocol ?? "—";
            CountryCode = st.ExitCountryCode;
            CountryName = st.ExitCountryName ?? (st.PublicIp is null ? L.Get("Home_Locating") : L.Get("Home_UnknownCountry"));
            LocationDetail = string.Join(" · ", new[] { st.ExitCity, st.ExitIsp }.Where(x => !string.IsNullOrWhiteSpace(x)));
            PublicIp = st.PublicIp;
            LatencyText = st.LatencyMs is { } ms ? $"{ms:0} ms" : "…";
            LatencyColor = st.LatencyMs switch
            {
                null => Colors.Gray,
                < 150 => Color.FromArgb("#22C55E"),
                < 300 => Color.FromArgb("#F59E0B"),
                _ => Color.FromArgb("#EF4444"),
            };
            _since = st.ConnectedSinceUtc;
            _clock.Start();
            if (!wasConnected)
            {
                _smoothDown = _smoothUp = 0;
            }

            if (st.Traffic is { } t)
            {
                _smoothDown = _smoothDown * 0.55 + t.DownloadBytesPerSecond * 0.45;
                _smoothUp = _smoothUp * 0.55 + t.UploadBytesPerSecond * 0.45;
                DownSpeed = Fmt.Speed(_smoothDown);
                UpSpeed = Fmt.Speed(_smoothUp);
                DownTotal = Fmt.Bytes(t.DownloadBytesTotal);
                UpTotal = Fmt.Bytes(t.UploadBytesTotal);
            }
        }
        else if (ConnectionStateRules.IsIdle(st.State))
        {
            _since = null;
            _clock.Stop();
            Duration = "00:00";
            LatencyText = "—";
            LatencyColor = Colors.Gray;
            CountryCode = CountryName = LocationDetail = PublicIp = null;
            DownSpeed = UpSpeed = "0 B/s";
            ServerName = Protocol = "—";
        }


        Refresh();
    }

    private async Task RefreshSelectionAsync()
    {
        var c = _s.Settings.Current.Connection;
        if (c.SmartMode)
        {
            SelectionText = L.Get("Home_AutoServerDetail");
            Refresh();
            return;
        }

        var p = c.SelectedProfileId is { } id ? await _s.Profiles.GetAsync(id, _s.Lifetime) : null;
        SelectionText = p is null ? L.Get("Home_NoServer") : p.DisplayName;
        SelectionTitle = p?.DisplayName ?? L.Get("Home_NoServer");
        SelectionDetail = p is null ? L.Get("Home_NoServerDetail") : Fmt.Protocol(p.Protocol) + " · " + Fmt.Transport(p.Transport.Type);
        Refresh();
    }
}
