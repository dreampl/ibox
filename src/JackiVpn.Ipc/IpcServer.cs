using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Ipc;

public sealed class IpcHandlerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IIpcRequestHandler
{
    Task<JsonElement?> HandleAsync(string method, JsonElement? payload, IpcClientContext client, CancellationToken ct);
}

public sealed record IpcServerOptions
{
    public string PipeName { get; init; } = Domain.ProductInfo.PipeName;
    public int MaxConnections { get; init; } = 4;
    public string ServerVersion { get; init; } = "0.0.0";

    /// <summary>Production pipes are restricted to SYSTEM, Administrators and interactive users. Tests may relax this.</summary>
    public bool RestrictToInteractiveUsers { get; init; } = true;
}

/// <summary>
/// Named-pipe server for the privileged service. Security layers:
/// <list type="number">
/// <item>Pipe DACL: SYSTEM/Administrators full control; INTERACTIVE read/write (no create-instance);
/// NETWORK logons explicitly denied (no remote access).</item>
/// <item>First instance flag: another process cannot pre-create (squat) the pipe name.</item>
/// <item>Per-connection client verification (<see cref="IClientAuthorizer"/>): the client image must be the
/// registered Desktop binary (path + SHA-256).</item>
/// <item>Protocol: hello/version handshake first; only whitelisted methods; strict payload validation by handlers.</item>
/// </list>
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IpcServerOptions _options;
    private readonly IClientAuthorizer _authorizer;
    private readonly IIpcRequestHandler _handler;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private bool _firstInstanceCreated;

    public IpcServer(IpcServerOptions options, IClientAuthorizer authorizer, IIpcRequestHandler handler, ILogger logger)
    {
        _options = options;
        _authorizer = authorizer;
        _handler = handler;
        _logger = logger;
    }

    public int ConnectionCount => _connections.Count;

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));

    /// <summary>Pushes an event to every client that called <see cref="IpcMethods.Subscribe"/>.</summary>
    public async Task BroadcastAsync<T>(string eventName, T payload)
    {
        var envelope = new IpcEnvelope
        {
            Kind = IpcMessageKind.Event,
            Id = Guid.NewGuid().ToString("N"),
            Method = eventName,
            Payload = IpcFraming.ToElement(payload),
        };
        foreach (var c in _connections.Values.Where(c => c.Subscribed))
        {
            try
            {
                await IpcFraming.WriteAsync(c.Pipe, envelope, c.WriteLock, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                c.Cancel();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (!_firstInstanceCreated)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        NamedPipeServerStream pipe;
        if (_options.RestrictToInteractiveUsers)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            pipe = NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, _options.MaxConnections, PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, security);
        }
        else
        {
            pipe = new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut, _options.MaxConnections, PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly);
        }

        _firstInstanceCreated = true;
        return pipe;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (IOException ex)
            {
                // All instances busy, or (first instance) the name is already taken: back off.
                _logger.LogWarning("Cannot create pipe instance: {Error}", ex.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogCritical("Pipe {Name} could not be created securely (possible squatting): {Error}", _options.PipeName, ex.Message);
                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
    {
        try
        {
            await ServeCoreAsync(pipe, serverCt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A per-connection failure must never leave a half-open pipe (client would hang) or kill the server.
            _logger.LogError(ex, "IPC connection handler failed; closing the connection.");
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeCoreAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
    {
        IpcClientContext client;
        bool allowed;
        string reason;
        try
        {
            client = PeerIdentity.Describe(pipe);
            (allowed, reason) = _authorizer.Authorize(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            client = new IpcClientContext(-1, null, null, -1);
            (allowed, reason) = (false, "Client identity could not be verified: " + ex.Message);
        }
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var conn = new Connection(pipe, connCts);
        if (!allowed)
        {
            _logger.LogWarning("Rejected IPC client pid {Pid} ({Image}, user {User}): {Reason}", client.ProcessId, client.ImagePath, SidText.Describe(client.UserSid), reason);
            await TrySendAsync(conn, new IpcEnvelope { Kind = IpcMessageKind.Response, Id = "0", Error = new IpcError(IpcErrorCodes.Unauthorized, reason) }).ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var id = Guid.NewGuid();
        _connections[id] = conn;
        _logger.LogInformation("IPC client connected: pid {Pid} ({User}).", client.ProcessId, SidText.Describe(client.UserSid));
        var helloDone = false;
        try
        {
            while (!connCts.IsCancellationRequested)
            {
                IpcEnvelope? request;
                try
                {
                    request = await IpcFraming.ReadAsync(pipe, connCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    _logger.LogWarning("Malformed IPC frame from pid {Pid}: {Error}; closing.", client.ProcessId, ex.Message);
                    break;
                }

                if (request is null)
                {
                    break;
                }

                if (request.Kind != IpcMessageKind.Request || request.Method is null || !IpcMethods.All.Contains(request.Method))
                {
                    await TrySendAsync(conn, Fail(request.Id, IpcErrorCodes.UnknownMethod, "Unknown method.")).ConfigureAwait(false);
                    continue;
                }

                if (!helloDone && request.Method != IpcMethods.Hello)
                {
                    await TrySendAsync(conn, Fail(request.Id, IpcErrorCodes.BadRequest, "Handshake required.")).ConfigureAwait(false);
                    continue;
                }

                if (request.Method == IpcMethods.Hello)
                {
                    HelloRequest hello;
                    try
                    {
                        hello = IpcFraming.FromElement<HelloRequest>(request.Payload);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException)
                    {
                        await TrySendAsync(conn, Fail(request.Id, IpcErrorCodes.BadRequest, "Invalid hello.")).ConfigureAwait(false);
                        continue;
                    }

                    if (hello.ProtocolVersion != IpcProtocol.Version)
                    {
                        await TrySendAsync(conn, Fail(request.Id, IpcErrorCodes.VersionMismatch, $"Service speaks protocol {IpcProtocol.Version}, client {hello.ProtocolVersion}.")).ConfigureAwait(false);
                        break;
                    }

                    helloDone = true;
                    await TrySendAsync(conn, Ok(request.Id, IpcFraming.ToElement(new HelloResponse(IpcProtocol.Version, _options.ServerVersion, id.ToString("N"))))).ConfigureAwait(false);
                    continue;
                }

                if (request.Method == IpcMethods.Subscribe)
                {
                    conn.Subscribed = true;
                    await TrySendAsync(conn, Ok(request.Id, null)).ConfigureAwait(false);
                    continue;
                }

                // Requests are handled concurrently so a long "connect" never blocks "status".
                _ = Task.Run(() => DispatchAsync(conn, request, client, connCts.Token), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _connections.TryRemove(id, out _);
            await connCts.CancelAsync().ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("IPC client pid {Pid} disconnected.", client.ProcessId);
        }
    }

    private async Task DispatchAsync(Connection conn, IpcEnvelope request, IpcClientContext client, CancellationToken ct)
    {
        IpcEnvelope response;
        try
        {
            var result = await _handler.HandleAsync(request.Method!, request.Payload, client, ct).ConfigureAwait(false);
            response = Ok(request.Id, result);
        }
        catch (IpcHandlerException ex)
        {
            response = Fail(request.Id, ex.Code, SecretRedactor.Redact(ex.Message));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or FormatException)
        {
            response = Fail(request.Id, IpcErrorCodes.BadRequest, SecretRedactor.Redact(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "IPC method {Method} failed.", request.Method);
            response = Fail(request.Id, IpcErrorCodes.Failed, SecretRedactor.Redact(ex.Message));
        }

        await TrySendAsync(conn, response).ConfigureAwait(false);
    }

    private static IpcEnvelope Ok(string id, JsonElement? payload) => new() { Kind = IpcMessageKind.Response, Id = id, Payload = payload };

    private static IpcEnvelope Fail(string id, string code, string message) => new() { Kind = IpcMessageKind.Response, Id = id, Error = new IpcError(code, message) };

    private async Task TrySendAsync(Connection conn, IpcEnvelope envelope)
    {
        try
        {
            // Bounded: a client that stops reading must not wedge the server.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await IpcFraming.WriteAsync(conn.Pipe, envelope, conn.WriteLock, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var c in _connections.Values)
        {
            c.Cancel();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private sealed class Connection(NamedPipeServerStream pipe, CancellationTokenSource cts)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public CancellationTokenSource Cts { get; } = cts;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public bool Subscribed { get; set; }

        /// <summary>Idempotent and race-free with the connection's own teardown.</summary>
        public void Cancel()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
