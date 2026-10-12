using System.Globalization;
using System.Text.Json;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Agent;

/// <summary>
/// The command line for a running IBOX (Windows: ibox-ctl.exe; Linux: <c>jackivpn ctl …</c>), and the entry to the
/// MCP server for AI agents (<c>mcp</c>). Output is short text, or JSON with <c>--json</c>.
/// </summary>
public static class AgentCli
{
    public static string Usage(string exe) => $"""
        IBOX control (talks to the running app; turn on "AI agents" in IBOX settings first)

          {exe} status                         connection state, server, exit, ping
          {exe} servers [filter]               servers (id, name, protocol, country, ping)
          {exe} connect [server|--smart]       connect (a name, part of a name or an id)
          {exe} disconnect                     disconnect (or cancel while connecting)
          {exe} mode tun|proxy                 connection mode for the next connection
          {exe} rules                          routing rules (first match wins)
          {exe} rule add NAME direct|proxy|block [--domain a.com,b.com] [--ip 1.2.3.0/24] [--app telegram.exe]
          {exe} rule remove ID
          {exe} dns                            which DNS is used outside the tunnel
          {exe} dns smart|system               smart (fastest clean per network) or the network's own
          {exe} dns manual ADDRESS [--doh HOST] a DNS of your own
          {exe} speedtest                      download / upload / ping through the VPN
          {exe} mcp                            MCP server on stdin/stdout for AI agents (Claude, …)

          --json   print the raw answer
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string exe, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            await output.WriteLineAsync(Usage(exe)).ConfigureAwait(false);
            return 0;
        }

        if (args[0] == "mcp")
        {
            await new McpServer(pipeName).RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        var json = args.Contains("--json");
        var rest = args.Where(a => a != "--json").ToArray();
        (string Method, object? Params)? call = rest switch
        {
            ["status"] => (AgentMethods.Status, null),
            ["servers"] => (AgentMethods.ListServers, null),
            ["servers", var f, ..] => (AgentMethods.ListServers, new { filter = f }),
            ["connect", "--smart"] => (AgentMethods.Connect, new AgentConnectRequest(Smart: true)),
            ["connect"] => (AgentMethods.Connect, new AgentConnectRequest()),
            ["connect", .. var name] => (AgentMethods.Connect, new AgentConnectRequest(Server: string.Join(' ', name))),
            ["disconnect"] or ["cancel"] => (AgentMethods.Disconnect, null),
            ["mode", var m] => (AgentMethods.SetMode, new { mode = m }),
            ["rules"] => (AgentMethods.ListRules, null),
            ["rule", "add", var n, var action, .. var opts] => (AgentMethods.AddRule, new AgentRuleRequest(n, action, Opt(opts, "--domain"), Opt(opts, "--ip"), Opt(opts, "--app"))),
            ["rule", "remove", var id] => (AgentMethods.RemoveRule, new { id }),
            ["dns"] => (AgentMethods.GetDns, null),
            ["dns", "manual", var address, "--doh", var host] => (AgentMethods.SetDns, new AgentDnsRequest("manual", address, true, host)),
            ["dns", "manual", var address] => (AgentMethods.SetDns, new AgentDnsRequest("manual", address)),
            ["dns", var selection] => (AgentMethods.SetDns, new AgentDnsRequest(selection)),
            ["speedtest"] => (AgentMethods.SpeedTest, null),
            _ => null,
        };
        if (call is not { } c)
        {
            await error.WriteLineAsync(Usage(exe)).ConfigureAwait(false);
            return 2;
        }

        try
        {
            await using var client = new AgentClient(pipeName);
            var response = await client.CallAsync(c.Method, c.Params, CancellationToken.None).ConfigureAwait(false);
            if (!response.Ok)
            {
                await error.WriteLineAsync(response.Error).ConfigureAwait(false);
                return 1;
            }

            await output.WriteLineAsync(json || response.Result is not { } r ? JsonSerializer.Serialize(response.Result, Pretty) : Text(c.Method, r)).ConfigureAwait(false);
            return 0;
        }
        catch (AgentUnavailableException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 3;
        }
    }

    private static readonly JsonSerializerOptions Pretty = new(AgentProtocol.Json) { WriteIndented = true };

    private static IReadOnlyList<string>? Opt(string[] opts, string name)
    {
        var i = Array.IndexOf(opts, name);
        return i >= 0 && i + 1 < opts.Length ? opts[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
    }

    /// <summary>Short human-readable text for the common answers.</summary>
    private static string Text(string method, JsonElement result)
    {
        switch (method)
        {
            case AgentMethods.Status:
            case AgentMethods.Connect:
            case AgentMethods.Disconnect:
                var s = result.Deserialize<AgentStatus>(AgentProtocol.Json)!;
                var parts = new List<string> { s.State };
                if (s.Server is { } server)
                {
                    parts.Add(server);
                }

                if (s.Country is { } country)
                {
                    parts.Add(country + (s.City is { } city ? ", " + city : string.Empty));
                }

                if (s.LatencyMs is { } ms)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms"));
                }

                if (s.Mode is { } mode)
                {
                    parts.Add(mode.ToUpperInvariant());
                }

                return string.Join(" · ", parts) + (s.LastError is { } e ? Environment.NewLine + e : string.Empty);
            case AgentMethods.ListServers:
                var servers = result.Deserialize<List<AgentServer>>(AgentProtocol.Json)!;
                return servers.Count == 0 ? "No servers." : string.Join(Environment.NewLine, servers.Select(x =>
                    string.Create(CultureInfo.InvariantCulture, $"{(x.Active ? "●" : x.Selected ? "○" : " ")} {x.Name,-34} {x.Protocol,-12} {x.Country ?? "--",-3} {(x.LatencyMs is { } l ? $"{l:0} ms" : "—"),8}  {x.Id}")));
            case AgentMethods.ListRules:
                var rules = result.Deserialize<List<AgentRule>>(AgentProtocol.Json)!;
                return rules.Count == 0 ? "No rules." : string.Join(Environment.NewLine, rules.Select(x => $"{(x.Enabled ? " " : "-")} {x.Action,-6} {x.Name} — {x.Summary}  [{x.Id}]"));
            case AgentMethods.GetDns:
            case AgentMethods.SetDns:
                var d = result.Deserialize<AgentDns>(AgentProtocol.Json)!;
                return $"{d.Selection}: {d.InUse}" + (d.UseForDirect ? " (also for direct sites)" : string.Empty);
            default:
                return JsonSerializer.Serialize(result, Pretty);
        }
    }
}
