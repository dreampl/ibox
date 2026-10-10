using Android.App;
using Android.App.Usage;
using Android.Content;
using Android.Content.PM;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// Gaming / streaming detection on the phone (only while connected, every 5 s, on-device only):
/// <list type="bullet">
/// <item>Streaming: sustained download (≥ 190 KB/s in 4 of the last 6 readings), the same rule as Windows.</item>
/// <item>Gaming: the app in front is a game (Android's app category, or a known game). This needs the optional
/// "usage access" permission; without it only streaming is recognised.</item>
/// </list>
/// The orchestrator debounces readings, pauses optional work while gaming/streaming, and the status (power
/// button colour, the tag on Home, the notification) shows the active mode.
/// </summary>
public sealed class ActivityMonitor
{
    private const long StreamingBytesPerSecond = 190 * 1024;
    private static readonly string[] KnownGames = ["com.tencent.ig", "com.pubg.imobile", "com.activision.callofduty.shooter", "com.garena.game", "com.supercell.", "com.miHoYo.", "com.HoYoverse.", "com.mojang.minecraftpe", "com.roblox.client", "com.ea.gp.", "com.dts.freefire", "com.riotgames.", "com.netease."];
    private readonly MobileServices _s;
    private readonly Queue<bool> _streaming = new();
    private CancellationTokenSource? _cts;

    public ActivityMonitor(MobileServices services)
    {
        _s = services;
        services.Orchestrator.StatusChanged += (_, st) =>
        {
            if (st.State == ConnectionState.Connected)
            {
                Start();
            }
            else if (ConnectionStateRules.IsIdle(st.State))
            {
                Stop();
            }
        };
    }

    public static bool HasUsageAccess()
    {
        var context = global::Android.App.Application.Context;
        if (context.GetSystemService(Context.AppOpsService) is not AppOpsManager ops)
        {
            return false;
        }

#pragma warning disable CA1422 // CheckOpNoThrow is the only call on Android 10
        var mode = OperatingSystem.IsAndroidVersionAtLeast(29)
            ? ops.UnsafeCheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, global::Android.OS.Process.MyUid(), context.PackageName!)
            : ops.CheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, global::Android.OS.Process.MyUid(), context.PackageName!);
#pragma warning restore CA1422
        return mode == AppOpsManagerMode.Allowed;
    }

    public static void OpenUsageAccessSettings()
    {
        var intent = new Intent(global::Android.Provider.Settings.ActionUsageAccessSettings).AddFlags(ActivityFlags.NewTask);
        global::Android.App.Application.Context.StartActivity(intent);
    }

    private void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    _s.Orchestrator.ReportActivity(Detect());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Detection is optional; never disturb the connection.
                }
            }
        }, CancellationToken.None);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _streaming.Clear();
    }

    private ActivityProfile Detect()
    {
        if (_s.Settings.Current.Performance.Activity == ActivityOptimization.Off)
        {
            return ActivityProfile.Normal;
        }

        if (_s.Settings.Current.Ui.DetectGames && ForegroundIsGame())
        {
            return ActivityProfile.Gaming;
        }

        var down = _s.Orchestrator.Status.Traffic?.DownloadBytesPerSecond ?? 0;
        _streaming.Enqueue(down >= StreamingBytesPerSecond);
        while (_streaming.Count > 6)
        {
            _streaming.Dequeue();
        }

        return _streaming.Count(x => x) >= 4 ? ActivityProfile.Streaming : ActivityProfile.Normal;
    }

    private static bool ForegroundIsGame()
    {
        var context = global::Android.App.Application.Context;
        if (!HasUsageAccess() || context.GetSystemService(Context.UsageStatsService) is not UsageStatsManager usage)
        {
            return false;
        }

        var now = Java.Lang.JavaSystem.CurrentTimeMillis();
        var events = usage.QueryEvents(now - 15_000, now);
        string? foreground = null;
        var e = new UsageEvents.Event();
        while (events is not null && events.HasNextEvent)
        {
            events.GetNextEvent(e);
            if (e.EventType == UsageEventType.ActivityResumed)
            {
                foreground = e.PackageName;
            }
        }

        if (foreground is null || foreground == context.PackageName)
        {
            return false;
        }

        if (KnownGames.Any(g => foreground.StartsWith(g, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            var info = context.PackageManager!.GetApplicationInfo(foreground, 0);
#pragma warning disable CA1422 // FlagIsGame covers games that predate the category
            return info.Category == ApplicationCategories.Game || (info.Flags & ApplicationInfoFlags.IsGame) != 0;
#pragma warning restore CA1422
        }
        catch (PackageManager.NameNotFoundException)
        {
            return false;
        }
    }
}

