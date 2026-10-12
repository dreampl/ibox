using System;
using System.Threading.Tasks;
using JackiVpn.Application.Media;
using Windows.Media.Control;

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
        }

        _session = session;
        if (session is null)
        {
            Publish(null);
            return;
        }

        session.MediaPropertiesChanged += OnProperties;
        session.PlaybackInfoChanged += OnPlayback;
        _ = RefreshAsync();
    }

    private void OnProperties(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

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
            Publish(string.IsNullOrWhiteSpace(props?.Title)
                ? null
                : new NowPlaying(props.Title.Trim(), string.IsNullOrWhiteSpace(props.Artist) ? null : props.Artist.Trim(), session.SourceAppUserModelId, playing));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Publish(null);
        }
    }

    private void Publish(NowPlaying? item)
    {
        if (item == Current)
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

    public void Dispose() => Attach(null);
}
