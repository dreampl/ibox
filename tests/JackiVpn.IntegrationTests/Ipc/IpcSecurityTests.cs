using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using JackiVpn.Ipc;
using JackiVpn.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.IntegrationTests.Ipc;

/// <summary>Named-pipe authentication, authorization and robustness (no admin required).</summary>
public sealed class IpcSecurityTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class EchoHandler : IIpcRequestHandler
    {
        public int Calls;

        public Task<JsonElement?> HandleAsync(string method, JsonElement? payload, IpcClientContext client, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return method switch
            {
                IpcMethods.GetStatus => Task.FromResult<JsonElement?>(IpcFraming.ToElement(new ServiceStatus { ServiceVersion = "test", RestartCount = client.ProcessId })),
                IpcMethods.StopTunnel => throw new IpcHandlerException(IpcErrorCodes.Busy, "busy password=hunter2"),
                _ => Task.FromResult<JsonElement?>(null),
            };
        }
    }

    private static (IpcServer Server, string Pipe, EchoHandler Handler) StartServer(IClientAuthorizer authorizer)
    {
        var name = "JackiVpn.Test." + Guid.NewGuid().ToString("N");
        var handler = new EchoHandler();
        var server = new IpcServer(new IpcServerOptions { PipeName = name, RestrictToInteractiveUsers = false, ServerVersion = "9.9.9" }, authorizer, handler, NullLogger.Instance);
        server.Start();
        return (server, name, handler);
    }

    private static IpcClient Client(string pipe, params SecurityIdentifier[] trustedOwners) =>
        new(new IpcClientOptions { PipeName = pipe, TrustedServerOwners = trustedOwners.Length == 0 ? [Me] : trustedOwners, ConnectTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(10) });

    [Fact]
    public async Task Authorized_client_handshakes_and_calls_methods()
    {
        var (server, pipe, handler) = StartServer(new SameUserAuthorizer());
        await using var _ = server;
        await using var client = Client(pipe);

        await client.ConnectAsync(Ct);
        var status = await client.RequestAsync<ServiceStatus>(IpcMethods.GetStatus, Ct);

        client.Hello!.ServiceVersion.Should().Be("9.9.9");
        status.RestartCount.Should().Be(Environment.ProcessId, "the server identified the real client PID");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Unregistered_client_image_is_rejected()
    {
        var other = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var authorizer = new TrustedImageAuthorizer(() => (other, FileIntegrity.ComputeSha256(other)));
        var (server, pipe, handler) = StartServer(authorizer);
        await using var _ = server;
        await using var client = Client(pipe);

        var act = async () => await client.ConnectAsync(Ct);

        (await act.Should().ThrowAsync<IpcRemoteException>()).Which.Code.Should().Be(IpcErrorCodes.Unauthorized);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Registered_client_with_matching_path_and_hash_is_allowed_but_tampered_hash_is_not()
    {
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        var good = new TrustedImageAuthorizer(() => (self, FileIntegrity.ComputeSha256(self)));
        var ctx = new IpcClientContext(Environment.ProcessId, self, Me, 1);

        good.Authorize(ctx).Allowed.Should().BeTrue();
        new TrustedImageAuthorizer(() => (self, new string('0', 64))).Authorize(ctx).Allowed.Should().BeFalse();
        new TrustedImageAuthorizer(() => (null, null)).Authorize(ctx).Allowed.Should().BeFalse("nothing registered");
        good.Authorize(ctx with { UserSid = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null) }).Allowed.Should().BeFalse();
        good.Authorize(ctx with { ImagePath = Path.Combine(Path.GetTempPath(), "evil.exe") }).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Client_refuses_a_pipe_not_owned_by_the_service_account()
    {
        // Simulates pipe squatting: the "server" runs as a normal user, the client only trusts SYSTEM/Admins.
        var (server, pipe, handler) = StartServer(new SameUserAuthorizer());
        await using var _ = server;
        await using var client = Client(pipe, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        var act = async () => await client.ConnectAsync(Ct);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        handler.Calls.Should().Be(0, "no request (and no credential) may be sent to an untrusted pipe");
    }

    [Fact]
    public async Task Handler_errors_are_redacted_and_typed()
    {
        var (server, pipe, _) = StartServer(new SameUserAuthorizer());
        await using var __ = server;
        await using var client = Client(pipe);
        await client.ConnectAsync(Ct);

        var act = async () => await client.SendAsync(IpcMethods.StopTunnel, null, Ct);

        var ex = (await act.Should().ThrowAsync<IpcRemoteException>()).Which;
        ex.Code.Should().Be(IpcErrorCodes.Busy);
        ex.Message.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task Requests_before_handshake_unknown_methods_and_version_mismatch_are_refused()
    {
        var (server, pipe, handler) = StartServer(new SameUserAuthorizer());
        await using var _ = server;
        await using var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await raw.ConnectAsync(5000, Ct);
        var wl = new SemaphoreSlim(1, 1);

        await IpcFraming.WriteAsync(raw, new IpcEnvelope { Kind = IpcMessageKind.Request, Id = "1", Method = IpcMethods.GetStatus }, wl, Ct);
        (await IpcFraming.ReadAsync(raw, Ct))!.Error!.Code.Should().Be(IpcErrorCodes.BadRequest);

        await IpcFraming.WriteAsync(raw, new IpcEnvelope { Kind = IpcMessageKind.Request, Id = "2", Method = "shell.exec" }, wl, Ct);
        (await IpcFraming.ReadAsync(raw, Ct))!.Error!.Code.Should().Be(IpcErrorCodes.UnknownMethod);

        await IpcFraming.WriteAsync(raw, new IpcEnvelope { Kind = IpcMessageKind.Request, Id = "3", Method = IpcMethods.Hello, Payload = IpcFraming.ToElement(new HelloRequest(999, "x")) }, wl, Ct);
        (await IpcFraming.ReadAsync(raw, Ct))!.Error!.Code.Should().Be(IpcErrorCodes.VersionMismatch);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Malformed_and_oversized_frames_close_the_connection_without_killing_the_server()
    {
        var (server, pipe, _) = StartServer(new SameUserAuthorizer());
        await using var __ = server;

        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            await raw.ConnectAsync(5000, Ct);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
            await raw.WriteAsync(header, Ct);
            await raw.FlushAsync(Ct);
            var buffer = new byte[16];
            (await raw.ReadAsync(buffer, Ct)).Should().Be(0, "server must drop the connection");
        }

        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            await raw.ConnectAsync(5000, Ct);
            var body = "{ not json"u8.ToArray();
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
            await raw.WriteAsync(header, Ct);
            await raw.WriteAsync(body, Ct);
            await raw.FlushAsync(Ct);
            var buffer = new byte[16];
            (await raw.ReadAsync(buffer, Ct)).Should().Be(0);
        }

        await using var client = Client(pipe);
        await client.ConnectAsync(Ct);
        (await client.RequestAsync<ServiceStatus>(IpcMethods.GetStatus, Ct)).ServiceVersion.Should().Be("test");
    }

    [Fact]
    public async Task Subscribed_clients_receive_events()
    {
        var (server, pipe, _) = StartServer(new SameUserAuthorizer());
        await using var __ = server;
        await using var client = Client(pipe);
        var received = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (_, e) => received.TrySetResult(e);
        await client.ConnectAsync(Ct);
        await client.SubscribeAsync(Ct);

        await server.BroadcastAsync(IpcEvents.Traffic, new TrafficEvent(1, 2, 3, 4));

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        e.Method.Should().Be(IpcEvents.Traffic);
        IpcFraming.FromElement<TrafficEvent>(e.Payload).DownloadTotal.Should().Be(2);
    }
}
