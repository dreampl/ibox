using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Agent;

/// <summary>
/// <c>ibox-ctl hook claude|codex</c>: run by Claude Code / Codex with the hook's JSON on stdin. Permission requests
/// wait for the answer on the IBOX notch; notifications are handed over and the hook returns at once. Whatever goes
/// wrong (IBOX closed, notch off, no answer in time) the hook prints nothing and exits 0, so the assistant asks the
/// usual way — it never blocks or decides on its own.
/// </summary>
public static class NotchHook
{
    private const int MaxInputBytes = 1024 * 1024;

    public static async Task<int> RunAsync(string source, TextReader input, TextWriter output, string? pipeName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            var message = NotchBridge.Parse(source, await ReadAsync(input, ct).ConfigureAwait(false));
            if (message is null)
            {
                return 0;
            }

            var decision = await SendAsync(message, pipeName ?? PipeName(), ct).ConfigureAwait(false);
            if (message.IsAsk && NotchBridge.HookOutput(decision) is { } answer)
            {
                await output.WriteLineAsync(answer).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            // No answer: the assistant shows its own prompt.
        }

        return 0;
    }

    /// <summary>The pipe of this user's IBOX (IBOX_NOTCH_PIPE points a test copy elsewhere).</summary>
    public static string PipeName() =>
        Environment.GetEnvironmentVariable("IBOX_NOTCH_PIPE") is { Length: > 0 } custom ? custom : NotchBridge.PipeName(Environment.UserName);

    /// <summary>Sends one message; for a request, waits for the decision (null = none).</summary>
    public static async Task<string?> SendAsync(NotchMessage message, string pipeName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (OperatingSystem.IsWindows() && !PipeListed(pipeName))
        {
            // IBOX is not running (or its notch is off): no waiting, the assistant asks at once.
            return null;
        }

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, NotchBridge.Json).AsMemory(), ct).ConfigureAwait(false);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(message.IsAsk ? NotchBridge.AnswerTimeout : TimeSpan.FromSeconds(3));
        var line = await reader.ReadLineAsync(wait.Token).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<NotchReply>(line, NotchBridge.Json)?.Decision;
    }

    /// <summary>Whether a pipe exists, without opening it (opening one would take an instance from its server).</summary>
    public static bool PipeListed(string name)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static async Task<string> ReadAsync(TextReader input, CancellationToken ct)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, n);
            if (sb.Length > MaxInputBytes)
            {
                return string.Empty;
            }
        }

        return sb.ToString();
    }
}
