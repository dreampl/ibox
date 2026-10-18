using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using JackiVpn.Domain.Agent;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Agent;

/// <summary>
/// The control channel's listener: a named pipe (Windows) / Unix socket (Linux) that only processes of the same OS
/// user can open (<see cref="PipeOptions.CurrentUserOnly"/>), and that exists only while the user has turned agent
/// control on. One JSON request per line, one answer per line; changing requests need "allow changes".
/// </summary>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private const int MaxClients = 4;
    private readonly AgentController _controller;
    private readonly Func<AgentSettings> _settings;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _listening;

    public AgentPipeServer(AgentController controller, Func<AgentSettings> settings, ILogger logger, string? pipeName = null)
    {
        _controller = controller;
        _settings = settings;
        _logger = logger;
        _pipeName = pipeName ?? AgentProtocol.PipeName(Environment.UserName);
    }

    public string PipeName => _pipeName;

    /// <summary>
    /// Recognises the ecosystem app (IBAR) on the other end of a connection: it may read and act without "AI agents"
    /// being on. Null = no ecosystem client on this platform.
    /// </summary>
    public Func<NamedPipeServerStream, bool>? IsTrustedClient { get; init; }

    /// <summary>Ecosystem clients are welcome (listening even with agent control off).</summary>
    public Func<bool> EcosystemOn { get; init; } = () => false;

    /// <summary>Something IBAR shows may have changed (the server pushes eco.status to watchers).</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private event EventHandler? Changed;

    private int _yielding;

    /// <summary>True while IBAR shows the notch (IBOX hides its own); raised on a background thread.</summary>
    public event EventHandler<bool>? NotchYieldChanged;

    public bool NotchYielded => Volatile.Read(ref _yielding) > 0;

    /// <summary>Starts or stops listening to follow the setting.</summary>
    public void Sync()
    {
        var enabled = _settings().Enabled || EcosystemOn();
        lock (_gate)
        {
            if (enabled && _listening is null)
            {
                _listening = new CancellationTokenSource();
                for (var i = 0; i < MaxClients; i++)
                {
                    var token = _listening.Token;
                    _ = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);
                }

                _logger.LogInformation("Agent control is on (pipe {Pipe}).", _pipeName);
            }
            else if (!enabled && _listening is not null)
            {
                _listening.Cancel();
                _listening.Dispose();
                _listening = null;
                _logger.LogInformation("Agent control is off.");
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(pipe, IsTrustedClient?.Invoke(pipe) == true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogDebug("Agent pipe: {Error}", ex.Message);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(Stream pipe, bool trusted, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            AgentResponse response;
            AgentRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<AgentRequest>(line, AgentProtocol.Json);
            }
            catch (JsonException)
            {
                // answered below
            }

            if (request is null || string.IsNullOrEmpty(request.Method))
            {
                response = new AgentResponse(0, false, null, "Not a request.");
            }
            else if (!AgentMethods.All.Contains(request.Method))
            {
                response = new AgentResponse(request.Id, false, null, "Unknown method.");
            }
            else if (!trusted && !_settings().Enabled)
            {
                response = new AgentResponse(request.Id, false, null, "Agent control is off in IBOX settings.");
            }
            else if (!trusted && AgentMethods.Changing.Contains(request.Method) && !_settings().AllowChanges)
            {
                response = new AgentResponse(request.Id, false, null, "IBOX allows agents to read only. Turn on \"Allow changes\" in IBOX settings to let them connect or change rules.");
            }
            else
            {
                _logger.LogInformation("Agent request: {Method}.", request.Method);
                response = await _controller.HandleAsync(request, ct).ConfigureAwait(false);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.Json)).ConfigureAwait(false);
            if (trusted && response.Ok && request?.Method is AgentMethods.Watch or AgentMethods.NotchYield)
            {
                // IBAR stays connected: it hears every change, and IBOX keeps its own notch hidden meanwhile.
                await StreamAsync(reader, writer, request.Method == AgentMethods.NotchYield, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Pushes eco.status to IBAR whenever something it shows changed (at most once a second, nothing while idle)
    /// until IBAR closes the connection; a yielding connection keeps IBOX's own notch hidden meanwhile.
    /// </summary>
    private async Task StreamAsync(StreamReader reader, StreamWriter writer, bool yield, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = new SemaphoreSlim(0, 1);
        void OnChanged(object? sender, EventArgs e)
        {
            if (signal.CurrentCount == 0)
            {
                signal.Release();
            }
        }

        Changed += OnChanged;
        if (yield && Interlocked.Increment(ref _yielding) == 1)
        {
            NotchYieldChanged?.Invoke(this, true);
        }

        var closed = Task.Run(async () =>
        {
            var buffer = new char[64];
            try
            {
                while (await reader.ReadAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false) > 0)
                {
                    // nothing else is expected on a stream
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // closed
            }

            await stop.CancelAsync().ConfigureAwait(false);
        }, CancellationToken.None);

        string? last = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await signal.WaitAsync(stop.Token).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(new AgentResponse(0, true, JsonSerializer.SerializeToElement(_controller.Eco(), AgentProtocol.Json)), AgentProtocol.Json);
                if (json != last)
                {
                    last = json;
                    await writer.WriteLineAsync(json.AsMemory(), stop.Token).ConfigureAwait(false);
                }

                await Task.Delay(1000, stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // IBAR went away
        }
        finally
        {
            Changed -= OnChanged;
            await stop.CancelAsync().ConfigureAwait(false);
            await closed.ConfigureAwait(false);
            if (yield && Interlocked.Decrement(ref _yielding) == 0)
            {
                NotchYieldChanged?.Invoke(this, false);
            }
        }
    }

    /// <summary>One line, refusing anything longer than the protocol allows (a stuck or hostile client).</summary>
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            if (buffer[0] == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }

            if (sb.Length >= AgentProtocol.MaxLineBytes)
            {
                throw new IOException("Request too long.");
            }

            sb.Append(buffer[0]);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _listening?.Cancel();
            _listening?.Dispose();
            _listening = null;
        }

        return ValueTask.CompletedTask;
    }
}
