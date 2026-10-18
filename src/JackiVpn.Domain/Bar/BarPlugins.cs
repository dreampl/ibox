using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Bar;

/// <summary>
/// IBAR's plugin API (Alita and others, in any language): one JSON object per line over a pipe only the same OS user
/// can open (also through <c>ibar-ctl</c>). A plugin registers once with a manifest and the user approves it on the
/// notch; after that it may show notifications, a status chip (coloured circle + number), a small dashboard
/// (number, bar, short list, sparkline) and buttons for the actions it declared. Everything is declarative JSON — no
/// code, no markup, no pictures from the network; an action is only ever a command or web address that was in the
/// approved manifest. Schema version <see cref="Version"/>.
/// </summary>
public static class BarPlugins
{
    public const int Version = 1;
    public const int MaxLineBytes = 32 * 1024;
    public const int MaxPlugins = 16;

    public static string PipeName(string user) => "ibar-" + new string(user.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray()).ToLowerInvariant();

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What is wrong with a manifest (null = acceptable).</summary>
    public static string? Problem(PluginManifest? m)
    {
        if (m is null)
        {
            return "No manifest.";
        }

        if (m.Schema != Version)
        {
            return $"Unsupported schema {m.Schema} (IBAR speaks {Version}).";
        }

        if (!IsId(m.Id))
        {
            return "id: 2–40 characters, a–z, 0–9, - and _.";
        }

        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 40 || m.Name.Any(char.IsControl))
        {
            return "name: 1–40 printable characters.";
        }

        if (m.Icon is { } icon && (!Path.IsPathFullyQualified(icon) || Path.GetExtension(icon).ToLowerInvariant() is not (".png" or ".ico")))
        {
            return "icon: a full path to a local .png or .ico file.";
        }

        if (m.Actions.Count > 8)
        {
            return "At most 8 actions.";
        }

        foreach (var a in m.Actions)
        {
            if (!IsId(a.Id) || string.IsNullOrWhiteSpace(a.Title) || a.Title.Length > 30)
            {
                return $"Action '{a.Id}': id like the plugin's, title of 1–30 characters.";
            }

            var isUrl = a.Url is not null;
            var isCommand = a.Command is not null;
            if (isUrl == isCommand)
            {
                return $"Action '{a.Id}': either url or command.";
            }

            if (isUrl && !(Uri.TryCreate(a.Url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
            {
                return $"Action '{a.Id}': only http(s) addresses.";
            }

            if (isCommand && (!Path.IsPathFullyQualified(a.Command!) || Path.GetExtension(a.Command!).ToLowerInvariant() != ".exe"))
            {
                return $"Action '{a.Id}': command must be the full path of an .exe (no shells, no scripts).";
            }

            if (a.Args.Count > 16 || a.Args.Any(x => x.Length > 256 || x.Any(char.IsControl)))
            {
                return $"Action '{a.Id}': up to 16 plain arguments.";
            }
        }

        return null;
    }

    /// <summary>A fingerprint of the manifest: any change (a new action, another command) needs approval again.</summary>
    public static string Hash(PluginManifest m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, Json))))[..32];

    public static bool IsId(string? id) =>
        id is { Length: >= 2 and <= 40 } && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>A widget is shown only if it is one of the known kinds and small enough.</summary>
    public static bool IsValid(PluginWidget w) => w.Kind switch
    {
        "number" => w.Value is { Length: <= 12 },
        "bar" => w.Fraction is >= 0 and <= 1,
        "list" => w.Items is { Count: > 0 and <= 5 } items && items.All(i => i.Length <= 60),
        "spark" => w.Values is { Count: >= 2 and <= 60 } v && v.All(double.IsFinite),
        _ => false,
    } && (w.Label is null || w.Label.Length <= 30);

    /// <summary>A chip colour: #RRGGBB only.</summary>
    public static bool IsColor(string? c) => c is { Length: 7 } && c[0] == '#' && c.Skip(1).All(char.IsAsciiHexDigit);
}

/// <summary>A plugin's description, approved once by the user.</summary>
public sealed record PluginManifest
{
    public int Schema { get; init; } = BarPlugins.Version;

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Publisher { get; init; }

    /// <summary>Full path of a local .png / .ico.</summary>
    public string? Icon { get; init; }

    public IReadOnlyList<PluginAction> Actions { get; init; } = [];

    public bool Equivalent(PluginManifest other) => BarPlugins.Hash(this) == BarPlugins.Hash(other);
}

/// <summary>A button: an .exe with fixed arguments, or a web address — exactly as approved.</summary>
public sealed record PluginAction
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public string? Url { get; init; }
}

