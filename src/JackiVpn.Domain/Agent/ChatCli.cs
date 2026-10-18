using System.Text.Json;
using System.Text.Json.Nodes;

namespace JackiVpn.Domain.Agent;

/// <summary>
/// Chat on the notch runs the user's own Claude Code (<c>claude -p</c>) or Codex (<c>codex exec</c>). This finds them
/// where they really are and reads Claude Code's streamed answer.
/// <para>
/// Why chat did not work (found on this PC, not guessed): <c>claude</c> was not on the PATH at all — the Claude
/// desktop app keeps its own copy under %APPDATA%\Claude\claude-code\&lt;version&gt;\&lt;build&gt;\claude.exe (and, for the
/// Store app, under %LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\…), so IBAR answered "not found"; Codex sits
/// under %LOCALAPPDATA%\OpenAI\Codex\bin\&lt;build&gt;\codex.exe, also off the PATH. Started directly, that claude.exe
/// works but is signed in only inside the desktop app (it passes its sign-in to the copies it starts), so on its
/// own it says "Not logged in · Please run /login" — which IBAR now shows with a Sign-in button instead of failing
/// silently. And <c>--output-format stream-json</c> needs <c>--verbose</c> with <c>-p</c>.
/// </para>
/// </summary>
public static class ChatCli
{
    /// <summary>The newest copy among candidate paths of claude.exe under a "…\claude-code\&lt;version&gt;\&lt;build&gt;\" layout.</summary>
    public static string? Newest(IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Select(path => (Path: path, Version: VersionOf(path)))
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    /// <summary>The version folder above the build folder (claude-code\2.1.295\d2e2…\claude.exe → 2.1.295); 0.0 when none.</summary>
    public static Version VersionOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts.Reverse())
        {
            if (System.Version.TryParse(part, out var v) && part.Count(c => c == '.') >= 1)
            {
                return v;
            }
        }

        return new Version(0, 0);
    }

    /// <summary>The fixed arguments (the question goes to standard input, never onto a command line).</summary>
    public static IReadOnlyList<string> ClaudeArguments(bool mayChange, string? settingsFile)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-mode", mayChange ? "default" : "plan" };
        if (settingsFile is not null)
        {
            // IBAR's hooks for this run only (each change then asks Allow / Deny on the notch).
            args.Add("--settings");
            args.Add(settingsFile);
        }

        return args;
    }

    public static IReadOnlyList<string> CodexArguments(bool mayChange) =>
        ["exec", "--sandbox", mayChange ? "workspace-write" : "read-only", "--skip-git-repo-check", "--color", "never", "-"];

    /// <summary>What a line of Claude Code's stream-json says.</summary>
    public sealed record StreamPiece(string Kind, string? Text = null, bool Error = false);

    /// <summary>
    /// One line of <c>--output-format stream-json</c>: a piece of text as it is written ("delta"), a finished message
    /// ("message"), the end ("result", with is_error), or the session start ("init"); plain text (Claude Code prints
    /// "Not logged in…" as text) comes back as "text". Null for lines with nothing to show.
    /// </summary>
    public static StreamPiece? Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        JsonObject? o;
        try
        {
            o = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return new StreamPiece("text", line);
        }

        if (o is null)
        {
            return new StreamPiece("text", line);
        }

        switch (Str(o, "type"))
        {
            case "system" when Str(o, "subtype") == "init":
                return new StreamPiece("init", Str(o, "model"));
            case "stream_event":
                var e = o["event"] as JsonObject;
                if (e is not null && Str(e, "type") == "content_block_delta" && e["delta"] is JsonObject d && Str(d, "type") == "text_delta")
                {
                    return new StreamPiece("delta", Str(d, "text"));
                }

                return null;
            case "assistant":
                var texts = (o["message"]?["content"] as JsonArray)?.OfType<JsonObject>().Where(c => Str(c, "type") == "text").Select(c => Str(c, "text")).Where(t => t is not null);
                return texts is null ? null : new StreamPiece("message", string.Concat(texts));
            case "result":
                var isError = o["is_error"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                return new StreamPiece("result", Str(o, "result"), isError);
            default:
                return null;
        }
    }

    /// <summary>Claude Code says it is not signed in.</summary>
    public static bool NeedsLogin(string? text) =>
        text is not null && (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
