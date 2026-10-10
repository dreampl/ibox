using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Net;
using Android.OS;
using Android.Service.QuickSettings;
using Android.Widget;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using JackiVpn.Mobile.Services;
using JackiVpn.Mobile.ViewModels;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>Keeps the home-screen widget and the quick-settings tile in step with the connection.</summary>
public static class StatusSurfaces
{
    /// <summary>Live numbers (ping, speed) refresh the widget at most this often (battery).</summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(10);
    private static ConnectionStatus _last = new();
    private static long _lastStatsTicks;

    public static ConnectionStatus Last => _last;

    public static void Update(Context context, ConnectionStatus status)
    {
        if (status.Activity != _last.Activity || status.State != _last.State)
        {
            var tag = status.State == ConnectionState.Connected ? status.Activity switch
            {
                ActivityProfile.Gaming => "🎮 " + Localization.L.Get("Act_Gaming"),
                ActivityProfile.Streaming => "📺 " + Localization.L.Get("Act_Streaming"),
                _ => null,
            } : null;
            JackiVpnService.Current?.SetActivityTag(tag);
        }

        var significant = _last.State != status.State || _last.ExitCountryName != status.ExitCountryName || _last.ProfileName != status.ProfileName || _last.Activity != status.Activity;
        _last = status;
        var now = System.Environment.TickCount64;
        if (!significant && (status.State != ConnectionState.Connected || now - _lastStatsTicks < StatsInterval.TotalMilliseconds))
        {
            return;
        }

        _lastStatsTicks = now;
        try
        {
            JackiWidget.Refresh(context);
            if (significant)
            {
                TileService.RequestListeningState(context, new ComponentName(context, Java.Lang.Class.FromType(typeof(JackiTileService))));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MobileServices.Instance?.Logger.LogDebug("Widget/tile refresh failed: {Error}", ex.Message);
        }
    }

    public static string Title(ConnectionStatus st) => st.State switch
    {
        ConnectionState.Connected => string.IsNullOrWhiteSpace(st.ExitCountryName) ? "Connected" : Flag(st.ExitCountryCode) + " " + st.ExitCountryName,
        ConnectionState.Disconnected => "Not connected",
        ConnectionState.Faulted => "Connection failed",
        _ => "Connecting…",
    };

    public static string Describe(ConnectionStatus st) => st.State switch
    {
        ConnectionState.Connected => string.Join(" · ", new[] { st.Mode == ConnectionMode.SystemProxy ? "Proxy only" : "Protected", st.ProfileName }.Where(x => !string.IsNullOrWhiteSpace(x))),
        ConnectionState.Disconnected => "Tap Connect - smart mode picks the fastest server",
        ConnectionState.Faulted => st.LastError ?? "Tap Connect to try again",
        _ => "Finding the fastest server…",
    };

    public static string PingText(ConnectionStatus st) =>
        st.LatencyMs is { } ms ? $"Ping {ms:0} ms" + (st.JitterMs is { } j && j >= 1 ? $"  ±{j:0}" : string.Empty) : "Ping: measuring…";

    public static string Flag(string? code) =>
        code is { Length: 2 } c && char.IsAsciiLetter(c[0]) && char.IsAsciiLetter(c[1])
            ? string.Concat(c.ToUpperInvariant().Select(ch => char.ConvertFromUtf32(0x1F1E6 + ch - 'A')))
            : string.Empty;
}

/// <summary>One-tap actions from the widget and the tile (VPN permission must have been granted once).</summary>
public static class QuickToggle
{
    public const string ActionToggle = "com.jacki.vpn.action.TOGGLE";
    public const string ActionFastest = "com.jacki.vpn.action.FASTEST";
    public const string RouteExtra = "jacki_route";

