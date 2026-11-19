namespace JackiVpn.Application.Media;

/// <summary>What the system's media session is playing (music, video, podcast), for the notch.</summary>
public sealed record NowPlaying(string Title, string? Artist, string? App, bool IsPlaying)
{
    /// <summary>Length of the item (null = unknown, a live stream, or the player does not say).</summary>
    public TimeSpan? Length { get; init; }

    /// <summary>Position reported by the player at <see cref="PositionAt"/>.</summary>
    public TimeSpan? Position { get; init; }

    /// <summary>When <see cref="Position"/> was true (UTC).</summary>
    public DateTimeOffset PositionAt { get; init; }

    /// <summary>The cover art as encoded image bytes (PNG/JPEG), or null. The same array for the same item.</summary>
    public byte[]? Artwork { get; init; }

    /// <summary>A timeline worth drawing: a known, positive length and a position.</summary>
    public bool HasTimeline => Length is { TotalSeconds: >= 1 } && Position is not null;

    /// <summary>"Title — Artist", shortened for a narrow line.</summary>
    public string Line(int max)
    {
        var text = Artist is { Length: > 0 } a ? $"{Title} — {a}" : Title;
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }

    /// <summary>
    /// Where playback is at <paramref name="now"/>: the reported position, moved on by the time since it was read
    /// while playing, kept inside the item's length. Null without a timeline.
    /// </summary>
    public TimeSpan? PositionNow(DateTimeOffset now)
    {
        if (!HasTimeline)
        {
            return null;
        }

        var p = Position!.Value;
        if (IsPlaying && now > PositionAt)
        {
            p += now - PositionAt;
        }

        return p < TimeSpan.Zero ? TimeSpan.Zero : p > Length!.Value ? Length.Value : p;
    }

    /// <summary>
    /// The same item and state as <paramref name="other"/>, with a position that only moved on as time passed
    /// (no seek): a new report like that is not worth redrawing for.
    /// </summary>
    public bool SameAsPredicted(NowPlaying? other, TimeSpan tolerance)
    {
        if (other is null || Title != other.Title || Artist != other.Artist || App != other.App || IsPlaying != other.IsPlaying
            || Length != other.Length || !ReferenceEquals(Artwork, other.Artwork))
        {
            return false;
        }

        if (Position is null || other.Position is null)
        {
            return Position is null && other.Position is null;
        }

        var predicted = other.PositionNow(PositionAt);
        return predicted is { } p && (p - Position.Value).Duration() <= tolerance;
    }

    /// <summary>"3:07" or "1:02:45".</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
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

    /// <summary>Jumps to a position in the current item (players that cannot seek ignore it).</summary>
    Task SeekAsync(TimeSpan position) => Task.CompletedTask;
}
