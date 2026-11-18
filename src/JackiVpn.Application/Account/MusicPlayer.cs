using JackiVpn.Application.Media;

namespace JackiVpn.Application.Account;

/// <summary>The platform's audio output (WPF MediaPlayer, Android MediaPlayer, mpv on Linux). Streams an https link.</summary>
public interface IAudioOutput : IDisposable
{
    bool IsPlaying { get; }

    TimeSpan Position { get; }

    TimeSpan? Duration { get; }

    /// <summary>Raised when the song played to its end.</summary>
    event EventHandler? Ended;

    /// <summary>Raised when the stream could not be played (message for the person).</summary>
    event EventHandler<string>? Failed;

    void Open(Uri stream, TimeSpan start);

    void Play();

    void Pause();

    void Seek(TimeSpan to);
}

/// <summary>
/// The IBOX music player: a queue over one <see cref="IAudioOutput"/>, with play / pause / seek / next / previous.
/// It is an <see cref="INowPlayingSource"/>, so the notch / island can show and control it like any media session.
/// </summary>
public sealed class MusicPlayer : INowPlayingSource
{
    private readonly IAudioOutput _out;
    private readonly Func<MusicTrack, double, Task>? _savePosition;
    private List<MusicTrack> _queue = [];
    private DateTime _lastSave;

    public MusicPlayer(IAudioOutput output, Func<MusicTrack, double, Task>? savePosition = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _out = output;
        _savePosition = savePosition;
        _out.Ended += (_, _) => _ = NextAsync();
        _out.Failed += (_, m) =>
        {
            Error = m;
            Publish();
        };
    }

    public IReadOnlyList<MusicTrack> Queue => _queue;

    public int Index { get; private set; } = -1;

    public MusicTrack? Track => Index >= 0 && Index < _queue.Count ? _queue[Index] : null;

    public string? Error { get; private set; }

    public TimeSpan Position => _out.Position;

    public TimeSpan? Duration => _out.Duration ?? (Track is { Seconds: > 0 } t ? TimeSpan.FromSeconds(t.Seconds) : null);

    public bool IsPlaying => _out.IsPlaying;

    public NowPlaying? Current => Track is { } t ? new NowPlaying(t.Title, t.Artist, "IBOX", _out.IsPlaying) : null;

    public event EventHandler<NowPlaying?>? Changed;

    public void Start()
    {
        // nothing to watch: the player itself reports every change
    }

    /// <summary>Plays <paramref name="songs"/> from <paramref name="index"/>, optionally at <paramref name="at"/>.</summary>
    public void Play(IReadOnlyList<MusicTrack> songs, int index, TimeSpan at = default)
    {
        ArgumentNullException.ThrowIfNull(songs);
        _queue = songs.Where(s => s.Stream is not null).ToList();
        var target = index >= 0 && index < songs.Count ? songs[index] : null;
        Index = target is null ? (_queue.Count > 0 ? 0 : -1) : _queue.IndexOf(target);
        OpenCurrent(at);
    }

    public Task PlayPauseAsync()
    {
        if (Track is null)
        {
            return Task.CompletedTask;
        }

        if (_out.IsPlaying)
        {
            _out.Pause();
            Save(force: true);
        }
        else
        {
            _out.Play();
        }

        Publish();
        return Task.CompletedTask;
    }

    public Task NextAsync()
    {
        if (_queue.Count == 0)
        {
            return Task.CompletedTask;
        }

        Index = (Index + 1) % _queue.Count;
        OpenCurrent(TimeSpan.Zero);
        return Task.CompletedTask;
    }

    /// <summary>Back to the start of the song when more than 3 s in, else the previous song.</summary>
    public Task PreviousAsync()
    {
        if (_queue.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (_out.Position > TimeSpan.FromSeconds(3))
        {
            _out.Seek(TimeSpan.Zero);
        }
        else
        {
            Index = (Index - 1 + _queue.Count) % _queue.Count;
            OpenCurrent(TimeSpan.Zero);
        }

        return Task.CompletedTask;
    }

    public void Seek(TimeSpan to)
    {
        if (Track is null)
        {
            return;
        }

        var max = Duration ?? TimeSpan.MaxValue;
        _out.Seek(to < TimeSpan.Zero ? TimeSpan.Zero : to > max ? max : to);
        Save(force: true);
    }

    /// <summary>Called by the UI's progress timer: saves the position on the server about every 20 s.</summary>
    public void Tick() => Save(force: false);

    private void OpenCurrent(TimeSpan at)
    {
        Error = null;
        if (Track is not { Stream: { } url })
        {
            Publish();
            return;
        }

        _out.Open(url, at);
        _out.Play();
        Publish();
    }

    private void Save(bool force)
    {
        if (_savePosition is null || Track is not { } t || (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(20)))
        {
            return;
        }

        _lastSave = DateTime.UtcNow;
        _ = SafeSave(t, _out.Position.TotalSeconds);
    }

    private async Task SafeSave(MusicTrack t, double seconds)
    {
        try
        {
            await _savePosition!(t, seconds).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ServiceUnreachableException or InvalidOperationException or HttpRequestException)
        {
            // the position is a convenience; the next save tries again
        }
    }

    private void Publish() => Changed?.Invoke(this, Current);

    public void Dispose() => _out.Dispose();
}