    public static async Task ToggleAsync(Context context)
    {
        var app = MobileServices.Resolve();
        if (app is null)
        {
            return;
        }

        await app.InitializeAsync().ConfigureAwait(false);
        if (!ConnectionStateRules.IsIdle(app.Orchestrator.State))
        {
            await app.Orchestrator.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        await ConnectAsync(context, app, new ConnectRequest()).ConfigureAwait(false);
    }

    /// <summary>Re-runs smart selection: reconnects to the fastest server that works right now.</summary>
    public static async Task FastestAsync(Context context)
    {
        var app = MobileServices.Resolve();
        if (app is null)
        {
            return;
        }

        await app.InitializeAsync().ConfigureAwait(false);
        if (!ConnectionStateRules.IsIdle(app.Orchestrator.State))
        {
            await app.Orchestrator.DisconnectAsync().ConfigureAwait(false);
        }

        await ConnectAsync(context, app, new ConnectRequest { Smart = true }).ConfigureAwait(false);
    }

    private static async Task ConnectAsync(Context context, MobileServices app, ConnectRequest request)
    {
        if (app.Settings.Current.Connection.Mode == ConnectionMode.Tun && VpnService.Prepare(context) is not null)
        {
            OpenAppIntent(context).Send(); // the VPN consent dialog needs an activity
            return;
        }

        await app.Orchestrator.ConnectAsync(request, app.Lifetime).ConfigureAwait(false);
    }

    public static PendingIntent OpenAppIntent(Context context, string? route = null)
    {
        var intent = new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        if (route is not null)
        {
            intent.PutExtra(RouteExtra, route);
        }

        return PendingIntent.GetActivity(context, route is null ? 10 : 12, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>Widget buttons go to a non-exported receiver: no other app can switch the VPN through them.</summary>
    public static PendingIntent Action(Context context, string action, int requestCode) =>
        PendingIntent.GetBroadcast(context, requestCode, new Intent(context, typeof(JackiToggleReceiver)).SetAction(action), PendingIntentFlags.Immutable)!;
}

/// <summary>
/// Home-screen widget. Large (4x2): status with a live pulsing dot or a spinner, a running session timer, rotating
/// stats (ping, speed, data), Connect/Disconnect, fastest server and the server list. Small (2x1/4x1): dot, status
/// and a round power button. The size picks the layout (Android 12+ switches automatically while resizing).
/// </summary>
[BroadcastReceiver(Name = "com.jacki.vpn.JackiWidget", Label = "IBOX", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/jacki_widget_info")]
public sealed class JackiWidget : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? manager, int[]? ids)
    {
        if (context is not null)
        {
            Refresh(context);
        }
    }

    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? manager, int appWidgetId, Bundle? newOptions)
    {
        base.OnAppWidgetOptionsChanged(context, manager, appWidgetId, newOptions);
        if (context is not null)
        {
            Refresh(context);
        }
    }

    public static void Refresh(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        var ids = manager?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(JackiWidget))));
        if (manager is null || ids is null || ids.Length == 0)
        {
            return;
        }

        var st = (MobileServices.Instance?.Orchestrator.Status) ?? StatusSurfaces.Last;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var responsive = new RemoteViews(new Dictionary<global::Android.Util.SizeF, RemoteViews>
            {
                [new global::Android.Util.SizeF(110, 40)] = Build(context, st, full: false),
                [new global::Android.Util.SizeF(200, 120)] = Build(context, st, full: true),
            });
            manager.UpdateAppWidget(ids, responsive);
            return;
        }

