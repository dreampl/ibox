using System;
using System.IO;
using System.Media;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// The connect sound: two soft, low notes (G4 then D5, a rising fifth) as pure sines with a slow swell and a short
/// gentle fade — quiet and light like a soft notification, never a beep. Synthesized once in
/// memory (16-bit PCM WAV), so no audio file ships with the app.
/// </summary>
public static class Chime
{
    private const int Rate = 44100;
    private static readonly Lazy<byte[]> Connected = new(() => Render([(392.0, 0.0), (587.33, 0.12)], 0.95, 0.075));
    private static SoundPlayer? _player;

    /// <summary>Plays the connect chime without blocking; failures (no audio device) are ignored.</summary>
    public static void PlayConnected()
    {
        try
        {
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(Connected.Value, writable: false));
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
        }
    }

    private static byte[] Render((double Hz, double Start)[] notes, double seconds, double volume)
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

                // Pure sine (no harmonics: nothing sharp), a slow 40 ms swell and a short soft fade.
                var swell = Math.Min(1, local / 0.04);
                var envelope = swell * swell * (3 - (2 * swell)) * Math.Exp(-local * 5.8);
                v += Math.Sin(2 * Math.PI * hz * local) * envelope;
            }

            // Fade the very end to silence (no click).
            var tail = Math.Min(1, (seconds - t) / 0.08);
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
}
