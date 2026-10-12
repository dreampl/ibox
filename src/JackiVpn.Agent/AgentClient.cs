using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Agent;

/// <summary>Talks to a running IBOX over its control pipe (same OS user only).</summary>
public sealed class AgentClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _id;

    public AgentClient(string? pipeName = null) =>
        _pipe = new NamedPipeClientStream(".", pipeName ?? AgentProtocol.PipeName(Environment.UserName), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            throw new AgentUnavailableException("IBOX is not running, or agent control is off (IBOX → Settings → AI agents).", ex);
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>Sends one request and returns the answer (a connect can take a while: smart connect tests servers).</summary>
    public async Task<AgentResponse> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        if (_writer is null || _reader is null)
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }

        var request = new AgentRequest(Interlocked.Increment(ref _id), method, parameters is null ? null : JsonSerializer.SerializeToElement(parameters, AgentProtocol.Json));
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, AgentProtocol.Json).AsMemory(), ct).ConfigureAwait(false);
        var line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new AgentUnavailableException("IBOX closed the connection.");
        return JsonSerializer.Deserialize<AgentResponse>(line, AgentProtocol.Json) ?? throw new AgentUnavailableException("Unreadable answer from IBOX.");
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class AgentUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
