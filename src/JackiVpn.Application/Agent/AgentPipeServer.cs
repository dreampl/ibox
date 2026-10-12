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

    /// <summary>Starts or stops listening to follow the setting.</summary>
    public void Sync()
    {
        var enabled = _settings().Enabled;
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
                await ServeAsync(pipe, ct).ConfigureAwait(false);
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

    private async Task ServeAsync(Stream pipe, CancellationToken ct)
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
            else if (!_settings().Enabled)
            {
                response = new AgentResponse(request.Id, false, null, "Agent control is off in IBOX settings.");
            }
            else if (AgentMethods.Changing.Contains(request.Method) && !_settings().AllowChanges)
            {
                response = new AgentResponse(request.Id, false, null, "IBOX allows agents to read only. Turn on \"Allow changes\" in IBOX settings to let them connect or change rules.");
            }
            else
            {
                _logger.LogInformation("Agent request: {Method}.", request.Method);
                response = await _controller.HandleAsync(request, ct).ConfigureAwait(false);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.Json)).ConfigureAwait(false);
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
