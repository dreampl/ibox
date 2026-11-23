using System.IO.Pipes;
using System.Text.Json;
using FluentAssertions;
using JackiVpn.Agent;
using JackiVpn.Domain.Agent;
using Xunit;

namespace JackiVpn.UnitTests.Agent;

/// <summary>
/// The whole approve / deny exchange over a real named pipe: Claude Code's hook input on stdin, the notch's session
/// on the other end, the hook's stdout as Claude Code reads it.
/// </summary>
public sealed class NotchApprovalFlowTests
{
    private const string BashRequest = """
        {"session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"C:\\src\\ibox","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"dotnet test tests/JackiVpn.UnitTests","description":"اجرای تست‌ها"}}
        """;

    private static string NewPipe() => "ibox-notch-test-" + Guid.NewGuid().ToString("N")[..12];

    [Theory]
    [InlineData(NotchMessage.Tell, "ok", 1)]
    [InlineData(NotchMessage.Ask, "ask", 0)]
    public async Task Telegram_status_is_informational_and_cannot_request_a_decision(string kind, string expected, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = NewPipe();
        var ready = new TaskCompletionSource();
        var told = new List<NotchMessage>();
        var asked = false;
        var serving = ServeOnceAsync(pipe, (_, _) => { asked = true; return Task.FromResult(NotchBridge.Allow); }, ready, ct, told: told);
        await ready.Task;
        var response = await NotchHook.SendAsync(new NotchMessage { Source = NotchSources.Telegram, Kind = kind, Title = "Telegram sign-in" }, pipe, ct);
        response.Should().Be(expected);
        await serving;
        told.Should().HaveCount(count);
        asked.Should().BeFalse();
    }

    /// <summary>Listens once on <paramref name="pipe"/> and serves one hook connection.</summary>
    private static async Task<string?> ServeOnceAsync(string pipe, Func<NotchMessage, CancellationToken, Task<string>> ask, TaskCompletionSource ready, CancellationToken ct, TimeSpan? window = null, List<NotchMessage>? told = null)
    {
        await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ready.TrySetResult();
        await server.WaitForConnectionAsync(ct);
        return await NotchPipeSession.ServeAsync(server, m =>
        {
            told?.Add(m);
            return Task.CompletedTask;
        }, ask, ct, window);
    }

    private static async Task<(string Stdout, string? Served, NotchMessage? Seen)> RunAsync(string decision, string input = BashRequest)
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = NewPipe();
        var ready = new TaskCompletionSource();
        NotchMessage? seen = null;
        var serving = ServeOnceAsync(pipe, (m, _) =>
        {
            seen = m;
            return Task.FromResult(decision);
        }, ready, ct);
        await ready.Task;
        using var stdout = new StringWriter();
        var exit = await NotchHook.RunAsync(NotchSources.Claude, new StringReader(input), stdout, pipe, ct);
        exit.Should().Be(0);
        return (stdout.ToString().Trim(), await serving, seen);
    }

    [Fact]
    public async Task Allow_prints_the_permission_decision_Claude_Code_reads()
    {
        var (stdout, served, seen) = await RunAsync(NotchBridge.Allow);

        served.Should().Be(NotchBridge.Allow);
        seen!.Detail.Should().Be("dotnet test tests/JackiVpn.UnitTests");
        seen.Note.Should().Be("اجرای تست‌ها");
        using var doc = JsonDocument.Parse(stdout);
        var output = doc.RootElement.GetProperty("hookSpecificOutput");
        output.GetProperty("hookEventName").GetString().Should().Be("PermissionRequest");
        output.GetProperty("decision").GetProperty("behavior").GetString().Should().Be("allow");
    }

    [Fact]
    public async Task Deny_prints_a_deny_with_a_reason()
    {
        var (stdout, served, _) = await RunAsync(NotchBridge.Deny);

        served.Should().Be(NotchBridge.Deny);
        using var doc = JsonDocument.Parse(stdout);
        var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
        decision.GetProperty("behavior").GetString().Should().Be("deny");
        decision.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(NotchBridge.AskThere)]
    [InlineData("ALLOW")]
    [InlineData("")]
    public async Task Anything_but_allow_or_deny_leaves_the_terminal_prompt_in_charge(string decision)
    {
        var (stdout, served, _) = await RunAsync(decision);

        served.Should().Be(NotchBridge.AskThere);
        stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task No_notch_running_prints_nothing_and_exits_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stdout = new StringWriter();
        var started = DateTime.UtcNow;

        var exit = await NotchHook.RunAsync(NotchSources.Claude, new StringReader(BashRequest), stdout, NewPipe(), ct);

        exit.Should().Be(0);
        stdout.ToString().Should().BeEmpty();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Request_is_withdrawn_when_the_hook_goes_away()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = NewPipe();
        var ready = new TaskCompletionSource();
        var shown = new TaskCompletionSource();
        var withdrawn = new TaskCompletionSource();
        var serving = ServeOnceAsync(pipe, async (_, token) =>
        {
            shown.TrySetResult();
            using var reg = token.Register(() => withdrawn.TrySetResult());
            await Task.Delay(Timeout.Infinite, token);
            return NotchBridge.Allow;
        }, ready, ct);
        await ready.Task;

        // The assistant gives up (its timeout, or the user answered in the terminal): the hook process ends.
        using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(2000, ct);
            var line = JsonSerializer.Serialize(NotchBridge.Parse(NotchSources.Claude, BashRequest), NotchBridge.Json) + "\n";
            await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), ct);
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        await withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        (await serving.WaitAsync(TimeSpan.FromSeconds(5), ct)).Should().Be(NotchBridge.AskThere);
    }

    [Fact]
    public async Task Unanswered_request_times_out_to_the_terminal_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = NewPipe();
        var ready = new TaskCompletionSource();
        var serving = ServeOnceAsync(pipe, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return NotchBridge.Allow;
        }, ready, ct, window: TimeSpan.FromMilliseconds(300));
        await ready.Task;
        using var stdout = new StringWriter();

        await NotchHook.RunAsync(NotchSources.Claude, new StringReader(BashRequest), stdout, pipe, ct);

        (await serving).Should().Be(NotchBridge.AskThere);
        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Notifications_are_told_and_never_print_a_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = NewPipe();
        var ready = new TaskCompletionSource();
        var told = new List<NotchMessage>();
        var serving = ServeOnceAsync(pipe, (_, _) => Task.FromResult(NotchBridge.Allow), ready, ct, told: told);
        await ready.Task;
        using var stdout = new StringWriter();
        const string notification = """{"hook_event_name":"Notification","cwd":"C:\\src\\ibox","title":"Claude Code","message":"Claude is waiting for your input"}""";

        await NotchHook.RunAsync(NotchSources.Claude, new StringReader(notification), stdout, pipe, ct);

        (await serving).Should().Be("ok");
        told.Should().ContainSingle().Which.Detail.Should().Be("Claude is waiting for your input");
        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Garbage_on_the_pipe_is_answered_with_ask()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = NewPipe();
        var ready = new TaskCompletionSource();
        var serving = ServeOnceAsync(pipe, (_, _) => Task.FromResult(NotchBridge.Allow), ready, ct);
        await ready.Task;
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000, ct);
        await client.WriteAsync("{not json\n"u8.ToArray(), ct);
        using var reader = new StreamReader(client);

        var reply = JsonSerializer.Deserialize<NotchReply>((await reader.ReadLineAsync(ct))!, NotchBridge.Json);

        reply!.Decision.Should().Be(NotchBridge.AskThere);
        (await serving).Should().BeNull();
    }
}
