using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Runtime;
using JackiVpn.Domain.Settings;
using JackiVpn.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.Platforms.Android;

/// <param name="SplitPackages">Sorted, comma-joined package names (a string, so equal options compare equal).</param>
public sealed record TunnelOptions(int SocksPort, string SocksUser, string SocksPassword, int Mtu, bool BypassLan, SplitTunnelMode SplitMode, string SplitPackages)
{
    public static string JoinPackages(IEnumerable<string> packages) =>
        string.Join(',', packages.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>Same interface (addresses, routes, per-app rules): only tun2socks needs restarting.</summary>
    public bool SameInterfaceAs(TunnelOptions other) =>
        Mtu == other.Mtu && BypassLan == other.BypassLan && SplitMode == other.SplitMode && SplitPackages == other.SplitPackages;

    /// <summary>Never prints the SOCKS password.</summary>
    public override string ToString() => $"TunnelOptions {{ SocksPort = {SocksPort}, Mtu = {Mtu}, BypassLan = {BypassLan}, Split = {SplitMode} }}";
}

/// <summary>
/// The Android VPN: owns the TUN interface (all device traffic, IPv4 and IPv6, so nothing can leak around it) and
/// the in-process tun2socks that feeds it to the local sing-box inbound. The app's own UID — including the
/// sing-box process it starts — is never routed into the VPN, so the core's connections to the server can never
/// loop back into the tunnel. Per-app routing (all / all except / only selected) is applied by Android itself.
/// Runs as a foreground service with an ongoing (black) notification; in proxy-only mode it only keeps the
/// process alive in the background and opens no tunnel.
/// </summary>
[Register("com.jacki.vpn.JackiVpnService")]
public sealed class JackiVpnService : VpnService
{
    public const string ActionStart = "com.jacki.vpn.START";
    public const string ActionStop = "com.jacki.vpn.STOP";
    public const string ActionDisconnect = "com.jacki.vpn.DISCONNECT";
    private const int NotificationId = 1;
    private const string ChannelId = "vpn";
    public const string TunIpv4 = "172.19.0.1";
    public const string TunIpv6 = "fdfe:dcba:9876::1";
    public const string TunDns = "172.19.0.2";

    private static readonly Lock StaticGate = new();
    private static TaskCompletionSource<JackiVpnService>? _ready;
    private readonly Lock _gate = new();
    private ParcelFileDescriptor? _tun;
    private TunnelOptions? _options;
    private string _title = "Connecting…";
    private string _text = "Starting";

    public static JackiVpnService? Current { get; private set; }

    /// <summary>Raised when Android revokes the VPN (another VPN app took over, or the user disabled it).</summary>
    public static event EventHandler? Revoked;

    public bool IsTunnelOpen
    {
        get
        {
            lock (_gate)
            {
                return _tun is not null;
            }
        }
    }

    public static async Task<JackiVpnService> EnsureStartedAsync(Context context, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        TaskCompletionSource<JackiVpnService> tcs;
        lock (StaticGate)
        {
            if (Current is { } running)
            {
                return running;
            }

            tcs = _ready ??= new TaskCompletionSource<JackiVpnService>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var intent = new Intent(context, typeof(JackiVpnService)).SetAction(ActionStart);
        try
        {
            context.StartForegroundService(intent);
        }
        catch (Java.Lang.IllegalStateException ex)
        {
            // Android 12+ refuses to start a foreground service while the app is in the background
            // (e.g. an automatic reconnect with the app closed). Report it instead of crashing.
            lock (StaticGate)
            {
                _ready = null;
            }

            throw new InvalidOperationException("Android did not allow starting the VPN from the background. Open IBOX to connect.", ex);
        }
        return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            CloseTunnel();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        StartForegroundCompat(BuildNotification(_title, _text));
        lock (StaticGate)
        {
            Current = this;
            _ready?.TrySetResult(this);
            _ready = null;
        }

        if (intent?.Action == ActionDisconnect)
        {
            // The notification's "Disconnect" button: a proper disconnect (core, tunnel, history), not just a kill.
            _ = Task.Run(async () =>
            {
                if (MobileServices.Resolve() is { } app)
                {
                    await app.Orchestrator.DisconnectAsync().ConfigureAwait(false);
                }
            });
            return StartCommandResult.NotSticky;
        }

        if (intent is null || intent.Action == ServiceInterface)
        {
            // Started by the system (Always-on VPN / boot): connect with the user's saved choice.
            _ = Task.Run(async () =>
            {
                try
                {
                    var app = MobileServices.Resolve();
                    if (app is not null && Domain.Connection.ConnectionStateRules.IsIdle(app.Orchestrator.State))
                    {
                        await app.InitializeAsync().ConfigureAwait(false);
                        await app.Orchestrator.ConnectAsync(new Application.Connection.ConnectRequest(), app.Lifetime).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    MobileServices.Instance?.Logger.LogError("Always-on connect failed: {Error}", Security.SecretRedactor.Redact(ex.Message));
                }
            });
        }

        return StartCommandResult.Sticky;
    }

    /// <summary>Creates (or reuses) the TUN and (re)starts tun2socks towards the given SOCKS port.</summary>
    public void OpenTunnel(TunnelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_tun is not null && _options == options && HevTunnel.IsRunning())
            {
                return; // server switch on the same port: the tunnel keeps running, nothing leaks in between
            }

            if (_tun is not null && _options is not null && _options.SameInterfaceAs(options))
            {
                // Only the SOCKS endpoint changed (new core): restart tun2socks on the existing interface.
                HevTunnel.Stop();
                StartHev(options);
                _options = options;
                return;
            }

            CloseTunnelLocked();
            var builder = new Builder(this)
                .SetSession("IBOX")!
                .SetMtu(options.Mtu)!
                .AddAddress(TunIpv4, 30)!
                .AddRoute("0.0.0.0", 0)!
                .AddDnsServer(TunDns)!
                // IPv6 is always captured: whether it is carried or refused is decided inside the core, but it never
                // bypasses the VPN on dual-stack networks.
                .AddAddress(TunIpv6, 126)!
                .AddRoute("::", 0)!
                .SetBlocking(false)!
                .SetMetered(false)!;
            ApplyPerAppRouting(builder, options);
            if (options.BypassLan && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                foreach (var (address, prefix) in new[] { ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16) })
                {
                    builder.ExcludeRoute(new IpPrefix(Java.Net.InetAddress.GetByName(address)!, prefix));
                }
            }

            _tun = builder.Establish() ?? throw new InvalidOperationException("Android refused to create the VPN interface (VPN permission missing?).");
            StartHev(options);
            _options = options;
        }
    }

