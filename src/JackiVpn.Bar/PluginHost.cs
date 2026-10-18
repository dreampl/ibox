using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Domain.Bar;

namespace JackiVpn.Bar;

/// <summary>
/// IBAR's plugin pipe (only this OS user's programs can open it). A plugin registers its manifest once and waits
/// while the user decides on the notch; approved, it receives a secret token, and every later message must carry it.
/// Messages are rate-limited per plugin and are data only: notifications, a chip, a dashboard of the four widget
/// kinds, buttons that name actions from the approved manifest. A changed manifest is a new request.
/// </summary>
public sealed partial class PluginHost : IAsyncDisposable
{
    private const int Listeners = 4;
    private readonly BarSettingsStore _settings;
    private readonly string _pipeName;
    private readonly RateLimiter _messages = new(perSecond: 2, burst: 10);
    private readonly RateLimiter _notifications = new(perSecond: 0.5, burst: 3);
    private readonly RateLimiter _sounds = new(perSecond: 0.2, burst: 2);
    private readonly RateLimiter _bridgeMessages = new(perSecond: 25, burst: 60);
    private readonly Dictionary<string, HashSet<string>> _tasks = new(StringComparer.Ordinal);
    public const int MaxTasks = 4;
    private readonly CancellationTokenSource _stop = new();

    public PluginHost(BarSettingsStore settings)
    {
        _settings = settings;
        _pipeName = Environment.GetEnvironmentVariable("IBAR_PIPE") is { Length: > 0 } custom ? custom : BarPlugins.PipeName(Environment.UserName);
    }

    /// <summary>Ask the user to approve a manifest (true = allow). Raised on a background thread.</summary>
    public Func<PluginManifest, CancellationToken, Task<bool>>? Approve { get; set; }

    /// <summary>An accepted message from an approved plugin (notify, chip, panel, clear). Raised on a background thread.</summary>
    public event EventHandler<(ApprovedPlugin Plugin, PluginRequest Request)>? Message;

    /// <summary>A task started, changed or ended (source: plugin:&lt;id&gt; or a bridge's app). Raised on a background thread.</summary>
    public event EventHandler<(string Source, PluginRequest Request, string? Icon)>? Activity;

    /// <summary>A plugin asked for a sound (start, done, error). Raised on a background thread.</summary>
    public event EventHandler<string>? Sound;

    /// <summary>A bridge said hello (its browser's extension is connected).</summary>
    public event EventHandler<string>? BridgeOpened;

    /// <summary>A bridge went away (its browser closed): its tasks end with it.</summary>
    public event EventHandler<string>? BridgeClosed;

