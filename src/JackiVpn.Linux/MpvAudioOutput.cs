using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using JackiVpn.Application.Account;

namespace JackiVpn.Linux;

/// <summary>
/// Linux audio for the IBOX music player: one hidden <c>mpv --idle</c> process driven over its JSON IPC socket (no
/// window, no extra library in IBOX). Without mpv installed, playing reports how to get it.
/// </summary>
public sealed class MpvAudioOutput : IAudioOutput
{
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"ibox-mpv-{Environment.ProcessId}.sock");
    private readonly object _gate = new();
    private Process? _mpv;
    private Socket? _socket;
    private TimeSpan _pendingStart;
    private double _pos;
    private double? _dur;

    public bool IsPlaying { get; private set; }

    public TimeSpan Position => TimeSpan.FromSeconds(_pos);

    public TimeSpan? Duration => _dur is { } d ? TimeSpan.FromSeconds(d) : null;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;

    public void Open(Uri stream, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!EnsureStarted())
        {
            return;
        }

        _pendingStart = start;
        _pos = start.TotalSeconds;
        _dur = null;
        Send("loadfile", stream.AbsoluteUri, "replace");
    }

    public void Play()
    {
        Send("set_property", "pause", false);
        IsPlaying = _socket is not null;
    }

    public void Pause()
    {
        Send("set_property", "pause", true);
        IsPlaying = false;
    }

    public void Seek(TimeSpan to)
    {
        _pos = to.TotalSeconds;
        Send("seek", to.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture), "absolute");
    }

    private bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_socket is { Connected: true })
            {
                return true;
            }

            try
            {
                File.Delete(_socketPath);
                _mpv = Process.Start(new ProcessStartInfo("mpv", ["--no-video", "--idle=yes", "--no-terminal", "--really-quiet", "--input-ipc-server=" + _socketPath])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Failed?.Invoke(this, "Music needs mpv: install it (sudo apt install mpv) and press play again.");
                return false;
            }

            for (var i = 0; i < 40 && !File.Exists(_socketPath); i++)
            {
                Thread.Sleep(50);
            }

            try
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(_socketPath));
                _socket = s;
            }
            catch (SocketException)
            {
                Failed?.Invoke(this, "mpv did not start.");
                return false;
            }

            _ = Task.Run(ReadLoop);
            Send("observe_property", 1, "time-pos");
            Send("observe_property", 2, "duration");
            return true;
        }
    }

    private void Send(params object[] command)
    {
        var s = _socket;
        if (s is null)
        {
            return;
        }

        var arr = new JsonArray();
        foreach (var c in command)
        {
            arr.Add(JsonValue.Create(c));
        }

        try
        {
            s.Send(Encoding.UTF8.GetBytes(new JsonObject { ["command"] = arr }.ToJsonString() + "\n"));
        }
        catch (SocketException)
        {
            _socket = null;
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        var line = new StringBuilder();
        while (_socket is { } s)
        {
            int n;
            try
            {
                n = s.Receive(buf);
            }
            catch (SocketException)
            {
                break;
            }

            if (n <= 0)
            {
                break;
            }

            line.Append(Encoding.UTF8.GetString(buf, 0, n));
            for (var i = line.ToString().IndexOf('\n', StringComparison.Ordinal); i >= 0; i = line.ToString().IndexOf('\n', StringComparison.Ordinal))
            {
                var text = line.ToString(0, i);
                line.Remove(0, i + 1);
                Handle(text);
            }
        }

        IsPlaying = false;
    }

    private void Handle(string text)
    {
        JsonObject? e;
        try
        {
            e = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        switch (e?["event"]?.GetValue<string>())
        {
            case "property-change" when e["name"]?.GetValue<string>() == "time-pos" && e["data"] is JsonValue v && v.TryGetValue<double>(out var p):
                _pos = p;
                break;
            case "property-change" when e["name"]?.GetValue<string>() == "duration" && e["data"] is JsonValue v2 && v2.TryGetValue<double>(out var d):
                _dur = d;
                break;
            case "file-loaded" when _pendingStart > TimeSpan.Zero:
                var start = _pendingStart;
                _pendingStart = TimeSpan.Zero;
                Seek(start);
                break;
            case "end-file":
                var reason = e["reason"]?.GetValue<string>();
                if (reason == "eof")
                {
                    IsPlaying = false;
                    Ended?.Invoke(this, EventArgs.Empty);
                }
                else if (reason == "error")
                {
                    IsPlaying = false;
                    Failed?.Invoke(this, "This song could not be played.");
                }

                break;
        }
    }

    public void Dispose()
    {
        Send("quit");
        _socket?.Dispose();
        _socket = null;
        try
        {
            if (_mpv is { HasExited: false })
            {
                _mpv.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        _mpv?.Dispose();
        File.Delete(_socketPath);
    }
}
