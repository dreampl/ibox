using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using JackiVpn.Domain.Bar;

namespace JackiVpn.Agent;

/// <summary>
/// <c>ibar-ctl</c>: scripts and plugins talk to IBAR (the same JSON lines the SDKs send), and Claude Code / Codex
/// hooks reach the notch. The plugin token can be given with --token or IBAR_TOKEN.
/// </summary>
public static class BarCtl
{
    public const string Usage = """
        ibar-ctl — talk to IBAR

          ibar-ctl register MANIFEST.json                     ask the user to approve a plugin; prints its token
          ibar-ctl notify --plugin ID --title T [--body B] [--priority low|normal|high] [--duration S] [--link ACTION]
          ibar-ctl chip   --plugin ID --color #RRGGBB --value N
          ibar-ctl panel  --plugin ID PANEL.json              {"widgets":[...], "buttons":["action-id"]}
          ibar-ctl clear  --plugin ID
          ibar-ctl activity start|update --plugin ID --task T [--title T] [--kind download|render|export|copy|task]
                   [--progress 0..1 --accuracy exact|good|estimate] [--done N] [--total N] [--rate N/s]
                   [--detail D] [--state running|paused|waiting] [--color #RRGGBB]
                                                              a task on the notch's activity circle (no --progress = unknown)
          ibar-ctl activity end --plugin ID --task T [--failed] [--detail D]
          ibar-ctl sound  start|done|error --plugin ID         one of IBAR's three sounds
          ibar-ctl send   JSON|-                              one raw request (schema 1)
          ibar-ctl hook   claude|codex                        Claude Code / Codex hook (requests and states on the notch)

          --token TOKEN or IBAR_TOKEN: the token from register.
        """;

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is ["hook", var source, ..])
        {
            return await NotchHook.RunAsync(source, input, output, null, CancellationToken.None).ConfigureAwait(false);
        }

        PluginRequest? request;
        try
        {
            request = Build(args, input);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        if (request is null)
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var reply = await SendAsync(request, pipeName ?? PipeName(), request.Op == "register" ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(reply, BarPlugins.Json)).ConfigureAwait(false);
            return reply.Status == "ok" ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("IBAR is not running (or did not answer).").ConfigureAwait(false);
            return 3;
        }
    }

    public static string PipeName() => Environment.GetEnvironmentVariable("IBAR_PIPE") is { Length: > 0 } p ? p : BarPlugins.PipeName(Environment.UserName);

    /// <summary>The request a command line means (null = not understood).</summary>
    public static PluginRequest? Build(string[] args, TextReader input)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Opt(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var plugin = Opt("--plugin");
        var token = Opt("--token") ?? Environment.GetEnvironmentVariable("IBAR_TOKEN");
        var positional = args.Skip(1).Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !args[i].StartsWith("--", StringComparison.Ordinal))).ToList();
        switch (args.FirstOrDefault())
        {
            case "register" when positional.Count > 0:
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(positional[0]), BarPlugins.Json);
                return new PluginRequest { Op = "register", Plugin = manifest?.Id, Token = token, Manifest = manifest };
            case "notify" when plugin is not null && Opt("--title") is { } title:
                return new PluginRequest
                {
                    Op = "notify",
                    Plugin = plugin,
                    Token = token,
                    Title = title,
                    Body = Opt("--body"),
                    Priority = Opt("--priority"),
                    Duration = Opt("--duration") is { } d ? int.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null,
                    Link = Opt("--link"),
                };
            case "chip" when plugin is not null:
                return new PluginRequest { Op = "chip", Plugin = plugin, Token = token, Color = Opt("--color"), Value = Opt("--value") };
            case "panel" when plugin is not null && positional.Count > 0:
                var panel = JsonSerializer.Deserialize<PluginRequest>(File.ReadAllText(positional[0]), BarPlugins.Json);
                return new PluginRequest { Op = "panel", Plugin = plugin, Token = token, Widgets = panel?.Widgets, Buttons = panel?.Buttons };
            case "clear" when plugin is not null:
                return new PluginRequest { Op = "clear", Plugin = plugin, Token = token };
            case "activity" when plugin is not null && args.ElementAtOrDefault(1) is "start" or "update" or "end" && Opt("--task") is { } task:
                var op = "activity." + args[1];
                return new PluginRequest
                {
                    Op = op,
                    Plugin = plugin,
                    Token = token,
                    Task = task,
                    Title = Opt("--title"),
                    Kind = Opt("--kind"),
                    Detail = Opt("--detail"),
                    Color = Opt("--color"),
                    State = op == "activity.end" ? (args.Contains("--failed") ? "failed" : "done") : Opt("--state"),
                    Progress = Number(Opt("--progress")),
                    Accuracy = Opt("--accuracy"),
                    Done = Number(Opt("--done")) is { } done ? (long)done : null,
                    Total = Number(Opt("--total")) is { } total ? (long)total : null,
                    Rate = Number(Opt("--rate")),
                };
            case "sound" when plugin is not null && args.ElementAtOrDefault(1) is { } sound:
                return new PluginRequest { Op = "sound", Plugin = plugin, Token = token, Sound = sound };
            case "send" when positional.Count > 0:
                var json = positional[0] == "-" ? input.ReadToEnd() : positional[0];
                return JsonSerializer.Deserialize<PluginRequest>(json, BarPlugins.Json);
            default:
                return null;
        }
    }

    private static double? Number(string? text) =>
        text is null ? null : double.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<PluginReply> SendAsync(PluginRequest request, string pipeName, TimeSpan wait)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, BarPlugins.Json)).ConfigureAwait(false);
        using var answer = new CancellationTokenSource(wait);
        var line = await reader.ReadLineAsync(answer.Token).ConfigureAwait(false) ?? throw new IOException("No answer.");
        return JsonSerializer.Deserialize<PluginReply>(line, BarPlugins.Json) ?? throw new JsonException("Unreadable answer.");
    }
}
