using System.Text;
using System.Text.Json;

namespace JackiVpn.Domain.Agent;

/// <summary>
/// The notch's side of one hook connection: one <see cref="NotchMessage"/> line in, one <see cref="NotchReply"/> line
/// out. A request is answered only while the hook still waits: when the assistant gives up (its own timeout, or the
/// user answered in the terminal) the pipe closes, the answer is withdrawn and nothing is written. Kept free of UI so
/// the whole exchange can be tested over a real pipe.
/// </summary>
public static class NotchPipeSession
{
    /// <summary>How long the first line may take to arrive.</summary>
    public static TimeSpan ReadTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a request stays open on the notch (a little under the hook's own wait).</summary>
    public static TimeSpan AskWindow { get; } = NotchBridge.AnswerTimeout - TimeSpan.FromSeconds(2);

    /// <summary>
    /// Serves one connection. <paramref name="tell"/> takes notifications and states; <paramref name="ask"/> shows a
    /// request and returns allow / deny / ask, observing the token (cancelled when the hook goes away or time runs
    /// out). Returns the decision for a request, "ok" for anything else, null for input that is not ours.
    /// </summary>
    public static async Task<string?> ServeAsync(
        Stream pipe,
        Func<NotchMessage, Task> tell,
        Func<NotchMessage, CancellationToken, Task<string>> ask,
        CancellationToken ct,
        TimeSpan? askWindow = null)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(tell);
        ArgumentNullException.ThrowIfNull(ask);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
        NotchMessage? message = null;
        try
        {
            message = line is null ? null : JsonSerializer.Deserialize<NotchMessage>(line, NotchBridge.Json);
        }
        catch (JsonException)
        {
            // answered below
        }

        if (message is null || message.V != NotchBridge.Version || message.Source is not (NotchSources.Claude or NotchSources.Codex))
        {
            await ReplyAsync(writer, NotchBridge.AskThere).ConfigureAwait(false);
            return null;
        }

        if (!message.IsAsk)
        {
            await tell(message).ConfigureAwait(false);
            await ReplyAsync(writer, "ok").ConfigureAwait(false);
            return "ok";
        }

        using var answering = CancellationTokenSource.CreateLinkedTokenSource(ct);
        answering.CancelAfter(askWindow ?? AskWindow);
        var gone = WatchClosedAsync(reader, answering);
        string decision;
        try
        {
            decision = Normalize(await ask(message, answering.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            decision = NotchBridge.AskThere;
        }

        if (gone.IsCompleted && await gone.ConfigureAwait(false))
        {
            // Nobody to tell: the assistant already moved on.
            return NotchBridge.AskThere;
        }

        try
        {
            await ReplyAsync(writer, decision).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The hook left between the answer and the reply.
            return NotchBridge.AskThere;
        }

        return decision;
    }

    /// <summary>Only allow and deny are decisions; anything else lets the assistant ask in its own window.</summary>
    public static string Normalize(string? decision) => decision is NotchBridge.Allow or NotchBridge.Deny ? decision : NotchBridge.AskThere;

    /// <summary>True when the hook closed its end (the assistant stopped waiting).</summary>
    private static async Task<bool> WatchClosedAsync(StreamReader reader, CancellationTokenSource answering)
    {
        try
        {
            var buffer = new char[1];
            if (await reader.ReadAsync(buffer.AsMemory(), answering.Token).ConfigureAwait(false) == 0)
            {
                try
                {
                    await answering.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // the session already ended
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // done
        }

        return false;
    }

    private static Task ReplyAsync(StreamWriter writer, string decision) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(new NotchReply(decision), NotchBridge.Json));

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        var sb = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false) > 0)
        {
            if (buffer[0] == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }

            if (sb.Length >= NotchBridge.MaxLineBytes)
            {
                return null;
            }

            sb.Append(buffer[0]);
        }

        return null;
    }
}
