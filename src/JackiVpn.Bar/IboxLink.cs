using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Agent;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Bar;

/// <summary>
/// IBAR's link to IBOX over IBOX's own control pipe (same OS user only; IBOX recognises IBAR by its program). One
/// connection stays open: IBOX pushes its status whenever it changes and keeps its own notch hidden while it is
/// open. Commands (connect, cancel, switch, traffic mode, Dual VPN) use short connections of their own. When IBOX is
/// not running IBAR waits on the event IBOX sets at start-up — nothing polls.
/// </summary>
public sealed class IboxLink : IAsyncDisposable
{
    public const string ReadyEvent = @"Local\IBOX.Ecosystem.Ready";
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public IboxLink()
    {
        _pipeName = Environment.GetEnvironmentVariable("IBAR_IBOX_PIPE") is { Length: > 0 } custom ? custom : AgentProtocol.PipeName(Environment.UserName);
    }

    /// <summary>IBOX's status (null = IBOX is not running or not reachable). Raised on a background thread.</summary>
    public event EventHandler<AgentEco?>? Changed;

    public AgentEco? Current { get; private set; }

    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    private bool PipeExists => NotchHook.PipeListed(_pipeName);

    private async Task RunAsync(CancellationToken ct)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, ReadyEvent);
        while (!ct.IsCancellationRequested)
        {
            if (!PipeExists)
            {
                await WaitAsync(ready, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await StreamAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or UnauthorizedAccessException or OperationCanceledException)
            {
                // IBOX went away (or does not know IBAR yet)
            }

            Publish(null);
            if (!ct.IsCancellationRequested)
            {
                // Give a restarting IBOX a moment, then wait for it to say it is up.
                await Task.Delay(1500, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                if (!PipeExists)
                {
                    await WaitAsync(ready, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private static Task WaitAsync(WaitHandle handle, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => done.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        ct.Register(() =>
        {
            registration.Unregister(null);
            done.TrySetResult();
        });
        return done.Task;
    }

    private async Task StreamAsync(CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentRequest(1, AgentMethods.NotchYield, null), AgentProtocol.Json).AsMemory(), ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            var response = JsonSerializer.Deserialize<AgentResponse>(line, AgentProtocol.Json);
            if (response is not { Ok: true, Result: { } result })
            {
                // An older IBOX (no ecosystem), or IBAR not recognised: no VPN on the notch.
                return;
            }

            Publish(result.Deserialize<AgentEco>(AgentProtocol.Json));
        }
    }

    private void Publish(AgentEco? eco)
    {
        Current = eco;
        Changed?.Invoke(this, eco);
    }

    /// <summary>One command; returns the problem to show, or null.</summary>
    public async Task<string?> CallAsync(string method, object? parameters)
    {
        try
        {
            await using var client = new AgentClient(_pipeName);
            var response = await client.CallAsync(method, parameters, CancellationToken.None).ConfigureAwait(false);
            return response.Ok ? null : response.Error;
        }
        catch (Exception ex) when (ex is AgentUnavailableException or IOException or JsonException)
        {
            return ex.Message;
        }
    }

    public async Task<System.Collections.Generic.IReadOnlyList<AgentServer>> ServersAsync()
    {
        try
        {
            await using var client = new AgentClient(_pipeName);
            var response = await client.CallAsync(AgentMethods.ListServers, null, CancellationToken.None).ConfigureAwait(false);
            return response is { Ok: true, Result: { } r } ? r.Deserialize<System.Collections.Generic.List<AgentServer>>(AgentProtocol.Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is AgentUnavailableException or IOException or JsonException)
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }
}
