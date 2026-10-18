using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Agent;
using JackiVpn.Domain.Agent;

namespace JackiVpn.UnitTests.Agent;

/// <summary>Claude Code / Codex hooks on the notch: what is shown, what is answered, and the assistants' settings.</summary>
public class NotchBridgeTests
{
    private const string BashRequest = """
        {"session_id":"abc","cwd":"C:\\work\\ibox","permission_mode":"default","hook_event_name":"PermissionRequest",
         "tool_name":"Bash","tool_input":{"command":"rm -rf node_modules","description":"Remove node_modules directory"}}
        """;

    [Fact]
    public void A_permission_request_shows_what_will_run_and_where()
    {
        var m = NotchBridge.Parse(NotchSources.Claude, BashRequest)!;

        m.IsAsk.Should().BeTrue();
        m.Title.Should().Be("Run this command?");
        m.Detail.Should().Be("rm -rf node_modules");
        m.Note.Should().Be("Remove node_modules directory");
        m.Project.Should().Be("ibox");
        m.Tool.Should().Be("Bash");
    }

    [Theory]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"Edit","tool_input":{"file_path":"/src/a.cs","old_string":"x"}}""", "Change this file?", "/src/a.cs")]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"WebFetch","tool_input":{"url":"https://example.com/x","prompt":"y"}}""", "Open this page?", "https://example.com/x")]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"mcp__github__create_issue","tool_input":{"title":"t"}}""", "Use create_issue (github)?", """{"title":"t"}""")]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"apply_patch","tool_input":{"command":"*** Begin Patch"}}""", "Apply this change?", "*** Begin Patch")]
    public void Each_kind_of_request_reads_naturally(string json, string title, string detail)
    {
        var m = NotchBridge.Parse(NotchSources.Codex, json)!;
        m.Title.Should().Be(title);
        m.Detail.Should().Be(detail);
    }

    [Fact]
    public void Notifications_are_short_and_permission_prompts_are_not_repeated()
    {
        NotchBridge.Parse(NotchSources.Claude, """{"hook_event_name":"Notification","message":"Claude needs your permission","notification_type":"permission_prompt"}""").Should().BeNull();
        var idle = NotchBridge.Parse(NotchSources.Claude, """{"hook_event_name":"Notification","cwd":"/home/a/site","message":"Claude is waiting for your input","notification_type":"idle_prompt"}""")!;
        idle.IsAsk.Should().BeFalse();
        idle.Detail.Should().Be("Claude is waiting for your input");
        idle.Project.Should().Be("site");

        var done = NotchBridge.Parse(NotchSources.Codex, """{"hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"All tests pass."}""")!;
        done.Kind.Should().Be(NotchMessage.StateKind);
        done.State.Should().Be(NotchMessage.Done);
        NotchBridge.Parse(NotchSources.Claude, """{"hook_event_name":"UserPromptSubmit","prompt":"hi"}""")!.State.Should().Be(NotchMessage.Working);
        NotchBridge.Parse(NotchSources.Claude, """{"hook_event_name":"StopFailure","error":"rate limited"}""")!.State.Should().Be(NotchMessage.Failed);
        done.Detail.Should().Be("All tests pass.");
        NotchBridge.Parse(NotchSources.Codex, """{"hook_event_name":"Stop","stop_hook_active":true}""").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"hook_event_name":"PreToolUse"}""")]
    public void Anything_else_is_ignored(string input) => NotchBridge.Parse(NotchSources.Claude, input).Should().BeNull();

    [Fact]
    public void Unknown_sources_are_ignored() => NotchBridge.Parse("other", BashRequest).Should().BeNull();

    [Fact]
    public void Terminal_escapes_and_control_characters_never_reach_the_notch()
    {
        NotchBridge.Clean("\u001b[31mred\u001b[0m text\u0007", 100).Should().Be("red text");
        NotchBridge.Clean("a\u202Eb", 100).Should().Be("a b");
        NotchBridge.Clean(new string('x', 50), 10).Should().Be(new string('x', 10) + "…");
        NotchBridge.Clean("one\ntwo\nthree", 100, lines: 2).Should().Be("one\ntwo three");
    }

    [Fact]
    public void Only_allow_and_deny_are_answers_the_assistant_acts_on()
    {
        JsonNode.Parse(NotchBridge.HookOutput(NotchBridge.Allow)!)!["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>().Should().Be("allow");
        var deny = JsonNode.Parse(NotchBridge.HookOutput(NotchBridge.Deny)!)!["hookSpecificOutput"]!;
        deny["hookEventName"]!.GetValue<string>().Should().Be("PermissionRequest");
        deny["decision"]!["behavior"]!.GetValue<string>().Should().Be("deny");
        NotchBridge.HookOutput(NotchBridge.AskThere).Should().BeNull();
        NotchBridge.HookOutput(null).Should().BeNull();
        NotchBridge.HookOutput("ALLOW").Should().BeNull();
    }

    [Fact]
    public async Task Without_IBOX_the_hook_says_nothing_and_the_assistant_asks_itself()
    {
        var output = new StringWriter();
        var code = await NotchHook.RunAsync(NotchSources.Claude, new StringReader(BashRequest), output, "ibox-notch-test-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        code.Should().Be(0);
        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(NotchBridge.Allow, "allow")]
    [InlineData(NotchBridge.Deny, "deny")]
    [InlineData(NotchBridge.AskThere, null)]
    public async Task The_answer_on_the_notch_becomes_the_hooks_decision(string decision, string? behavior)
    {
        var pipeName = "ibox-notch-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            var request = JsonSerializer.Deserialize<NotchMessage>((await reader.ReadLineAsync())!, NotchBridge.Json)!;
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new NotchReply(decision), NotchBridge.Json));
            return request;
        });

        var output = new StringWriter();
        (await NotchHook.RunAsync(NotchSources.Claude, new StringReader(BashRequest), output, pipeName, CancellationToken.None)).Should().Be(0);

        var seen = await serving;
        seen.IsAsk.Should().BeTrue();
        seen.Detail.Should().Be("rm -rf node_modules");
        if (behavior is null)
        {
            output.ToString().Should().BeEmpty();
        }
        else
        {
            JsonNode.Parse(output.ToString())!["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>().Should().Be(behavior);
        }
    }

    // ------------------------------------------------------------------ the assistants' settings

    private const string Exe = @"C:\Program Files\Jacki VPN\ibox-ctl.exe";

    private const string ExistingClaude = """
        {
          // the user's own settings
          "model": "opus",
          "hooks": {
            "PostToolUse": [ { "matcher": "Edit", "hooks": [ { "type": "command", "command": "lint.sh" } ] } ],
            "Notification": [ { "hooks": [ { "type": "command", "command": "say-it.sh" } ] } ]
          },
        }
        """;

    [Fact]
    public void Connecting_Claude_Code_adds_only_IBOXs_hooks_and_keeps_the_rest()
    {
        var json = AgentHookConfig.ClaudeInstall(ExistingClaude, Exe);
        var root = JsonNode.Parse(json)!;

        root["model"]!.GetValue<string>().Should().Be("opus");
        root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().Should().Be("lint.sh");
        root["hooks"]!["Notification"]!.AsArray().Should().HaveCount(2, "the user's own notification hook stays");
        var ask = root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!;
        ask["command"]!.GetValue<string>().Should().Be(Exe);
        ask["args"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Equal("hook", "claude");
        ask["timeout"]!.GetValue<int>().Should().Be(NotchBridge.HookTimeoutSeconds);
        AgentHookConfig.ClaudeInstalled(json).Should().BeTrue();

        // Connecting again (or from a new install folder) never adds a second copy.
        var again = JsonNode.Parse(AgentHookConfig.ClaudeInstall(json, @"D:\IBOX\ibox-ctl.exe"))!;
        again["hooks"]!["PermissionRequest"]!.AsArray().Should().ContainSingle();
        again["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().Should().Be(@"D:\IBOX\ibox-ctl.exe");
    }

    [Fact]
    public void Disconnecting_Claude_Code_leaves_the_file_as_the_user_had_it()
    {
        var removed = JsonNode.Parse(AgentHookConfig.ClaudeRemove(AgentHookConfig.ClaudeInstall(ExistingClaude, Exe))!)!;

        removed["hooks"]!["PermissionRequest"].Should().BeNull();
        removed["hooks"]!["Notification"]!.AsArray().Should().ContainSingle().Which!["hooks"]![0]!["command"]!.GetValue<string>().Should().Be("say-it.sh");
        removed["hooks"]!["PostToolUse"].Should().NotBeNull();
        AgentHookConfig.ClaudeRemove(ExistingClaude).Should().BeNull("nothing of IBOX's was there");
        AgentHookConfig.ClaudeInstalled(ExistingClaude).Should().BeFalse();
        AgentHookConfig.ClaudeInstall(null, Exe).Should().Contain("PermissionRequest");
        FluentActions.Invoking(() => AgentHookConfig.ClaudeInstall("{ broken", Exe)).Should().Throw<JsonException>("an unreadable file is never overwritten");
    }

    [Fact]
    public void Codex_gets_its_hooks_in_its_own_format()
    {
        AgentHookConfig.CodexCommand(Exe).Should().Be("\"" + Exe + "\" hook codex");
        AgentHookConfig.CodexCommand(@"C:\IBOX\ibox-ctl.exe").Should().Be(@"C:\IBOX\ibox-ctl.exe hook codex");

        var json = AgentHookConfig.CodexInstall("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify.sh"}]}]}}""", AgentHookConfig.CodexCommand(Exe));
        var root = JsonNode.Parse(json)!;
        root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["statusMessage"]!.GetValue<string>().Should().Be(AgentHookConfig.StatusMessage);
        root["hooks"]!["Stop"]!.AsArray().Should().HaveCount(2);
        AgentHookConfig.CodexInstalled(json).Should().BeTrue();

        var removed = JsonNode.Parse(AgentHookConfig.CodexRemove(json)!)!;
        removed["hooks"]!["PermissionRequest"].Should().BeNull();
        removed["hooks"]!["Stop"]!.AsArray().Should().ContainSingle();
    }
}
