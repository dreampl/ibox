using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Agent;
using JackiVpn.Domain.Agent;

namespace JackiVpn.UnitTests.Application;

public class AgentTests
{
    private static async Task<List<JsonObject>> McpAsync(params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        var output = new MemoryStream();
        await new McpServer("ibox-agent-test-" + Guid.NewGuid().ToString("N")[..8]).RunAsync(input, output, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();
    }

    [Fact]
    public async Task Mcp_handshake_lists_tools_and_marks_the_changing_ones()
    {
        var replies = await McpAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        replies.Should().HaveCount(2, "notifications get no answer");
        replies[0]["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18");
        var tools = replies[1]["result"]!["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        tools.Select(t => t["name"]!.GetValue<string>()).Should().Contain(["ibox_status", "ibox_connect", "ibox_add_rule"]);
        tools.Single(t => t["name"]!.GetValue<string>() == "ibox_status")["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        tools.Single(t => t["name"]!.GetValue<string>() == "ibox_connect")["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Tool_call_without_a_running_ibox_is_a_tool_error_not_a_crash()
    {
        var replies = await McpAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"ibox_status","arguments":{}}}""");
        replies.Single()["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        replies.Single()["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("not running");
    }

    [Fact]
    public async Task Unknown_methods_and_tools_are_json_rpc_errors()
    {
        var replies = await McpAsync(
            """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rm_rf"}}""");
        replies[0]["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        replies[1]["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    [Fact]
    public void Pipe_name_is_per_user_and_safe() =>
        AgentProtocol.PipeName("Arsham Kh/../x").Should().Be("ibox-agent-arshamkhx");

    [Fact]
    public void Every_changing_method_is_a_known_method() =>
        AgentMethods.Changing.Should().BeSubsetOf(AgentMethods.All);
}