/// <summary>One request from a plugin.</summary>
public sealed record PluginRequest
{
    public int V { get; init; } = BarPlugins.Version;

    /// <summary>register, notify, chip, panel, clear.</summary>
    public string Op { get; init; } = string.Empty;

    public string? Plugin { get; init; }

    /// <summary>The secret IBAR gave the plugin on approval.</summary>
    public string? Token { get; init; }

    public PluginManifest? Manifest { get; init; }

    // notify
    public string? Title { get; init; }

    public string? Body { get; init; }

    /// <summary>low, normal, high (high stays until seen).</summary>
    public string? Priority { get; init; }

    /// <summary>Seconds on screen (2–30).</summary>
    public int? Duration { get; init; }

    /// <summary>The id of a declared action to run when the notification is clicked.</summary>
    public string? Link { get; init; }

    // chip
    public string? Color { get; init; }

    public string? Value { get; init; }

    // panel
    public IReadOnlyList<PluginWidget>? Widgets { get; init; }

    /// <summary>Ids of declared actions shown as buttons in the panel.</summary>
    public IReadOnlyList<string>? Buttons { get; init; }

    // activity.start / activity.update / activity.end

    /// <summary>The task's id (the plugin's own, a–z 0–9 - _).</summary>
    public string? Task { get; init; }

    /// <summary>download, render, export, save, copy, task…</summary>
    public string? Kind { get; init; }

    /// <summary>running, paused, waiting (activity.end: done or failed).</summary>
    public string? State { get; init; }

    /// <summary>0…1, or absent when unknown (then the ring turns and no number is shown).</summary>
    public double? Progress { get; init; }

    /// <summary>exact, good or estimate — required with a progress.</summary>
    public string? Accuracy { get; init; }

    public string? Detail { get; init; }

    /// <summary>Bytes (or units) done and in all.</summary>
    public long? Done { get; init; }

    public long? Total { get; init; }

    /// <summary>Per second.</summary>
    public double? Rate { get; init; }

    // sound

    /// <summary>start, done or error.</summary>
    public string? Sound { get; init; }

    // bridge (IBAR's own browser / Adobe hosts only)

    /// <summary>Which app a trusted bridge speaks for (chrome, edge, opera, firefox, aftereffects…).</summary>
    public string? Bridge { get; init; }
}

/// <summary>A dashboard widget: number, bar (fraction 0…1), list (≤ 5 lines) or spark (a small line chart).</summary>
public sealed record PluginWidget
{
    public string Kind { get; init; } = string.Empty;

    public string? Label { get; init; }

    public string? Value { get; init; }

    public double? Fraction { get; init; }

    public IReadOnlyList<string>? Items { get; init; }

    public IReadOnlyList<double>? Values { get; init; }
}

/// <summary>The answer: ok, pending (waiting for the user), denied, or an error; register returns the token.</summary>
public sealed record PluginReply(string Status, string? Token = null, string? Error = null);

/// <summary>A token bucket per plugin: a burst, then a steady rate (the clock is injectable for tests).</summary>
public sealed class RateLimiter(double perSecond, double burst, Func<double>? clock = null)
{
    private readonly Func<double> _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
    private readonly Dictionary<string, (double Tokens, double At)> _buckets = new(StringComparer.Ordinal);

    public bool Allow(string key)
    {
        var now = _clock();
        var (tokens, at) = _buckets.TryGetValue(key, out var b) ? b : (burst, now);
        tokens = Math.Min(burst, tokens + ((now - at) * perSecond));
        if (tokens < 1)
        {
            _buckets[key] = (tokens, now);
            return false;
        }

        _buckets[key] = (tokens - 1, now);
        return true;
    }
}

/// <summary>An approved plugin: its manifest (as approved) and the secret it uses to talk to IBAR.</summary>
public sealed record ApprovedPlugin(PluginManifest Manifest, string Token, string Hash);

