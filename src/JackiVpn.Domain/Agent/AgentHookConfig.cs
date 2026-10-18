using System.Text.Json;
using System.Text.Json.Nodes;

namespace JackiVpn.Domain.Agent;

/// <summary>
/// Adds or removes IBOX's notch hooks in the assistants' own configuration, leaving everything else in the file as
/// it was: Claude Code (<c>~/.claude/settings.json</c>: permission requests and notifications) and Codex
/// (<c>~/.codex/hooks.json</c>: permission requests and finished turns; Codex asks the user to trust new hooks once
/// with <c>/hooks</c>). Only entries that run <c>ibox-ctl … hook</c> are ever changed.
/// </summary>
public static class AgentHookConfig
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string StatusMessage = "Waiting for your answer on the IBOX notch";

    /// <summary>Claude Code's settings with IBOX's hooks (an older IBOX entry is replaced, other hooks are kept).</summary>
    public static string ClaudeInstall(string? json, string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var root = Load(json);
        var hooks = Strip(root, IsOursClaude);
        Add(hooks, "PermissionRequest", ClaudeHandler(exePath, NotchBridge.HookTimeoutSeconds));
        Add(hooks, "Notification", ClaudeHandler(exePath, 10));
        Add(hooks, "UserPromptSubmit", ClaudeHandler(exePath, 5));
        Add(hooks, "Stop", ClaudeHandler(exePath, 5));
        return root.ToJsonString(Indented);
    }

    /// <summary>Claude Code's settings without IBOX's hooks; null when there was nothing to remove.</summary>
    public static string? ClaudeRemove(string? json)
    {
        if (!ClaudeInstalled(json))
        {
            return null;
        }

        var root = Load(json);
        Strip(root, IsOursClaude);
        return root.ToJsonString(Indented);
    }

    public static bool ClaudeInstalled(string? json) => Installed(json, IsOursClaude);

    /// <summary>Codex's hooks file with IBOX's hooks; <paramref name="command"/> is the full command line to run.</summary>
    public static string CodexInstall(string? json, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var root = Load(json);
        var hooks = Strip(root, IsOursCodex);
        Add(hooks, "PermissionRequest", new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = NotchBridge.HookTimeoutSeconds,
            ["statusMessage"] = StatusMessage,
        });
        Add(hooks, "Stop", new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 10 });
        Add(hooks, "UserPromptSubmit", new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 });
        return root.ToJsonString(Indented);
    }

    public static string? CodexRemove(string? json)
    {
        if (!CodexInstalled(json))
        {
            return null;
        }

        var root = Load(json);
        Strip(root, IsOursCodex);
        return root.ToJsonString(Indented);
    }

    public static bool CodexInstalled(string? json) => Installed(json, IsOursCodex);

    /// <summary>The Codex command for ibox-ctl: a path with spaces is quoted.</summary>
    public static string CodexCommand(string exePath) =>
        (exePath.Contains(' ', StringComparison.Ordinal) ? "\"" + exePath + "\"" : exePath) + " hook " + NotchSources.Codex;

    private static JsonObject ClaudeHandler(string exePath, int timeout) => new()
    {
        ["type"] = "command",
        ["command"] = exePath,
        ["args"] = new JsonArray("hook", NotchSources.Claude),
        ["timeout"] = timeout,
    };

    private static bool IsOursClaude(JsonObject handler) =>
        handler["command"]?.GetValueKind() == JsonValueKind.String
        && handler["command"]!.GetValue<string>() is var command
        && (command.EndsWith("ibox-ctl.exe", StringComparison.OrdinalIgnoreCase) || command.EndsWith("ibox-ctl", StringComparison.OrdinalIgnoreCase) || command.EndsWith("ibar-ctl.exe", StringComparison.OrdinalIgnoreCase)
            ? handler["args"] is JsonArray { Count: > 0 } args && args[0]?.ToString() == "hook"
            : (command.Contains("ibox-ctl", StringComparison.OrdinalIgnoreCase) || command.Contains("ibar-ctl", StringComparison.OrdinalIgnoreCase)) && command.Contains(" hook ", StringComparison.Ordinal));

    private static bool IsOursCodex(JsonObject handler) =>
        handler["command"]?.GetValueKind() == JsonValueKind.String
        && handler["command"]!.GetValue<string>() is var command
        && (command.Contains("ibox-ctl", StringComparison.OrdinalIgnoreCase) || command.Contains("ibar-ctl", StringComparison.OrdinalIgnoreCase))
        && command.Contains(" hook ", StringComparison.Ordinal);

    private static JsonObject Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        // Unreadable settings are never overwritten: the caller tells the user instead.
        return JsonNode.Parse(json, documentOptions: Lenient) as JsonObject
            ?? throw new InvalidDataException("The settings file is not a JSON object.");
    }

    private static bool Installed(string? json, Func<JsonObject, bool> ours)
    {
        try
        {
            return Load(json)["hooks"] is JsonObject hooks
                && hooks.Select(e => e.Value).OfType<JsonArray>().SelectMany(g => g.OfType<JsonObject>())
                    .Any(g => g["hooks"] is JsonArray list && list.OfType<JsonObject>().Any(ours));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Removes IBOX's handlers (and groups or events left empty); returns the hooks object.</summary>
    private static JsonObject Strip(JsonObject root, Func<JsonObject, bool> ours)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
            return hooks;
        }

        foreach (var name in hooks.Select(e => e.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups)
            {
                continue;
            }

            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray list)
                {
                    continue;
                }

                foreach (var handler in list.OfType<JsonObject>().Where(ours).ToList())
                {
                    list.Remove(handler);
                }

                if (list.Count == 0)
                {
                    groups.Remove(group);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(name);
            }
        }

        return hooks;
    }

    private static void Add(JsonObject hooks, string eventName, JsonObject handler)
    {
        if (hooks[eventName] is not JsonArray groups)
        {
            groups = new JsonArray();
            hooks[eventName] = groups;
        }

        groups.Add(new JsonObject { ["hooks"] = new JsonArray(handler) });
    }
}