    public void Start()
    {
        for (var i = 0; i < Listeners; i++)
        {
            _ = Task.Run(() => ListenAsync(_stop.Token));
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, Listeners, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // IBAR's own bridges (the browser extension's native host, the Adobe panel's helper) are recognised by the
        // program at the other end: ibar-host.exe in IBAR's own folder.
        var bridge = IsTrustedBridge(pipe);
        string? spokeFor = null;
        try
        {
            await ServeLinesAsync(pipe, bridge, name => spokeFor = name, ct).ConfigureAwait(false);
        }
        finally
        {
            if (spokeFor is not null)
            {
                BridgeClosed?.Invoke(this, spokeFor);
            }
        }
    }

    private async Task ServeLinesAsync(Stream pipe, bool bridge, Action<string> spokeFor, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            PluginReply reply;
            try
            {
                var request = JsonSerializer.Deserialize<PluginRequest>(line, BarPlugins.Json);
                if (request is null)
                {
                    reply = new PluginReply("error", Error: "Not a request.");
                }
                else if (bridge && request.Bridge is { } name)
                {
                    reply = HandleBridge(request);
                    if (reply.Status == "ok")
                    {
                        spokeFor(name);
                    }
                }
                else
                {
                    reply = await HandleAsync(request, ct).ConfigureAwait(false);
                }
            }
            catch (JsonException)
            {
                reply = new PluginReply("error", Error: "Not JSON.");
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(reply, BarPlugins.Json).AsMemory(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The rules for one request (public for tests through <see cref="PluginRules"/>).</summary>
    public async Task<PluginReply> HandleAsync(PluginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.V != BarPlugins.Version)
        {
            return new PluginReply("error", Error: $"Schema {request.V} is not supported (IBAR speaks {BarPlugins.Version}).");
        }

        if (request.Op == "register")
        {
            return await RegisterAsync(request, ct).ConfigureAwait(false);
        }

        var plugin = PluginRules.Authenticate(_settings.Current.Plugins, request);
        if (plugin is null)
        {
            return new PluginReply("denied", Error: "Unknown plugin or wrong token: register first.");
        }

        if (!_messages.Allow(plugin.Manifest.Id) || (request.Op == "notify" && !_notifications.Allow(plugin.Manifest.Id)))
        {
            return new PluginReply("error", Error: "Too many messages; slow down.");
        }

        if (PluginRules.Problem(plugin, request) is { } problem)
        {
            return new PluginReply("error", Error: problem);
        }

        if (request.Op.StartsWith("activity.", StringComparison.Ordinal))
        {
            if (!TrackTask(plugin.Manifest.Id, request))
            {
                return new PluginReply("error", Error: $"At most {MaxTasks} tasks at a time.");
            }

            Activity?.Invoke(this, ("plugin:" + plugin.Manifest.Id, request, plugin.Manifest.Icon));
            return new PluginReply("ok");
        }

        if (request.Op == "sound")
        {
            if (!_sounds.Allow(plugin.Manifest.Id))
            {
                return new PluginReply("error", Error: "Too many sounds.");
            }

            Sound?.Invoke(this, request.Sound!);
            return new PluginReply("ok");
        }

        Message?.Invoke(this, (plugin, request));
        return new PluginReply("ok");
    }

    /// <summary>A message from one of IBAR's own bridges: activity only, for the app it names.</summary>
    public PluginReply HandleBridge(PluginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.V != BarPlugins.Version || !PluginRules.IsBridge(request.Bridge))
        {
            return new PluginReply("error", Error: "Unknown bridge.");
        }

        if (request.Op == "hello")
        {
            BridgeOpened?.Invoke(this, request.Bridge!);
            return new PluginReply("ok");
        }

        if (!request.Op.StartsWith("activity.", StringComparison.Ordinal))
        {
            return new PluginReply("error", Error: "A bridge reports activity only.");
        }

        if (!_bridgeMessages.Allow(request.Bridge!))
        {
            return new PluginReply("error", Error: "Too many messages; slow down.");
        }

        if (PluginRules.ActivityProblem(request) is { } problem)
        {
            return new PluginReply("error", Error: problem);
        }

        Activity?.Invoke(this, (request.Bridge!, request, null));
        return new PluginReply("ok");
    }

    private bool TrackTask(string plugin, PluginRequest request)
    {
        lock (_tasks)
        {
            if (!_tasks.TryGetValue(plugin, out var open))
            {
                open = new(StringComparer.Ordinal);
                _tasks[plugin] = open;
            }

            if (request.Op == "activity.end")
            {
                open.Remove(request.Task!);
                return true;
            }

            if (!open.Contains(request.Task!) && open.Count >= MaxTasks)
            {
                return false;
            }

            open.Add(request.Task!);
            return true;
        }
    }

    private bool IsTrustedBridge(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
                return false;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            var trusted = Environment.GetEnvironmentVariable("IBAR_TRUST_HOST") is { Length: > 0 } test && Environment.GetEnvironmentVariable("IBAR_DATA") is not null
                ? test
                : Path.Combine(AppContext.BaseDirectory, "ibar-host.exe");
            return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(trusted), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    private async Task<PluginReply> RegisterAsync(PluginRequest request, CancellationToken ct)
    {
        if (BarPlugins.Problem(request.Manifest) is { } problem)
        {
            return new PluginReply("error", Error: problem);
        }

        var manifest = request.Manifest!;
        var hash = BarPlugins.Hash(manifest);
        var known = _settings.Current.Plugins.FirstOrDefault(p => p.Manifest.Id == manifest.Id);
        if (known is not null && known.Hash == hash && PluginRules.SameToken(known.Token, request.Token))
        {
            return new PluginReply("ok", known.Token);
        }

        if (_settings.Current.DeniedPlugins.Contains(manifest.Id + ":" + hash))
        {
            return new PluginReply("denied", Error: "The user declined this plugin.");
        }

        if (_settings.Current.Plugins.Count >= BarPlugins.MaxPlugins && known is null)
        {
            return new PluginReply("error", Error: "Too many plugins.");
        }

        if (!_messages.Allow("register:" + manifest.Id) || Approve is not { } approve)
        {
            return new PluginReply("pending", Error: "Try again later.");
        }

        if (!await approve(manifest, ct).ConfigureAwait(false))
        {
            _settings.Update(s => s with { DeniedPlugins = [.. s.DeniedPlugins, manifest.Id + ":" + hash] });
            return new PluginReply("denied", Error: "The user declined this plugin.");
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _settings.Update(s => s with { Plugins = [.. s.Plugins.Where(p => p.Manifest.Id != manifest.Id), new ApprovedPlugin(manifest, token, hash)] });
        return new PluginReply("ok", token);
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0)
        {
            if (buffer[0] == '\n')
            {
                return sb.ToString();
            }

            if (sb.Length >= BarPlugins.MaxLineBytes)
            {
                return null;
            }

            sb.Append(buffer[0]);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
