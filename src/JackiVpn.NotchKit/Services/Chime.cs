using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;

namespace JackiVpn.Desktop.Services;

/// <summary>The three state sounds (one family: soft sines, the same timbre and envelope).</summary>
public enum ChimeKind
{
    /// <summary>Something began (a render, a download): a short soft rise.</summary>
    Start,

    /// <summary>It finished: a resolving rise, a little brighter.</summary>
    Done,

    /// <summary>It failed: the same voice falling, lower and darker — never harsh.</summary>
    Error,
}

/// <summary>
/// The notch's sounds, synthesized in memory (16-bit PCM WAV; no audio file ships): pure sines with a faint second
/// harmonic for warmth, a 14 ms smoothstep swell and an exponential fade, 140–200 ms long — soft and simple, never a
/// beep. One family for start, done and error, plus the older connect chime. Silent while a full-screen app, a
/// game or a presentation is in front (Windows' own "do not disturb" states), at volume 0, or when turned off.
/// </summary>
public static partial class Chime
{
    private const int Rate = 44100;
    private static readonly Lazy<byte[]> Connected = new(() => Render([(392.0, 0.0), (587.33, 0.12)], 0.95, 0.075, harmonic: 0, decay: 5.8));
    private static readonly Dictionary<(ChimeKind, int), byte[]> Cache = [];
    private static SoundPlayer? _player;

    /// <summary>0…1 (0 = silent).</summary>
    public static double Volume { get; set; } = 0.6;

    public static bool Enabled { get; set; } = true;

    /// <summary>Plays the connect chime without blocking; failures (no audio device) are ignored.</summary>
    public static void PlayConnected() => Play(Connected.Value);

    /// <summary>Plays a state sound unless muted or something full-screen is in front. True when it played.</summary>
    public static bool Play(ChimeKind kind)
    {
        if (!Enabled || Volume <= 0.001 || Quiet())
        {
            return false;
        }

        var level = (int)Math.Round(Math.Clamp(Volume, 0, 1) * 20);
        if (!Cache.TryGetValue((kind, level), out var wav))
        {
            wav = Wave(kind, level / 20.0);
            Cache[(kind, level)] = wav;
        }

        Play(wav);
        return true;
    }

    /// <summary>The samples of one sound (tests read them: length, peak, no click at the ends).</summary>
    public static byte[] Wave(ChimeKind kind, double volume) => kind switch
    {
        // Start: A4 → E5, close together, short.
        ChimeKind.Start => Render([(440.0, 0.0), (659.25, 0.055)], 0.16, 0.085 * volume),

        // Done: C5 → G5 → C6, the last one carrying.
        ChimeKind.Done => Render([(523.25, 0.0), (783.99, 0.05), (1046.5, 0.1)], 0.2, 0.075 * volume),

        // Error: E4 → C4, lower and softer.
        _ => Render([(329.63, 0.0), (261.63, 0.07)], 0.2, 0.09 * volume),
    };

    /// <summary>Windows says not to disturb: a full-screen app or game, a presentation, quiet hours.</summary>
    public static bool Quiet()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state is 2 /* BUSY */ or 3 /* D3D full screen */ or 4 /* presentation */ or 6 /* quiet time */;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Play(byte[] wav)
    {
        try
        {
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(wav, writable: false));
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
        }
    }

    private static byte[] Render((double Hz, double Start)[] notes, double seconds, double volume, double harmonic = 0.06, double decay = 16)
    {
        var samples = (int)(seconds * Rate);
        var pcm = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / Rate;
            var v = 0.0;
            foreach (var (hz, start) in notes)
            {
                var local = t - start;
                if (local < 0)
                {
                    continue;
                }

                // Sine (+ a whisper of the octave for warmth), a 14 ms smoothstep swell and an exponential fade.
                var swell = Math.Min(1, local / 0.014);
                var envelope = swell * swell * (3 - (2 * swell)) * Math.Exp(-local * decay);
                v += ((Math.Sin(2 * Math.PI * hz * local) * (1 - harmonic)) + (Math.Sin(4 * Math.PI * hz * local) * harmonic)) * envelope;
            }

            // Fade the very end to silence (no click).
            var tail = Math.Min(1, (seconds - t) / 0.04);
            pcm[i] = (short)Math.Clamp(v * volume * tail * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + (samples * 2));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        foreach (var s in pcm)
        {
            w.Write(s);
        }

        w.Flush();
        return stream.ToArray();
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