    /// <summary>
    /// "Only selected": just those apps use the VPN (this app is never among them). "All except selected" and
    /// "All apps": everything except this app (and the excluded ones). Android cannot mix the two lists, and an
    /// allow-list that ends up empty would route every app — including this one — so it falls back to all apps.
    /// </summary>
    private void ApplyPerAppRouting(Builder builder, TunnelOptions options)
    {
        var own = PackageName!;
        var packages = options.SplitPackages.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(p => p != own).ToArray();
        if (options.SplitMode == SplitTunnelMode.OnlySelected)
        {
            var allowed = 0;
            foreach (var p in packages)
            {
                try
                {
                    builder.AddAllowedApplication(p);
                    allowed++;
                }
                catch (PackageManager.NameNotFoundException)
                {
                    MobileServices.Instance?.Logger.LogInformation("Per-app VPN: {Package} is not installed; skipped.", p);
                }
            }

            if (allowed > 0)
            {
                return;
            }

            MobileServices.Instance?.Logger.LogWarning("Per-app VPN: none of the selected apps is installed; routing all apps instead.");
        }

        builder.AddDisallowedApplication(own);
        if (options.SplitMode == SplitTunnelMode.ExcludeSelected)
        {
            foreach (var p in packages)
            {
                try
                {
                    builder.AddDisallowedApplication(p);
                }
                catch (PackageManager.NameNotFoundException)
                {
                    MobileServices.Instance?.Logger.LogInformation("Per-app VPN: {Package} is not installed; skipped.", p);
                }
            }
        }
    }

    private void StartHev(TunnelOptions options)
    {
        var config = Path.Combine(FilesDir!.AbsolutePath, "tun2socks.yml");
        // App-private storage: other apps cannot read the session's SOCKS credentials.
        File.WriteAllText(config, HevTunnel.Config(options.SocksPort, options.SocksUser, options.SocksPassword, options.Mtu, TunIpv4, TunIpv6));
        if (!HevTunnel.Start(config, _tun!.Fd))
        {
            throw new InvalidOperationException("tun2socks failed to start.");
        }
    }

    public void CloseTunnel()
    {
        lock (_gate)
        {
            CloseTunnelLocked();
        }
    }

    private void CloseTunnelLocked()
    {
        try
        {
            if (HevTunnel.IsRunning())
            {
                HevTunnel.Stop();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MobileServices.Instance?.Logger.LogWarning("tun2socks stop failed: {Error}", ex.Message);
        }

        _tun?.Close();
        _tun?.Dispose();
        _tun = null;
        _options = null;
    }

    private string? _tag;

    /// <summary>"🎮 Gaming mode" / "📺 Streaming mode" in the notification header while that mode is active.</summary>
    public void SetActivityTag(string? tag)
    {
        if (tag == _tag)
        {
            return;
        }

        _tag = tag;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(_title, _text));
    }

    public void UpdateNotification(string title, string text)
    {
        _title = title;
        _text = text;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(title, text));
    }

    public static void Stop(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Current is null)
        {
            return;
        }

        try
        {
            context.StartService(new Intent(context, typeof(JackiVpnService)).SetAction(ActionStop));
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Background start restrictions: close the tunnel directly instead.
            Current?.CloseTunnel();
            Current?.StopSelf();
        }
    }

    public override void OnRevoke()
    {
        CloseTunnel();
        Revoked?.Invoke(this, EventArgs.Empty);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        CloseTunnel();
        lock (StaticGate)
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }

        base.OnDestroy();
    }

    private void StartForegroundCompat(Notification notification)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    /// <summary>Black (colorized foreground-service notification), with a one-tap Disconnect.</summary>
    private Notification BuildNotification(string title, string text)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is null)
        {
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "VPN status", NotificationImportance.Low) { Description = "Shows that IBOX is active." });
        }

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop), PendingIntentFlags.Immutable);
        var disconnect = PendingIntent.GetService(this, 1, new Intent(this, typeof(JackiVpnService)).SetAction(ActionDisconnect), PendingIntentFlags.Immutable);
        var action = new Notification.Action.Builder(global::Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.ic_stat_jacki), Localization.L.Get("Notif_Disconnect"), disconnect).Build();
        var builder = new Notification.Builder(this, ChannelId);
        if (_tag is { } tag)
        {
            builder.SetSubText(tag);
        }

        return builder
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetSmallIcon(Resource.Drawable.ic_stat_jacki)!
            .SetColor(global::Android.Graphics.Color.Black)!
            .SetColorized(true)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(open)!
            .AddAction(action)!
            .Build()!;
    }
}
