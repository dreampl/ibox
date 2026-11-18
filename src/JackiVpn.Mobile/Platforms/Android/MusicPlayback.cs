using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using JackiVpn.Application.Account;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// Android audio for the IBOX music player: the system MediaPlayer (streams and seeks over https, partial wake lock)
/// plus <see cref="MusicPlaybackService"/> while playing, so the music keeps going with the screen off or IBOX in the
/// background.
/// </summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private readonly Context _context;
    private MediaPlayer? _player;
    private int _start;
    private bool _prepared;

    public AndroidAudioOutput(Context context) => _context = context;

    public bool IsPlaying => _player is { } p && _prepared && p.IsPlaying;

    public TimeSpan Position => _player is { } p && _prepared ? TimeSpan.FromMilliseconds(p.CurrentPosition) : TimeSpan.FromMilliseconds(_start);

    public TimeSpan? Duration => _player is { } p && _prepared && p.Duration > 0 ? TimeSpan.FromMilliseconds(p.Duration) : null;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;

    public void Open(Uri stream, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Release();
        _prepared = false;
        _start = (int)start.TotalMilliseconds;
        var p = new MediaPlayer();
        p.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!);
        p.SetWakeMode(_context, WakeLockFlags.Partial);
        p.Prepared += (_, _) =>
        {
            _prepared = true;
            if (_start > 0)
            {
                p.SeekTo(_start);
            }

            if (_wantPlay)
            {
                p.Start();
                MusicPlaybackService.Set(_context, true);
            }
        };
        p.Completion += (_, _) =>
        {
            MusicPlaybackService.Set(_context, false);
            Ended?.Invoke(this, EventArgs.Empty);
        };
        p.Error += (_, e) =>
        {
            e.Handled = true;
            MusicPlaybackService.Set(_context, false);
            Failed?.Invoke(this, "This song could not be played.");
        };
        p.SetDataSource(stream.AbsoluteUri);
        p.PrepareAsync();
        _player = p;
    }

    private bool _wantPlay;

    public void Play()
    {
        _wantPlay = true;
        if (_player is { } p && _prepared)
        {
            p.Start();
            MusicPlaybackService.Set(_context, true);
        }
    }

    public void Pause()
    {
        _wantPlay = false;
        if (_player is { } p && _prepared && p.IsPlaying)
        {
            p.Pause();
        }

        MusicPlaybackService.Set(_context, false);
    }

    public void Seek(TimeSpan to)
    {
        if (_player is { } p && _prepared)
        {
            p.SeekTo((int)to.TotalMilliseconds);
        }
        else
        {
            _start = (int)to.TotalMilliseconds;
        }
    }

    private void Release()
    {
        _player?.Release();
        _player?.Dispose();
        _player = null;
    }

    public void Dispose()
    {
        MusicPlaybackService.Set(_context, false);
        Release();
    }
}

/// <summary>Foreground service (type mediaPlayback) that only exists while IBOX music plays.</summary>
[Service(Name = "com.jacki.vpn.MusicPlaybackService", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class MusicPlaybackService : Service
{
    private const string ChannelId = "ibox-music";
    private const int NotificationId = 4711;

    public static void Set(Context context, bool playing)
    {
        var intent = new Intent(context, typeof(MusicPlaybackService));
        if (playing)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        else
        {
            context.StopService(intent);
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager.GetNotificationChannel(ChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Music", NotificationImportance.Low));
        }

        var open = PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!), PendingIntentFlags.Immutable);
        var n = new Notification.Builder(this, ChannelId)
            .SetContentTitle("IBOX music")!
            .SetContentText(MobileServices.Instance?.Ibox.Player?.Track?.Line ?? "Playing")!
            .SetSmallIcon(Resource.Drawable.ic_stat_jacki)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, n, ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, n);
        }

        return StartCommandResult.NotSticky;
    }
}
