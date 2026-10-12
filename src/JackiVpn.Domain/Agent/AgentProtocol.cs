using System.Text.Json;
using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Agent;

/// <summary>
/// The local control channel of a running IBOX (for the command line and AI agents through MCP). One JSON object per
/// line each way over a pipe only the same OS user can open. Off unless the user turns it on; changing anything
/// (connect, rules, DNS) needs a second switch. Server credentials are never part of any answer.
/// </summary>
public static class AgentProtocol
{
    public const int Version = 1;

    /// <summary>Pipe name: per user, so two accounts on one machine never meet.</summary>
    public static string PipeName(string user) => "ibox-agent-" + new string(user.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray()).ToLowerInvariant();

    public const int MaxLineBytes = 64 * 1024;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Answers go to terminals and JSON-RPC, never into HTML: keep quotes and non-ASCII names readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public static class AgentMethods
{
    public const string Status = "status";
    public const string ListServers = "servers.list";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string SetMode = "mode.set";
    public const string ListRules = "rules.list";
    public const string AddRule = "rules.add";
    public const string RemoveRule = "rules.remove";
    public const string GetDns = "dns.get";
    public const string SetDns = "dns.set";
    public const string SpeedTest = "speedtest.run";

    /// <summary>Methods that change something (need "allow changes").</summary>
    public static readonly IReadOnlySet<string> Changing = new HashSet<string>(StringComparer.Ordinal) { Connect, Disconnect, SetMode, AddRule, RemoveRule, SetDns };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Status, ListServers, Connect, Disconnect, SetMode, ListRules, AddRule, RemoveRule, GetDns, SetDns, SpeedTest };
}

public sealed record AgentRequest(int Id, string Method, JsonElement? Params = null);

public sealed record AgentResponse(int Id, bool Ok, JsonElement? Result = null, string? Error = null);

public sealed record AgentStatus
{
    public required string State { get; init; }
    public string? Server { get; init; }
    public string? Mode { get; init; }
    public string? Country { get; init; }
    public string? City { get; init; }
    public string? PublicIp { get; init; }
    public double? LatencyMs { get; init; }
    public long? DownloadBytesPerSecond { get; init; }
    public long? UploadBytesPerSecond { get; init; }
    public DateTimeOffset? ConnectedSinceUtc { get; init; }
    public string? LastError { get; init; }
    public bool SmartConnect { get; init; }
}

public sealed record AgentServer(string Id, string Name, string Protocol, string? Country, double? LatencyMs, string Health, bool Selected, bool Active);

public sealed record AgentRule(string Id, string Name, string Action, bool Enabled, string Summary);

/// <summary>rules.add: a rule from plain lists (domain suffixes, IPs/CIDRs, app executables); action direct, proxy or block.</summary>
public sealed record AgentRuleRequest(string Name, string Action, IReadOnlyList<string>? Domains = null, IReadOnlyList<string>? Ips = null, IReadOnlyList<string>? Apps = null);

public sealed record AgentConnectRequest(string? Server = null, bool? Smart = null);

public sealed record AgentDnsRequest(string Selection, string? Address = null, bool Doh = false, string? Host = null);

public sealed record AgentDns(string Selection, string InUse, bool UseForDirect);