        foreach (var id in ids)
        {
            var height = manager.GetAppWidgetOptions(id)?.GetInt(AppWidgetManager.OptionAppwidgetMinHeight) ?? 110;
            manager.UpdateAppWidget(id, Build(context, st, full: height == 0 || height >= 100));
        }
    }

    private static RemoteViews Build(Context context, ConnectionStatus st, bool full)
    {
        var connected = st.State == ConnectionState.Connected;
        var idle = ConnectionStateRules.IsIdle(st.State);
        var busy = !connected && !idle;
        var views = new RemoteViews(context.PackageName, full ? Resource.Layout.jacki_widget : Resource.Layout.jacki_widget_small);

        views.SetInt(Resource.Id.widget_root, "setBackgroundResource", connected ? Resource.Drawable.widget_bg_on : busy ? Resource.Drawable.widget_bg_busy : Resource.Drawable.widget_bg);
        views.SetViewVisibility(Resource.Id.widget_dot_live, connected ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone);
        views.SetViewVisibility(Resource.Id.widget_spinner, busy ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone);
        views.SetViewVisibility(Resource.Id.widget_dot_idle, idle ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone);
        views.SetTextViewText(Resource.Id.widget_state, full ? StatusSurfaces.Title(st) : connected ? StatusSurfaces.Title(st) : "IBOX");
        views.SetTextViewText(Resource.Id.widget_detail, full ? StatusSurfaces.Describe(st) : connected ? StatusSurfaces.PingText(st) : StatusSurfaces.Title(st));

        var toggle = QuickToggle.Action(context, QuickToggle.ActionToggle, 11);
        views.SetOnClickPendingIntent(Resource.Id.widget_toggle, toggle);
        views.SetOnClickPendingIntent(Resource.Id.widget_root, QuickToggle.OpenAppIntent(context));

        if (!full)
        {
            views.SetInt(Resource.Id.widget_toggle, "setBackgroundResource", connected ? Resource.Drawable.widget_circle_on : Resource.Drawable.widget_circle_off);
            return views;
        }

        views.SetTextViewText(Resource.Id.widget_toggle, connected ? "Disconnect" : busy ? "Cancel" : "Connect");
        views.SetInt(Resource.Id.widget_toggle, "setBackgroundResource", idle ? Resource.Drawable.widget_button_off : Resource.Drawable.widget_button_on);
        views.SetOnClickPendingIntent(Resource.Id.widget_best, QuickToggle.Action(context, QuickToggle.ActionFastest, 13));
        views.SetOnClickPendingIntent(Resource.Id.widget_servers, QuickToggle.OpenAppIntent(context, "servers"));

        views.SetViewVisibility(Resource.Id.widget_stats, connected ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone);
        views.SetViewVisibility(Resource.Id.widget_timer, connected && st.ConnectedSinceUtc is not null ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone);
        if (connected)
        {
            if (st.ConnectedSinceUtc is { } since)
            {
                var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - since).TotalMilliseconds);
                views.SetChronometer(Resource.Id.widget_timer, SystemClock.ElapsedRealtime() - elapsed, null, true);
            }

            var t = st.Traffic;
            views.SetTextViewText(Resource.Id.widget_ping, st.LatencyMs is { } ms ? ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "…");
            views.SetTextViewText(Resource.Id.widget_down, t is null ? "0 B/s" : Fmt.Speed(t.DownloadBytesPerSecond));
            views.SetTextViewText(Resource.Id.widget_up, t is null ? "0 B/s" : Fmt.Speed(t.UploadBytesPerSecond));
            views.SetTextViewText(Resource.Id.widget_stat_ping, t is null ? "This session: 0 B" : $"This session: {Fmt.Bytes(t.DownloadBytesTotal + t.UploadBytesTotal)}");
            views.SetTextViewText(Resource.Id.widget_stat_speed, string.IsNullOrEmpty(st.PublicIp) ? "Server: " + (st.ProfileName ?? "-") : "Exit IP: " + st.PublicIp);
            views.SetTextViewText(Resource.Id.widget_stat_data, "⚡ switches to the fastest server now");
        }
        else
        {
            views.SetTextViewText(Resource.Id.widget_stat_ping, "⚡ connects to the fastest server");
            views.SetTextViewText(Resource.Id.widget_stat_speed, "☰ opens your server list");
            views.SetTextViewText(Resource.Id.widget_stat_data, "Long-press the widget to resize it");
        }
        return views;
    }

    /// <summary>Asks the launcher to place the widget (Android 8+, launchers that support pinning).</summary>
    public static bool RequestPin(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null || !manager.IsRequestPinAppWidgetSupported)
        {
            return false;
        }

        return manager.RequestPinAppWidget(new ComponentName(context, Java.Lang.Class.FromType(typeof(JackiWidget))), null, null);
    }
}

[BroadcastReceiver(Name = "com.jacki.vpn.JackiToggleReceiver", Exported = false)]
public sealed class JackiToggleReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        var app = context.ApplicationContext!;
        var fastest = intent?.Action == QuickToggle.ActionFastest;
        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                if (fastest)
                {
                    await QuickToggle.FastestAsync(app).ConfigureAwait(false);
                }
                else
                {
                    await QuickToggle.ToggleAsync(app).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MobileServices.Instance?.Logger.LogWarning("Widget action failed: {Error}", Security.SecretRedactor.Redact(ex.Message));
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}

