using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace JackiVpn.Ipc;

public sealed class IpcRemoteException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ServiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record IpcClientOptions
{
    public string PipeName { get; init; } = Domain.ProductInfo.PipeName;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public string ClientVersion { get; init; } = "0.0.0";

    /// <summary>
    /// Owners accepted for the server end of the pipe. Production: SYSTEM or Administrators — a pipe created by
    /// an unprivileged process (squatting) is refused before any credentials are sent.
    /// </summary>
    public IReadOnlyList<SecurityIdentifier> TrustedServerOwners { get; init; } =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];
}

/// <summary>Desktop-side pipe client: owner verification, handshake, request/response correlation and events.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly IpcClientOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private volatile IpcError? _rejection;

    public IpcClient(IpcClientOptions options)
    {
        _options = options;
    }

    public event EventHandler<IpcEnvelope>? EventReceived;

    public event EventHandler? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true && _readLoop is { IsCompleted: false };

    public HelloResponse? Hello { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.ConnectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new ServiceUnavailableException("The service is not running or not installed.", ex);
        }

        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !_options.TrustedServerOwners.Contains(owner))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException($"The pipe '{_options.PipeName}' is not owned by the service (owner {owner?.Value ?? "unknown"}). Refusing to connect.");
        }

        _pipe = pipe;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        Hello = await RequestAsync<HelloRequest, HelloResponse>(IpcMethods.Hello, new HelloRequest(IpcProtocol.Version, _options.ClientVersion), ct).ConfigureAwait(false);
    }

    public Task SubscribeAsync(CancellationToken ct) => SendAsync(IpcMethods.Subscribe, null, ct);

    public async Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken ct)
    {
        var response = await SendAsync(method, IpcFraming.ToElement(request), ct).ConfigureAwait(false);
        return IpcFraming.FromElement<TResponse>(response.Payload);
    }

    public async Task<TResponse> RequestAsync<TResponse>(string method, CancellationToken ct)
    {
        var response = await SendAsync(method, null, ct).ConfigureAwait(false);
        return IpcFraming.FromElement<TResponse>(response.Payload);
    }

    public async Task<IpcEnvelope> SendAsync(string method, JsonElement? payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new ServiceUnavailableException("Not connected to the service.");
        if (_rejection is { } rejected)
        {
            throw new IpcRemoteException(rejected.Code, rejected.Message);
        }

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await IpcFraming.WriteAsync(pipe, new IpcEnvelope { Kind = IpcMessageKind.Request, Id = id, Method = method, Payload = payload }, _writeLock, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            var response = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                throw new IpcRemoteException(error.Code, error.Message);
            }

            return response;
        }
        catch (Exception ex) when (ex is IOException or ServiceUnavailableException)
        {
            // The server may have rejected us and closed the pipe before our write went out: let the read loop
            // drain what was already received (the rejection message) so the caller gets the real reason.
            if (_rejection is null && _readLoop is { } loop)
            {
                await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            }

            if (_rejection is { } rejection)
            {
                throw new IpcRemoteException(rejection.Code, rejection.Message);
            }

            throw ex as ServiceUnavailableException ?? new ServiceUnavailableException("Lost connection to the service.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe is { } pipe)
            {
                var envelope = await IpcFraming.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.Kind == IpcMessageKind.Event)
                {
                    EventReceived?.Invoke(this, envelope);
                }
                else if (envelope.Error is { Code: IpcErrorCodes.Unauthorized } && envelope.Id == "0")
                {
                    _rejection = envelope.Error;
                    foreach (var p in _pending.Values)
                    {
                        p.TrySetResult(envelope);
                    }
                }
                else if (_pending.TryRemove(envelope.Id, out var tcs))
                {
                    tcs.TrySetResult(envelope);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or JsonException)
        {
        }
        finally
        {
            foreach (var p in _pending.Values)
            {
                p.TrySetException(new ServiceUnavailableException("Lost connection to the service."));
            }

            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
