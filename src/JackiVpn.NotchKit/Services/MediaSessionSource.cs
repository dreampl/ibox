using System;
using System.IO;
using System.Threading.Tasks;
using JackiVpn.Application.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace JackiVpn.Desktop.Services;

/// <summary>Windows' global media session (the media flyout's source): Spotify, browsers, Groove, VLC…</summary>
public sealed class MediaSessionSource : INowPlayingSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public NowPlaying? Current { get; private set; }

    public event EventHandler<NowPlaying?>? Changed;

    public void Start() => _ = StartAsync();

    private async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach(_manager.GetCurrentSession());
            Attach(_manager.GetCurrentSession());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not available (older Windows, policy): the notch simply shows no music.
            Publish(null);
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnProperties;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = session;
        if (session is null)
        {
            Publish(null);
            return;
        }

        session.MediaPropertiesChanged += OnProperties;
        session.PlaybackInfoChanged += OnPlayback;
        session.TimelinePropertiesChanged += OnTimeline;
        _ = RefreshAsync();
    }

    private void OnProperties(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    // Some players report the timeline every second: only a seek or a new length gets through (see Publish).
    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    // The cover of the current item, read once per item (same array = no redraw).
    private string? _artKey;
    private byte[]? _art;

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (string.IsNullOrWhiteSpace(props?.Title))
            {
                Publish(null);
                return;
            }

            var title = props.Title.Trim();
            var artist = string.IsNullOrWhiteSpace(props.Artist) ? null : props.Artist.Trim();
            var key = session.SourceAppUserModelId + "|" + title + "|" + artist;
            if (key != _artKey)
            {
                _artKey = key;
                _art = await ReadArtworkAsync(props.Thumbnail);
            }

            TimeSpan? length = null, position = null;
            var at = DateTimeOffset.UtcNow;
            if (session.GetTimelineProperties() is { } tl && tl.EndTime > tl.StartTime)
            {
                length = tl.EndTime - tl.StartTime;
                position = tl.Position - tl.StartTime;
                if (tl.LastUpdatedTime.Year > 2000 && tl.LastUpdatedTime <= at)
                {
                    at = tl.LastUpdatedTime;
                }
            }

            Publish(new NowPlaying(title, artist, session.SourceAppUserModelId, playing)
            {
                Length = length,
                Position = position,
                PositionAt = at,
                Artwork = _art,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Publish(null);
        }
    }

    /// <summary>The cover as image bytes (capped at 2 MB); null when the player has none.</summary>
    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size is 0 or > 2 * 1024 * 1024)
            {
                return null;
            }

            using var managed = stream.AsStreamForRead();
            using var copy = new MemoryStream((int)stream.Size);
            await managed.CopyToAsync(copy);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void Publish(NowPlaying? item)
    {
        if (item == Current || (item is not null && item.SameAsPredicted(Current, TimeSpan.FromSeconds(1.5))))
        {
            return;
        }

        Current = item;
        Changed?.Invoke(this, item);
    }

    public async Task PlayPauseAsync()
    {
        if (_session is { } s)
        {
            await s.TryTogglePlayPauseAsync();
        }
    }

    public async Task NextAsync()
    {
        if (_session is { } s)
        {
            await s.TrySkipNextAsync();
        }
    }

    public async Task PreviousAsync()
    {
        if (_session is { } s)
        {
            await s.TrySkipPreviousAsync();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_session is { } s && s.GetTimelineProperties() is { } tl)
        {
            var target = tl.StartTime + (position < TimeSpan.Zero ? TimeSpan.Zero : position);
            await s.TryChangePlaybackPositionAsync(target.Ticks);
        }
    }

    public void Dispose() => Attach(null);
}
