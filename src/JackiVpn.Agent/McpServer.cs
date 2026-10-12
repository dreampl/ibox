using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Agent;

/// <summary>
/// A Model Context Protocol server over stdio (JSON-RPC 2.0, one message per line) that lets AI agents use IBOX:
/// its tools map onto the control channel of the running app, with the same read-only / allow-changes switches.
/// Configure the agent with the command <c>ibox-ctl mcp</c> (Windows) or <c>jackivpn mcp</c> (Linux).
/// </summary>
public sealed class McpServer(string? pipeName = null)
{
    private const string LatestProtocol = "2025-06-18";

    private sealed record Tool(string Name, string Description, JsonObject Schema, string Method, Func<JsonObject?, object?> Params);

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            properties[p.Name] = p.Type == "array"
                ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = p.Description }
                : new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = props.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required);
        }

        return schema;
    }

    private static string? Str(JsonObject? a, string name) => a?[name]?.GetValue<string>();

    private static IReadOnlyList<string>? List(JsonObject? a, string name) => a?[name] is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList() : null;

    private static readonly Tool[] Tools =
    [
        new("ibox_status", "Connection state of IBOX (VPN client): server, exit country and IP, ping, speeds, mode.", Schema(), AgentMethods.Status, _ => null),
        new("ibox_list_servers", "List the VPN servers (id, name, protocol, exit country, measured ping). Optional name filter.",
            Schema(("filter", "string", "Part of a server name", false)), AgentMethods.ListServers, a => new { filter = Str(a, "filter") }),
        new("ibox_connect", "Connect the VPN: to a named server (name, part of a name or id), or with smart connect (fastest working server). Switches server if already connected.",
            Schema(("server", "string", "Server name or id; omit for the current choice", false), ("smart", "boolean", "Use smart connect", false)),
            AgentMethods.Connect, a => new AgentConnectRequest(Str(a, "server"), a?["smart"]?.GetValue<bool>())),
        new("ibox_disconnect", "Disconnect the VPN (also cancels a connection in progress).", Schema(), AgentMethods.Disconnect, _ => null),
        new("ibox_set_mode", "Set the connection mode: tun (all apps) or proxy (system proxy). Applies from the next connection.",
            Schema(("mode", "string", "tun or proxy", true)), AgentMethods.SetMode, a => new { mode = Str(a, "mode") }),
        new("ibox_list_rules", "List the routing rules in order (first match wins): which sites, IPs or apps go direct, through the VPN, or are blocked.", Schema(), AgentMethods.ListRules, _ => null),
        new("ibox_add_rule", "Add a routing rule at the top: domains (suffixes like example.com), IPs/CIDRs and/or app executables go direct, through the VPN (proxy) or are blocked. Applies to a running connection at once.",
            Schema(("name", "string", "Rule name", true), ("action", "string", "direct, proxy or block", true), ("domains", "array", "Domain suffixes", false), ("ips", "array", "IP addresses or CIDRs", false), ("apps", "array", "Executable names, e.g. telegram.exe", false)),
            AgentMethods.AddRule, a => new AgentRuleRequest(Str(a, "name") ?? "Agent rule", Str(a, "action") ?? string.Empty, List(a, "domains"), List(a, "ips"), List(a, "apps"))),
        new("ibox_remove_rule", "Remove a routing rule by id (see ibox_list_rules).", Schema(("id", "string", "Rule id", true)), AgentMethods.RemoveRule, a => new { id = Str(a, "id") }),
        new("ibox_get_dns", "Which DNS resolver IBOX uses outside the tunnel (smart per network, the network's own, or a manual one).", Schema(), AgentMethods.GetDns, _ => null),
        new("ibox_set_dns", "Choose the DNS used outside the tunnel: smart, system, or manual with an IP address (optionally DNS-over-HTTPS with its host name).",
            Schema(("selection", "string", "smart, system or manual", true), ("address", "string", "IP address for manual", false), ("doh", "boolean", "Use DNS-over-HTTPS", false), ("host", "string", "DoH host name", false)),
            AgentMethods.SetDns, a => new AgentDnsRequest(Str(a, "selection") ?? string.Empty, Str(a, "address"), a?["doh"]?.GetValue<bool>() ?? false, Str(a, "host"))),
        new("ibox_speed_test", "Measure download, upload and latency through the VPN (takes about 20 seconds).", Schema(), AgentMethods.SpeedTest, _ => null),
    ];

    public async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input, Encoding.UTF8);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        AgentClient? client = null;
        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    await writer.WriteLineAsync(Error(null, -32700, "Parse error").ToJsonString()).ConfigureAwait(false);
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                var id = message["id"]?.DeepClone();
                var method = message["method"]?.GetValue<string>();
                if (id is null)
                {
                    continue; // a notification (initialized, cancelled…): nothing to answer
                }

                JsonObject reply;
                switch (method)
                {
                    case "initialize":
                        var requested = message["params"]?["protocolVersion"]?.GetValue<string>();
                        reply = Result(id, new JsonObject
                        {
                            ["protocolVersion"] = requested ?? LatestProtocol,
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JsonObject { ["name"] = "ibox", ["title"] = "IBOX VPN", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1" },
                            ["instructions"] = "Controls the IBOX VPN client running on this computer. Changing tools (connect, rules, DNS) work only when the user allowed changes in IBOX settings.",
                        });
                        break;
                    case "ping":
                        reply = Result(id, new JsonObject());
                        break;
                    case "tools/list":
                        reply = Result(id, new JsonObject
                        {
                            ["tools"] = new JsonArray(Tools.Select(t => (JsonNode?)new JsonObject
                            {
                                ["name"] = t.Name,
                                ["description"] = t.Description,
                                ["inputSchema"] = t.Schema.DeepClone(),
                                ["annotations"] = new JsonObject { ["readOnlyHint"] = !AgentMethods.Changing.Contains(t.Method) },
                            }).ToArray()),
                        });
                        break;
                    case "tools/call":
                        var name = message["params"]?["name"]?.GetValue<string>();
                        var tool = Tools.FirstOrDefault(t => t.Name == name);
                        if (tool is null)
                        {
                            reply = Error(id, -32602, "Unknown tool: " + name);
                            break;
                        }

                        string text;
                        var isError = false;
                        try
                        {
                            client ??= new AgentClient(pipeName);
                            var response = await client.CallAsync(tool.Method, tool.Params(message["params"]?["arguments"] as JsonObject), ct).ConfigureAwait(false);
                            isError = !response.Ok;
                            text = response.Ok ? JsonSerializer.Serialize(response.Result, AgentProtocol.Json) : response.Error ?? "Failed.";
                        }
                        catch (Exception ex) when (ex is AgentUnavailableException or IOException or InvalidOperationException or JsonException)
                        {
                            if (client is not null)
                            {
                                await client.DisposeAsync().ConfigureAwait(false);
                                client = null;
                            }

                            isError = true;
                            text = ex.Message;
                        }

                        reply = Result(id, new JsonObject
                        {
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                            ["isError"] = isError,
                        });
                        break;
                    default:
                        reply = Error(id, -32601, "Method not found: " + method);
                        break;
                }

                await writer.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
            }
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
