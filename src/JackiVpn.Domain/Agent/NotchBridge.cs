using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JackiVpn.Domain.Agent;

/// <summary>
/// The notch bridge: Claude Code and Codex hooks (<c>ibox-ctl hook claude|codex</c>) hand their permission requests
/// and notifications to the IBOX notch over a pipe only the same OS user can open. A request the user does not answer
/// on the notch (no IBOX, notch off, "answer there", timeout) leaves the assistant's own prompt in charge: the hook
/// then prints nothing.
/// </summary>
public static class NotchBridge
{
    public const int Version = 1;

    /// <summary>Longest request line accepted (a hostile or stuck client cannot grow it).</summary>
    public const int MaxLineBytes = 16 * 1024;

    /// <summary>How long a hook waits for an answer on the notch before the assistant asks the usual way.</summary>
    public static TimeSpan AnswerTimeout { get; } = TimeSpan.FromSeconds(110);

    /// <summary>The hook timeout written into the assistants' configuration (a little above <see cref="AnswerTimeout"/>).</summary>
    public const int HookTimeoutSeconds = 120;

    public const string Allow = "allow";
    public const string Deny = "deny";

    /// <summary>No decision: the assistant shows its own prompt.</summary>
    public const string AskThere = "ask";

    public static string PipeName(string user) => "ibox-notch-" + new string(user.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray()).ToLowerInvariant();

    public static JsonSerializerOptions Json => AgentProtocol.Json;

