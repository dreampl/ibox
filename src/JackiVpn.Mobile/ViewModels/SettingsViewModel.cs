using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Infrastructure.Updates;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;
using JackiVpn.Routing;
using JackiVpn.Security;

namespace JackiVpn.Mobile.ViewModels;

/// <summary>Settings: every choice opens a drawer picker; toggles are instant; nothing needs a restart.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly (string Label, string Value)[] Fingerprints =
        [("Chrome", "chrome"), ("Firefox", "firefox"), ("Safari", "safari"), ("Edge", "edge"), ("iOS", "ios"), ("Android", "android"), ("Randomized", "randomized")];

    private readonly MobileServices _s;

    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _updateProgress;

    public SettingsViewModel(MobileServices services)
    {
        _s = services;
        services.Settings.Changed += (_, _) => Fmt.OnUi(() => OnPropertyChanged(string.Empty));
        services.Orchestrator.StatusChanged += (_, _) => Fmt.OnUi(() => OnPropertyChanged(nameof(ShowApplyHint)));
        services.Updates.Changed += (_, _) => Fmt.OnUi(() =>
        {
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateTitle));
            OnPropertyChanged(nameof(UpdateNotes));
        });
    }

    private AppSettings S => _s.Settings.Current;

    public bool ShowApplyHint => !ConnectionStateRules.IsIdle(_s.Orchestrator.State);

    // ------------------------------------------------------------------ DNS search

    public string DnsValue => _s.DnsFinder.BestLabel() is { } best ? best.Split(" · ")[0] : L.Get("Dns_NotYet");

    public bool AutoDns
    {
        get => S.Connectivity.AutoPickDns;
        set => _s.Settings.Update(x => x with { Connectivity = x.Connectivity with { AutoPickDns = value } });
    }

    /// <summary>Tests the resolvers on this network (a thinking orb while it runs), then offers the results.</summary>
    [RelayCommand]
    private async Task FindDnsAsync()
    {
        var orb = new OrbView { State = Domain.Visuals.OrbState.Searching, WidthRequest = 72, HeightRequest = 72, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 10, 0, 6) };
        var note = new Label { Text = L.Get("Dns_Searching"), HorizontalTextAlignment = TextAlignment.Center, TextColor = Rtl.Res("Text2"), FontSize = 13, Margin = new Thickness(24, 0, 24, 18) };
        var search = _s.DnsFinder.SearchAsync(_s.Lifetime);
        var sheet = Sheets.ShowAsync(new VerticalStackLayout { Children = { orb, note } }, L.Get("Dns_Title"));
        IReadOnlyList<Application.Connectivity.DnsResult> results;
        try
        {
            results = await search;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Sheets.HideAsync();
        _ = sheet;
        var options = results.Where(r => r.Reachable).Take(10).Select(r => new SheetOption<string>(
            $"{r.Candidate.Name} · {(r.Candidate.Transport == DnsTransport.Https ? L.Get("Dns_Encrypted") : L.Get("Dns_Plain"))} · {r.LatencyMs:0} ms",
            r.Candidate.Label,
            r.Clean ? L.Get("Dns_Clean") : r.Note?.StartsWith("intercepted", StringComparison.Ordinal) == true ? L.Get("Dns_Intercepted") : L.Get("Dns_Filtered"))).ToList();
        if (options.Count == 0)
        {
            await Shell.Current.DisplayAlertAsync(L.Get("Dns_Title"), L.Get("Dns_None"), L.Get("Ok"));
            return;
        }

        var best = results.FirstOrDefault(r => r.Reachable && r.Clean);
        var chosen = await Sheets.PickAsync(L.Get("Dns_Pick"), options, best?.Candidate.Label ?? string.Empty);
        if (results.FirstOrDefault(r => r.Candidate.Label == chosen) is { Clean: true } pick)
        {
            _s.Settings.Update(x => x with { Dns = x.Dns with { Bootstrap = pick.Candidate.ToEndpoint() } });
        }

        OnPropertyChanged(nameof(DnsValue));
    }

    // ------------------------------------------------------------------ general

    public string LanguageValue => S.Ui.Language switch
    {
        "fa" => "فارسی",
        "en" => "English",
        _ => L.Get("Set_LangAuto"),
    };

    [RelayCommand]
    private async Task ChooseLanguageAsync()
    {
        var v = await Sheets.PickAsync(L.Get("Set_Language"),
            [new(L.Get("Set_LangAuto"), "auto", L.Get("Set_LangAutoHint")), new("English", "en"), new("فارسی", "fa")], S.Ui.Language);
        if (v != S.Ui.Language)
        {
            _s.Settings.Update(x => x with { Ui = x.Ui with { Language = v } });
            L.Instance.Apply(v);
        }
    }

    public string ConnectionTypeValue => L.Get(S.Connection.Mode == ConnectionMode.SystemProxy ? "Set_ProxyOnly" : "Set_Vpn");

    [RelayCommand]
    private async Task ChooseConnectionTypeAsync()
    {
        var v = await Sheets.PickAsync(L.Get("Set_ConnectionType"),
            [new(L.Get("Set_Vpn"), ConnectionMode.Tun, L.Get("Set_VpnHint"), "shield"), new(L.Get("Set_ProxyOnly"), ConnectionMode.SystemProxy, L.Get("Set_ProxyOnlyHint"), "key")],
            S.Connection.Mode);
        _s.Settings.Update(x => x with { Connection = x.Connection with { Mode = v } });
    }

    public bool IsProxyOnly => S.Connection.Mode == ConnectionMode.SystemProxy;

    public bool IsVpn => !IsProxyOnly;

    public string SplitModeValue => L.Get(S.SplitTunnel.Mode switch
    {
        SplitTunnelMode.ExcludeSelected => "Set_SplitExclude",
        SplitTunnelMode.OnlySelected => "Set_SplitOnly",
        _ => "Set_SplitAll",
    });

    [RelayCommand]
    private async Task ChooseSplitModeAsync()
    {
        var v = await Sheets.PickAsync(L.Get("Set_AppsUsingVpn"),
            [new(L.Get("Set_SplitAll"), SplitTunnelMode.AllApps), new(L.Get("Set_SplitExclude"), SplitTunnelMode.ExcludeSelected, L.Get("Set_SplitExcludeHint")), new(L.Get("Set_SplitOnly"), SplitTunnelMode.OnlySelected, L.Get("Set_SplitOnlyHint"))],
            S.SplitTunnel.Mode);
        _s.Settings.Update(x => x with { SplitTunnel = x.SplitTunnel with { Mode = v } });
        if (v != SplitTunnelMode.AllApps)
        {
            await Shell.Current.GoToAsync("apps");
        }
    }

    public bool CanChooseApps => S.SplitTunnel.Mode != SplitTunnelMode.AllApps;

    public string SelectedAppsText => L.N(L.F("Set_AppsSelected", S.SplitTunnel.Packages.Count));

    [RelayCommand]
    private static Task ChooseAppsAsync() => Shell.Current.GoToAsync("apps");

    public bool SmartMode
    {
        get => S.Connection.SmartMode;
        set => _s.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = value } });
    }

    // ------------------------------------------------------------------ connection

    public bool AutoTune
    {
        get => S.Performance.AutoTune;
        set => _s.Settings.Update(x => x with { Performance = x.Performance with { AutoTune = value } });
    }

    public bool FastDns
    {
        get => S.Dns.FakeIpEnabled;
        set => _s.Settings.Update(x => x with { Dns = x.Dns with { FakeIpEnabled = value } });
    }

    public bool BypassLan
    {
        get => S.Routing.BypassLan;
        set => _s.Settings.Update(x => x with { Routing = x.Routing with { BypassLan = value } });
    }

    public string FingerprintValue => Fingerprints.FirstOrDefault(f => f.Value == S.Performance.DefaultFingerprint).Label ?? L.Get("Set_Off");

    [RelayCommand]
    private async Task ChooseFingerprintAsync()
    {
        var options = Fingerprints.Select(f => new SheetOption<string>(f.Label, f.Value)).Append(new SheetOption<string>(L.Get("Set_Off"), string.Empty)).ToList();
        var v = await Sheets.PickAsync(L.Get("Set_Fingerprint"), options, S.Performance.DefaultFingerprint ?? string.Empty);
        _s.Settings.Update(x => x with { Performance = x.Performance with { DefaultFingerprint = v } });
    }

    public IReadOnlyList<string> QuicOptions => [L.Get("Set_Auto"), L.Get("Set_Block"), L.Get("Set_Allow")];

    public int QuicIndex
    {
        get => (int)S.Performance.Quic;
        set => _s.Settings.Update(x => x with { Performance = x.Performance with { Quic = (QuicPolicy)Math.Clamp(value, 0, 2) } });
    }

    public IReadOnlyList<string> Ipv6Options => [L.Get("Set_Auto"), L.Get("Set_On"), L.Get("Set_Off")];

    public int Ipv6Index
    {
        get => (int)S.Ipv6;
        set => _s.Settings.Update(x => x with { Ipv6 = (Ipv6Mode)Math.Clamp(value, 0, 2) });
    }

    public string RoutingValue => L.Get(S.Routing.Preset switch
    {
        RoutingPreset.IranFriendly => "Set_RouteIran",
        RoutingPreset.Rule => "Set_RouteRule",
        RoutingPreset.Global => "Set_RouteGlobal",
        _ => "Set_RouteCustom",
    });

    [RelayCommand]
    private async Task ChooseRoutingAsync()
    {
        var v = await Sheets.PickAsync(L.Get("Set_Routing"),
            [new(L.Get("Set_RouteIran"), RoutingPreset.IranFriendly, L.Get("Set_RouteIranHint"), "route"),
             new(L.Get("Set_RouteRule"), RoutingPreset.Rule, L.Get("Set_RouteRuleHint"), "route"),
             new(L.Get("Set_RouteGlobal"), RoutingPreset.Global, L.Get("Set_RouteGlobalHint"), "globe")],
            S.Routing.Preset);
        if (v == S.Routing.Preset)
        {
            return;
        }

        var built = RoutingPresets.Build(v);
        _s.Settings.Update(x => x with { Routing = x.Routing with { Preset = v, Rules = built.Rules, RuleSets = built.RuleSets, Final = built.Final } });
    }

    // ------------------------------------------------------------------ gaming & streaming

    public string ActivityValue => L.Get(S.Performance.Activity switch
    {
        ActivityOptimization.Off => "Set_ActOff",
        ActivityOptimization.AlwaysGaming => "Set_ActGaming",
        ActivityOptimization.AlwaysStreaming => "Set_ActStreaming",
        _ => "Set_ActAuto",
    });

    [RelayCommand]
    private async Task ChooseActivityAsync()
    {
        var v = await Sheets.PickAsync(L.Get("Set_Activity"),
            [new(L.Get("Set_ActAuto"), ActivityOptimization.Auto, L.Get("Set_ActAutoHint"), "sparkle"),
             new(L.Get("Set_ActGaming"), ActivityOptimization.AlwaysGaming, null, "gamepad"),
             new(L.Get("Set_ActStreaming"), ActivityOptimization.AlwaysStreaming, null, "tv"),
             new(L.Get("Set_ActOff"), ActivityOptimization.Off, null, "x")],
            S.Performance.Activity);
        _s.Settings.Update(x => x with { Performance = x.Performance with { Activity = v } });
    }

    /// <summary>Games are recognised from the app in front (needs Android "usage access"); streams from traffic.</summary>
    public bool DetectGames
    {
        get => S.Ui.DetectGames && Platforms.Android.ActivityMonitor.HasUsageAccess();
        set
        {
            if (value && !Platforms.Android.ActivityMonitor.HasUsageAccess())
            {
                _ = AskUsageAccessAsync();
                return;
            }

            _s.Settings.Update(x => x with { Ui = x.Ui with { DetectGames = value } });
        }
    }

    private async Task AskUsageAccessAsync()
    {
        if (await Shell.Current.DisplayAlertAsync(L.Get("Set_DetectGames"), L.Get("Set_UsageAccessExplain"), L.Get("Set_OpenSettings"), L.Get("Cancel")))
        {
            _s.Settings.Update(x => x with { Ui = x.Ui with { DetectGames = true } });
            Platforms.Android.ActivityMonitor.OpenUsageAccessSettings();
        }

        OnPropertyChanged(nameof(DetectGames));
    }

    // ------------------------------------------------------------------ proxy-only

    public string ProxyAddress => $"127.0.0.1:{S.Connection.LocalProxyPort}";

    public string ProxyUsername => S.MobileProxy.Username ?? string.Empty;

    public string ProxyPassword => S.MobileProxy.Password ?? string.Empty;

    public bool ProxyRequiresPassword
    {
        get => S.MobileProxy.RequirePassword;
        set
        {
            if (value == S.MobileProxy.RequirePassword)
            {
                return;
            }

            if (!value)
            {
                _ = ConfirmOpenProxyAsync();
                return;
            }

            _s.Settings.Update(x => x with { MobileProxy = x.MobileProxy with { RequirePassword = true } });
        }
    }

    private async Task ConfirmOpenProxyAsync()
    {
        var ok = await Shell.Current.DisplayAlertAsync(L.Get("Set_ProxyNoPassTitle"), L.Get("Set_ProxyNoPassText"), L.Get("Set_TurnOff"), L.Get("Set_KeepPassword"));
        if (ok)
        {
            _s.Settings.Update(x => x with { MobileProxy = x.MobileProxy with { RequirePassword = false } });
        }
        else
        {
            OnPropertyChanged(nameof(ProxyRequiresPassword));
        }
    }

    [RelayCommand]
    private async Task CopyProxyAsync()
    {
        var text = S.MobileProxy.RequirePassword
            ? $"SOCKS5/HTTP {ProxyAddress}  user: {ProxyUsername}  password: {ProxyPassword}"
            : $"SOCKS5/HTTP {ProxyAddress}";
        await Clipboard.Default.SetTextAsync(text);
    }

    // ------------------------------------------------------------------ shortcuts

    [RelayCommand]
    private static async Task AddTileAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity is not null && Platforms.Android.JackiTileService.RequestAdd(activity, msg => MainThread.BeginInvokeOnMainThread(() => _ = Shell.Current.DisplayAlertAsync(L.Get("Set_Tile"), msg, L.Get("Ok")))))
        {
            return;
        }

        await Shell.Current.DisplayAlertAsync(L.Get("Set_Tile"), L.Get("Set_TileManual"), L.Get("Ok"));
    }

    [RelayCommand]
    private static async Task AddWidgetAsync()
    {
        if (!Platforms.Android.JackiWidget.RequestPin(global::Android.App.Application.Context))
        {
            await Shell.Current.DisplayAlertAsync(L.Get("Set_Widget"), L.Get("Set_WidgetManual"), L.Get("Ok"));
        }
    }

    [RelayCommand]
    private static Task SyncDeviceAsync() => Shell.Current.GoToAsync("ecosystem");

    [RelayCommand]
    private static Task OpenUsageAsync() => Shell.Current.GoToAsync("usage");

    [RelayCommand]
    private static Task OpenIslandAsync() => Shell.Current.GoToAsync("island");

    [RelayCommand]
    private static void OpenVpnSettings()
    {
        var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionVpnSettings)
            .AddFlags(global::Android.Content.ActivityFlags.NewTask);
        global::Android.App.Application.Context.StartActivity(intent);
    }

    // ------------------------------------------------------------------ updates

    public string Version => L.N($"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})");

    public bool AutoCheckUpdates
    {
        get => S.Updates.AutoCheck;
        set => _s.Settings.Update(x => x with { Updates = x.Updates with { AutoCheck = value } });
    }

    public bool HasUpdate => _s.Updates.Available is not null;

    public string UpdateTitle => _s.Updates.Available is { } a ? L.N(L.F("Upd_Available", a.VersionText)) : L.Get("Upd_UpToDate");

    public string UpdateNotes => _s.Updates.Available?.Notes(L.IsPersian) ?? string.Empty;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        UpdateStatus = L.Get("Upd_Checking");
        try
        {
            var u = await Task.Run(() => _s.Updates.CheckNowAsync(_s.Lifetime));
            UpdateStatus = u is null ? (_s.Updates.LastError ?? L.Get("Upd_UpToDate")) : null;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_s.Updates.Available is not { } update || IsDownloadingUpdate)
        {
            return;
        }

        IsDownloadingUpdate = true;
        UpdateStatus = L.Get("Upd_Installing");
        try
        {
            // The browser downloads the APK and Android installs it. The app asks for no install permission (one of
            // the signals Play Protect weighs for apps from outside the store); Android itself only accepts the
            // update when it is signed with the same key as the installed app. The URL comes from the signed feed.
            await Launcher.Default.OpenAsync(new Uri(update.Asset.Url));
        }
        catch (Exception ex) when (ex is AppUpdateException or IOException or OperationCanceledException or UnauthorizedAccessException or UriFormatException)
        {
            UpdateStatus = SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    // ------------------------------------------------------------------ about

    [RelayCommand]
    private async Task ShareLogsAsync()
    {
        var latest = Directory.Exists(_s.LogsDir)
            ? new DirectoryInfo(_s.LogsDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null)
        {
            await Shell.Current.DisplayAlertAsync(L.Get("Set_Logs"), L.Get("Set_NoLogs"), L.Get("Ok"));
            return;
        }

        // Logs are redacted when written (no UUIDs, passwords, keys or subscription tokens).
        var copy = Path.Combine(FileSystem.CacheDirectory, "jackivpn-log.txt");
        File.Copy(latest.FullName, copy, overwrite: true);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = "IBOX log", File = new ShareFile(copy, "text/plain") });
    }

    [RelayCommand]
    private static Task LicensesAsync() => Shell.Current.DisplayAlertAsync(
        L.Get("Set_Licenses"),
        "sing-box 1.14.2 — GPL-3.0-or-later. github.com/SagerNet/sing-box (tag v1.14.2). Unmodified official binary.\n\n" +
        "Xray-core 26.3.27 — MPL-2.0. github.com/XTLS/Xray-core (tag v26.3.27). Unmodified official binary.\n\n" +
        "hev-socks5-tunnel 2.18.0 — MIT. github.com/heiher/hev-socks5-tunnel\n\n" +
        "Rule-sets: SagerNet sing-geoip / sing-geosite (GPL-3.0 / CC BY-SA).\n\n" +
        ".NET MAUI, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, YamlDotNet — MIT. SQLitePCLRaw — Apache-2.0. Open Sans — SIL OFL 1.1.",
        L.Get("Ok"));
}
