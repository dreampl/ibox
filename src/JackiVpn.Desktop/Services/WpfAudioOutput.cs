using System;
using System.Windows.Media;
using JackiVpn.Application.Account;

namespace JackiVpn.Desktop.Services;

/// <summary>Windows audio for the IBOX music player: WPF's MediaPlayer (Media Foundation, streams and seeks over https).</summary>
internal sealed class WpfAudioOutput : IAudioOutput
{
    private readonly MediaPlayer _player = new();
    private TimeSpan _start;

    public WpfAudioOutput()
    {
        _player.MediaEnded += (_, _) =>
        {
            IsPlaying = false;
            Ended?.Invoke(this, EventArgs.Empty);
        };
        _player.MediaFailed += (_, e) =>
        {
            IsPlaying = false;
            Failed?.Invoke(this, "This song could not be played: " + e.ErrorException?.Message);
        };
        _player.MediaOpened += (_, _) =>
        {
            if (_start > TimeSpan.Zero)
            {
                _player.Position = _start;
            }
        };
    }

    public bool IsPlaying { get; private set; }

    public TimeSpan Position => _player.Position;

    public TimeSpan? Duration => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : null;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;

    public void Open(Uri stream, TimeSpan start)
    {
        _start = start;
        _player.Open(stream);
    }

    public void Play()
    {
        _player.Play();
        IsPlaying = true;
    }

    public void Pause()
    {
        _player.Pause();
        IsPlaying = false;
    }

    public void Seek(TimeSpan to) => _player.Position = to;

    public void Dispose() => _player.Close();
}
