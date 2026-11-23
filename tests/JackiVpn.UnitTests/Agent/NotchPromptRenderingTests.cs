using System.Text;
using FluentAssertions;
using JackiVpn.Domain.Agent;

namespace JackiVpn.UnitTests.Agent;

/// <summary>Real-shaped Claude Code permission payloads render readably on the notch (Persian, emoji, long commands, MCP input).</summary>
public class NotchPromptRenderingTests
{
    private static NotchMessage Ask(string toolName, string toolInputJson) =>
        NotchBridge.Parse(NotchSources.Claude, "{\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\w\\\\ibox\",\"tool_name\":\"" + toolName + "\",\"tool_input\":" + toolInputJson + "}")!;

    [Fact]
    public void Persian_text_keeps_its_zero_width_non_joiner()
    {
        var m = Ask("Bash", "{\"command\":\"echo \\\"می\u200Cخواهم\\\"\",\"description\":\"نوشتن یک پیام آزمایشی\"}");
        m.Detail.Should().Be("echo \"می\u200Cخواهم\"");
        m.Note.Should().Be("نوشتن یک پیام آزمایشی");
        NotchBridge.IsRtl(m.Note).Should().BeTrue();
        NotchBridge.IsRtl(m.Detail).Should().BeFalse();
    }

    [Fact]
    public void Bidi_overrides_are_removed_so_the_command_reads_as_it_runs()
    {
        var m = Ask("Bash", "{\"command\":\"echo safe \\u202Ehs.lave\\u202C\"}");
        m.Detail.Should().Be("echo safe hs.lave");
    }

    [Fact]
    public void Emoji_are_never_cut_in_half()
    {
        var cleaned = NotchBridge.Clean(new string('a', 9) + "😀😀", 10)!;
        cleaned.Should().Be(new string('a', 9) + "…");
        cleaned.Any(char.IsSurrogate).Should().BeFalse();
        NotchBridge.Clean("x😀y", 10).Should().Be("x😀y");
    }

    [Fact]
    public void Windows_line_endings_do_not_leave_stray_spaces()
    {
        NotchBridge.Clean("line one\r\nline two", 100, lines: 3).Should().Be("line one\nline two");
    }

    [Fact]
    public void Long_multi_line_commands_keep_more_of_their_lines()
    {
        var command = string.Join("\\n", Enumerable.Range(1, 10).Select(i => "step " + i));
        var m = Ask("Bash", "{\"command\":\"" + command + "\"}");
        m.Detail!.Split('\n').Should().HaveCount(10);
    }

    [Fact]
    public void Mcp_input_is_shown_as_readable_lines_not_escaped_json()
    {
        var m = Ask("mcp__notes__save", "{\"title\":\"یادداشت \\\"اول\\\"\",\"tags\":[\"a\",\"b\"]}");
        m.Title.Should().Be("Use save (notes)?");
        m.Detail.Should().Be("title: یادداشت \"اول\"\ntags: [\"a\",\"b\"]");
    }

    [Fact]
    public void Utf8_stdin_round_trips_through_the_hook_reader()
    {
        const string payload = "{\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo “سلام”\"}}";
        using var reader = new StreamReader(new MemoryStream(new UTF8Encoding(false).GetBytes(payload)), new UTF8Encoding(false));
        NotchBridge.Parse(NotchSources.Claude, reader.ReadToEnd())!.Detail.Should().Be("echo “سلام”");
    }
}
