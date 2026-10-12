namespace JackiVpn.Application.Media;

/// <summary>What the system's media session is playing (music, video, podcast), for the notch.</summary>
public sealed record NowPlaying(string Title, string? Artist, string? App, bool IsPlaying)
{
    /// <summary>"Title — Artist", shortened for a narrow line.</summary>
    public string Line(int max)
    {
        var text = Artist is { Length: > 0 } a ? $"{Title} — {a}" : Title;
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }
}

/// <summary>
/// The platform's "now playing" (Windows media session, Linux MPRIS). Reports changes and can play/pause, skip
/// and go back; nothing is recorded or sent anywhere.
/// </summary>
public interface INowPlayingSource : IDisposable
{
    /// <summary>The current item, or null when nothing has a media session.</summary>
    NowPlaying? Current { get; }

    /// <summary>Raised (on any thread) when the item or its play state changed.</summary>
    event EventHandler<NowPlaying?>? Changed;

    void Start();

    Task PlayPauseAsync();

    Task NextAsync();

    Task PreviousAsync();
}