    /// <summary>
    /// What a hook's input means for the notch: a request to answer (<see cref="NotchMessage.IsAsk"/>), a short
    /// notification, or nothing worth showing (null).
    /// </summary>
    public static NotchMessage? Parse(string source, string hookInput)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(hookInput) || source is not (NotchSources.Claude or NotchSources.Codex))
        {
            return null;
        }

        JsonObject? input;
        try
        {
            input = JsonNode.Parse(hookInput) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (input is null)
        {
            return null;
        }

        var project = Project(Text(input, "cwd"));
        switch (Text(input, "hook_event_name"))
        {
            case "PermissionRequest":
            {
                var tool = Text(input, "tool_name") ?? "a tool";
                var toolInput = input["tool_input"] as JsonObject;
                return new NotchMessage
                {
                    Kind = NotchMessage.Ask,
                    Source = source,
                    Tool = Clean(tool, 80),
                    Title = AskTitle(tool),
                    Detail = Clean(Detail(tool, toolInput), 600, lines: 6),
                    Note = Clean(Text(toolInput, "description"), 160),
                    Project = project,
                };
            }

            case "Notification":
                // Permission prompts come through PermissionRequest; sign-in confirmations are not news.
                if (Text(input, "notification_type") is "permission_prompt" or "auth_success")
                {
                    return null;
                }

                return Tell(source, Clean(Text(input, "title"), 80) ?? "Needs your attention", Clean(Text(input, "message"), 220, lines: 3), project);

            case "UserPromptSubmit":
                return State(source, NotchMessage.Working, project);

            case "Stop":
                return input["stop_hook_active"]?.GetValueKind() == JsonValueKind.True ? null : State(source, NotchMessage.Done, project) with { Detail = Clean(Text(input, "last_assistant_message"), 220, lines: 3) };

            case "StopFailure":
                return State(source, NotchMessage.Failed, project) with { Detail = Clean(Text(input, "error") ?? Text(input, "message"), 220, lines: 3) };

            default:
                return null;
        }
    }

    private static NotchMessage State(string source, string state, string? project) => new()
    {
        Kind = NotchMessage.StateKind,
        Source = source,
        Title = state,
        State = state,
        Project = project,
    };

    private static NotchMessage Tell(string source, string title, string? detail, string? project) => new()
    {
        Kind = NotchMessage.Tell,
        Source = source,
        Title = title,
        Detail = detail,
        Project = project,
    };

    /// <summary>What the hook prints for a decision (nothing when the assistant should ask itself).</summary>
    public static string? HookOutput(string? decision) => decision switch
    {
        Allow => """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""",
        Deny => """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"Denied on the IBOX notch."}}}""",
        _ => null,
    };

    public static string AppName(string source) => source switch { NotchSources.Codex => "Codex", "plugin" => "IBAR", "chat" => "IBAR chat", _ => "Claude Code" };

    private static string AskTitle(string tool) => tool switch
    {
        "Bash" or "PowerShell" or "shell" or "exec_command" => "Run this command?",
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "Change this file?",
        "apply_patch" => "Apply this change?",
        "WebFetch" => "Open this page?",
        "WebSearch" => "Search the web?",
        "Read" => "Read this file?",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.Split("__") is [_, var server, var name, ..] => $"Use {name} ({server})?",
        _ => $"Use {tool}?",
    };

    /// <summary>The one thing worth reading before allowing: the command, the file, the address, the query.</summary>
    private static string? Detail(string tool, JsonObject? toolInput)
    {
        if (toolInput is null)
        {
            return null;
        }

        foreach (var key in tool switch
        {
            "WebFetch" => (string[])["url"],
            "WebSearch" => ["query"],
            "Edit" or "MultiEdit" or "Write" or "Read" => ["file_path"],
            "NotebookEdit" => ["notebook_path"],
            _ => ["command", "cmd", "file_path", "path", "url", "query", "pattern"],
        })
        {
            var node = toolInput[key];
            if (node is JsonArray parts)
            {
                return string.Join(' ', parts.Select(p => p?.ToString()));
            }

            if (Text(toolInput, key) is { Length: > 0 } value)
            {
                return value;
            }
        }

        var json = toolInput.ToJsonString();
        return json.Length > 2 ? json : null;
    }

    private static string? Project(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var trimmed = cwd.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return Clean(cut >= 0 ? trimmed[(cut + 1)..] : trimmed, 60);
    }

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Printable text only (no terminal escapes or control characters), shortened to fit the notch.</summary>
    public static string? Clean(string? text, int max, int lines = 1)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var lineCount = 1;
        var truncated = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (sb.Length >= max)
            {
                truncated = !string.IsNullOrWhiteSpace(text[i..]);
                break;
            }

            var c = text[i];
            if (c == '\u001b')
            {
                // An ANSI escape: skip to its final letter.
                while (i + 1 < text.Length && !char.IsAsciiLetter(text[i + 1]))
                {
                    i++;
                }

                i++;
                continue;
            }

            if (c == '\n' && lines > 1 && lineCount < lines)
            {
                lineCount++;
                sb.Append('\n');
            }
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format)
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : truncated ? result + "…" : result;
    }
}

public static class NotchSources
{
    public const string Claude = "claude";
    public const string Codex = "codex";
}

/// <summary>One line from a hook to the notch.</summary>
public sealed record NotchMessage
{
    public const string Ask = "ask";
    public const string Tell = "tell";
    public const string StateKind = "state";
    public const string Working = "working";
    public const string Done = "done";
    public const string Failed = "error";

    public int V { get; init; } = NotchBridge.Version;

    /// <summary><see cref="Ask"/> (a permission to answer) or <see cref="Tell"/> (a notification).</summary>
    public string Kind { get; init; } = Tell;

    public string Source { get; init; } = NotchSources.Claude;

    public string Title { get; init; } = string.Empty;

    public string? Detail { get; init; }

    /// <summary>The assistant's own words about the request ("Remove the build folder").</summary>
    public string? Note { get; init; }

    public string? Tool { get; init; }

    /// <summary>The project folder's name.</summary>
    public string? Project { get; init; }

    /// <summary>For <see cref="StateKind"/>: working, done or error.</summary>
    public string? State { get; init; }

    public bool IsAsk => Kind == Ask;
}

/// <summary>The notch's answer: allow, deny or ask (the assistant asks the usual way).</summary>
public sealed record NotchReply(string Decision);
