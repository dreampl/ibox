using System.Diagnostics;
using JackiVpn.Application.Media;

namespace JackiVpn.Linux;

/// <summary>
/// Linux media players (MPRIS: Spotify, browsers, VLC, Rhythmbox…) through <c>playerctl</c>. Polled every two
/// seconds while running; without playerctl the notch shows no music.
/// </summary>
public sealed class MprisNowPlaying : INowPlayingSource
{
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    public NowPlaying? Current { get; private set; }

    public event EventHandler<NowPlaying?>? Changed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await PlayerctlAsync("metadata", "--format", "{{status}}\t{{title}}\t{{artist}}\t{{playerName}}").ConfigureAwait(false);
                Publish(Parse(line));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>"Playing\tTitle\tArtist\tspotify" → item; empty or "No players found" → null.</summary>
    internal static NowPlaying? Parse(string? line)
    {
        var f = line?.Trim('\n', '\r').Split('\t');
        if (f is not { Length: >= 2 } || string.IsNullOrWhiteSpace(f[1]) || f[0] is not ("Playing" or "Paused"))
        {
            return null;
        }

        return new NowPlaying(f[1].Trim(), f.Length > 2 && f[2].Trim().Length > 0 ? f[2].Trim() : null, f.Length > 3 ? f[3].Trim() : null, f[0] == "Playing");
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

    public Task PlayPauseAsync() => PlayerctlAsync("play-pause");

    public Task NextAsync() => PlayerctlAsync("next");

    public Task PreviousAsync() => PlayerctlAsync("previous");

    private static async Task<string?> PlayerctlAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("playerctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