/// <summary>Plugin message rules that need no pipe (kept apart so they can be tested).</summary>
public static class PluginRules
{
    public static ApprovedPlugin? Authenticate(IEnumerable<ApprovedPlugin> plugins, PluginRequest request)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(request);
        var plugin = plugins.FirstOrDefault(p => p.Manifest.Id == request.Plugin);
        return plugin is not null && SameToken(plugin.Token, request.Token) ? plugin : null;
    }

    public static bool SameToken(string expected, string? given) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    /// <summary>What is wrong with a message from an approved plugin (null = show it).</summary>
    public static string? Problem(ApprovedPlugin plugin, PluginRequest r)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(r);
        bool Declared(string? id) => id is null || plugin.Manifest.Actions.Any(a => a.Id == id);
        return r.Op switch
        {
            "notify" when string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 80 || (r.Body?.Length ?? 0) > 300 => "notify: title 1–80, body up to 300 characters.",
            "notify" when r.Priority is not (null or "low" or "normal" or "high") => "priority: low, normal or high.",
            "notify" when r.Duration is < 2 or > 30 => "duration: 2–30 seconds.",
            "notify" when !Declared(r.Link) => "link: the id of a declared action.",
            "notify" => null,
            "chip" when !BarPlugins.IsColor(r.Color) || (r.Value?.Length ?? 0) > 3 => "chip: color #RRGGBB and a value of up to 3 characters.",
            "chip" => null,
            "panel" when r.Widgets is not { Count: > 0 and <= 3 } || !r.Widgets.All(BarPlugins.IsValid) => "panel: 1–3 widgets of kind number, bar, list or spark.",
            "panel" when r.Buttons is { } b && (b.Count > 3 || !b.All(Declared)) => "panel: up to 3 buttons, each a declared action.",
            "panel" => null,
            "clear" => null,
            "activity.start" or "activity.update" or "activity.end" => ActivityProblem(r),
            "sound" when r.Sound is not ("start" or "done" or "error") => "sound: start, done or error.",
            "sound" => null,
            _ => "Unknown op (register, notify, chip, panel, clear, activity.start, activity.update, activity.end, sound).",
        };
    }

    /// <summary>What is wrong with an activity message (null = fine). The same rules hold for plugins and bridges.</summary>
    public static string? ActivityProblem(PluginRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var ending = r.Op == "activity.end";
        return r switch
        {
            _ when !BarPlugins.IsId(r.Task) => "task: the task's id, 2–40 characters a–z 0–9 - _.",
            _ when r.Op == "activity.start" && string.IsNullOrWhiteSpace(r.Title) => "activity.start: a title.",
            _ when (r.Title?.Length ?? 0) > 80 || (r.Detail?.Length ?? 0) > 120 => "title up to 80, detail up to 120 characters.",
            _ when r.Kind is { } k && (k.Length > 16 || !BarPlugins.IsId(k)) => "kind: a short word (download, render, export, copy, task).",
            _ when ending && r.State is not (null or "done" or "failed") => "activity.end: state done or failed.",
            _ when !ending && r.State is not (null or "running" or "paused" or "waiting") => "state: running, paused or waiting.",
            _ when r.Progress is { } p && (!double.IsFinite(p) || p < 0 || p > 1) => "progress: 0…1 (leave it out when unknown — never guess).",
            _ when r.Progress is not null && r.Accuracy is not ("exact" or "good" or "estimate") => "accuracy: exact, good or estimate (required with a progress).",
            _ when r.Done is < 0 || r.Total is < 0 || (r.Done is { } d && r.Total is { } t && d > t) => "done / total: 0 ≤ done ≤ total.",
            _ when r.Rate is { } rate && (!double.IsFinite(rate) || rate < 0) => "rate: a non-negative number.",
            _ when r.Color is not null && !BarPlugins.IsColor(r.Color) => "color: #RRGGBB.",
            _ => null,
        };
    }

    /// <summary>The task an activity message describes, for <paramref name="source"/> (a plugin's or a bridge's name).</summary>
    public static Activity.ActivityItem ActivityFrom(PluginRequest r, string source, string? icon)
    {
        ArgumentNullException.ThrowIfNull(r);
        var state = r.State switch
        {
            "paused" => Activity.ActivityState.Paused,
            "waiting" => Activity.ActivityState.Waiting,
            "done" => Activity.ActivityState.Done,
            "failed" => Activity.ActivityState.Failed,
            _ => r.Op == "activity.end" ? Activity.ActivityState.Done : Activity.ActivityState.Running,
        };
        return new Activity.ActivityItem
        {
            Id = r.Task!,
            Source = source,
            Kind = r.Kind ?? "task",
            Title = r.Title ?? string.Empty,
            Detail = r.Detail,
            Progress = r.Progress,
            Accuracy = r.Accuracy switch
            {
                "exact" => Activity.ProgressAccuracy.Exact,
                "good" => Activity.ProgressAccuracy.Good,
                "estimate" => Activity.ProgressAccuracy.Estimate,
                _ => Activity.ProgressAccuracy.Unknown,
            },
            State = state,
            Color = r.Color,
            Icon = icon,
            Done = r.Done,
            Total = r.Total,
            Rate = r.Rate,
        };
    }

    /// <summary>The apps IBAR's own bridges may speak for.</summary>
    public static bool IsBridge(string? name) => name is "chrome" or "edge" or "opera" or "brave" or "vivaldi" or "chromium" or "firefox"
        or "aftereffects" or "premiere" or "mediaencoder" or "photoshop" or "illustrator";
}
