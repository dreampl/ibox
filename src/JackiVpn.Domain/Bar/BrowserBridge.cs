using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JackiVpn.Domain.Bar;

/// <summary>
/// The browser side of IBAR's native host: Native Messaging frames (a 32-bit little-endian length, then UTF-8 JSON)
/// and the translation of the extension's download messages into IBAR activity requests. Only the file's name,
/// sizes, state, speed, the browser and the source's domain go through — anything else the extension might send
/// (an address, a path) is dropped here.
/// </summary>
public static class BrowserBridge
{
    /// <summary>Chrome's limit for a message to the host is 64 MB; IBAR's host takes 64 KB (a download is ~300 bytes).</summary>
    public const int MaxMessage = 64 * 1024;

    /// <summary>Reads one message; null at the end of the stream (the browser closed) or on a frame that is too big.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new byte[4];
        if (!await FillAsync(input, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessage)
        {
            return null;
        }

        var body = new byte[length];
        if (!await FillAsync(input, body, ct).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task WriteAsync(Stream output, JsonObject message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> FillAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await input.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>Which browser started the host, from the program names above it (cmd.exe in between is skipped).</summary>
    public static string? BrowserOf(IEnumerable<string> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        foreach (var raw in ancestors)
        {
            var name = Path.GetFileNameWithoutExtension(raw).ToLowerInvariant();
            switch (name)
            {
                case "chrome": return "chrome";
                case "msedge": return "edge";
                case "opera" or "opera_gx" or "launcher": return name == "launcher" ? null : "opera";
                case "brave": return "brave";
                case "vivaldi": return "vivaldi";
                case "firefox": return "firefox";
            }
        }

        return null;
    }

    /// <summary>
    /// A download message from the extension as an IBAR request for <paramref name="browser"/> (null = not a download).
    /// <paramref name="rate"/> is the speed the host measured from the byte counts.
    /// </summary>
    public static PluginRequest? ToRequest(JsonObject message, string browser, double? rate)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Text(message, "type") != "download" || message["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id) || id < 0)
        {
            return null;
        }

        var file = Path.GetFileName(Text(message, "file") ?? string.Empty);
        var received = Number(message, "received");
        var total = Number(message, "total");
        var state = Text(message, "state");
        var paused = message["paused"] is JsonValue p && p.TryGetValue<bool>(out var isPaused) && isPaused;
        var error = Text(message, "error");
        var domain = Domain(Text(message, "domain"));
        var known = total is > 0 && received is >= 0 && received <= total;
        var detail = string.Join("  ·  ", new[] { domain, error is { Length: > 0 } && error != "USER_CANCELED" ? "failed: " + error.ToLowerInvariant().Replace('_', ' ') : null }.Where(x => !string.IsNullOrEmpty(x)));
        var ending = state is "complete" or "interrupted";
        return new PluginRequest
        {
            Op = ending ? "activity.end" : "activity.update",
            Bridge = browser,
            Task = "dl-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind = "download",
            Title = file.Length > 0 ? Trim(file, 80) : "Download",
            Detail = detail.Length > 0 ? Trim(detail, 120) : null,
            State = state switch
            {
                "complete" => "done",
                "interrupted" => "failed",
                _ => paused ? "paused" : "running",
            },
            Progress = known ? (double)received!.Value / total!.Value : null,
            Accuracy = known ? "exact" : null,
            Done = received is >= 0 && (total is not > 0 || received <= total) ? received : null,
            Total = total is > 0 ? total : null,
            Rate = !ending && !paused && rate is >= 0 ? rate : null,
        };
    }

    /// <summary>The download was cancelled by the user (it just leaves, no error sound).</summary>
    public static bool Cancelled(JsonObject message) => Text(message, "error") == "USER_CANCELED";

    /// <summary>A host name only (letters, digits, dots, dashes); anything else is dropped.</summary>
    public static string? Domain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253)
        {
            return null;
        }

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? value.ToLowerInvariant() : null;
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Number(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (long)d : null;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