/// <summary>
/// Quick-settings tile (the on/off button in the swipe-down control panel): tap to connect or disconnect; it shows
/// the exit country and ping, and a long press opens the app.
/// </summary>
[Service(Name = "com.jacki.vpn.JackiTileService", Label = "IBOX", Icon = "@drawable/ic_stat_jacki",
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE", Exported = true)]
[IntentFilter([TileService.ActionQsTile])]
[MetaData("android.service.quicksettings.TOGGLEABLE_TILE", Value = "true")]
public sealed class JackiTileService : TileService
{
    public override void OnStartListening()
    {
        base.OnStartListening();
        Render();
    }

    public override void OnClick()
    {
        base.OnClick();
        var context = ApplicationContext!;
        var needsConsent = MobileServices.Resolve() is { } app
            && ConnectionStateRules.IsIdle(app.Orchestrator.State)
            && app.Settings.Current.Connection.Mode == ConnectionMode.Tun
            && VpnService.Prepare(context) is not null;
        if (needsConsent)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                StartActivityAndCollapse(QuickToggle.OpenAppIntent(context));
            }
            else
            {
#pragma warning disable CA1422 // the Intent overload is the only one before Android 14
                StartActivityAndCollapse(new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask));
#pragma warning restore CA1422
            }

            return;
        }

        // Immediate feedback while the connection is being made.
        if (QsTile is { } tile && MobileServices.Resolve() is { } a && ConnectionStateRules.IsIdle(a.Orchestrator.State))
        {
            tile.State = TileState.Active;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                tile.Subtitle = "Connecting…";
            }

            tile.UpdateTile();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await QuickToggle.ToggleAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MobileServices.Instance?.Logger.LogWarning("Tile action failed: {Error}", Security.SecretRedactor.Redact(ex.Message));
            }

            Render();
        });
    }

    private void Render()
    {
        if (QsTile is not { } tile)
        {
            return;
        }

        var st = (MobileServices.Instance?.Orchestrator.Status) ?? StatusSurfaces.Last;
        var connected = st.State == ConnectionState.Connected;
        tile.State = connected || !ConnectionStateRules.IsIdle(st.State) ? TileState.Active : TileState.Inactive;
        tile.Label = "IBOX";
        tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_stat_jacki);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            tile.Subtitle = connected
                ? string.Join(" · ", new[] { st.ExitCountryName, st.LatencyMs is { } ms ? $"{ms:0} ms" : null }.Where(x => !string.IsNullOrEmpty(x))) is { Length: > 0 } s ? s : "On"
                : ConnectionStateRules.IsIdle(st.State) ? "Off" : "Connecting…";
        }

        tile.UpdateTile();
    }

    /// <summary>Android 13+: asks the system to add the tile to the control panel (one tap for the user).</summary>
    public static bool RequestAdd(Activity activity, Action<string> result)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || activity.GetSystemService(StatusBarService) is not StatusBarManager bar)
        {
            return false;
        }

        bar.RequestAddTileService(new ComponentName(activity, Java.Lang.Class.FromType(typeof(JackiTileService))), "IBOX",
            Icon.CreateWithResource(activity, Resource.Drawable.ic_stat_jacki)!, activity.MainExecutor!, new TileAnswer(result));
        return true;
    }

    private sealed class TileAnswer(Action<string> result) : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        public void Accept(Java.Lang.Object? t)
        {
            var code = t is Java.Lang.Integer i ? i.IntValue() : -1;
            result(code switch
            {
                2 => "Added to Quick Settings. Swipe down twice to see it.", // TILE_ADD_REQUEST_RESULT_TILE_ADDED
                1 => "IBOX is already in Quick Settings.",              // TILE_ADD_REQUEST_RESULT_TILE_ALREADY_ADDED
                0 => "Not added.",                                            // TILE_ADD_REQUEST_RESULT_TILE_NOT_ADDED
                _ => "Android could not add the tile; add it manually: swipe down twice, tap the pencil and drag IBOX in.",
            });
        }
    }
}
