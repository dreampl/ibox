using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JackiVpn.Subscriptions.Parsing.Json;

/// <summary>Lenient typed accessors over <see cref="JsonObject"/> (providers mix strings and numbers freely).</summary>
internal static class JsonNodeReader
{
    public static string? Str(this JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is null)
        {
            return null;
        }

        return n.GetValueKind() switch
        {
            JsonValueKind.String => n.GetValue<string>(),
            JsonValueKind.Number => n.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static int? Int(this JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is null)
        {
            return null;
        }

        return n.GetValueKind() switch
        {
            JsonValueKind.Number => TryInt(n),
            JsonValueKind.String when int.TryParse(n.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    /// <summary>Works for element-backed and primitive-backed (int/long/double) values alike.</summary>
    public static int? TryInt(JsonNode? n)
    {
        if (n is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return d is >= int.MinValue and <= int.MaxValue && d == Math.Floor(d) ? (int)d : null;
        }

        return null;
    }

    public static bool? Bool(this JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is null)
        {
            return null;
        }

        return n.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => n.GetValue<string>() is "1" or "true" or "True",
            JsonValueKind.Number => n.ToJsonString() != "0",
            _ => null,
        };
    }

    public static JsonObject? Obj(this JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) ? n as JsonObject : null;

    public static JsonArray? Arr(this JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) ? n as JsonArray : null;

    /// <summary>String or array of strings → list.</summary>
    public static IReadOnlyList<string> StrList(this JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is null)
        {
            return [];
        }

        if (n is JsonArray arr)
        {
            return arr.Where(x => x is not null && x.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                .Select(x => x!.GetValueKind() == JsonValueKind.String ? x.GetValue<string>() : x.ToJsonString())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        return n.GetValueKind() == JsonValueKind.String
            ? n.GetValue<string>().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    public static IReadOnlyList<int> IntList(this JsonObject? o, string key)
    {
        if (o?.Arr(key) is not { } arr)
        {
            // "reserved" is sometimes given as a comma list string.
            var s = o.Str(key);
            return s is null
                ? []
                : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1)
                    .ToArray();
        }

        return arr.Select(x => TryInt(x) ?? -1).ToArray();
    }

    /// <summary>Returns a deep copy of every property not in <paramref name="known"/>, or null if there are none.</summary>
    public static JsonObject? Unknown(this JsonObject? o, IReadOnlySet<string> known)
    {
        if (o is null)
        {
            return null;
        }

        JsonObject? ext = null;
        foreach (var (k, v) in o)
        {
            if (!known.Contains(k))
            {
                ext ??= new JsonObject();
                ext[k] = v?.DeepClone();
            }
        }

        return ext;
    }

    public static IReadOnlyDictionary<string, string> StringMap(this JsonObject? o, string key, params string[] exclude)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o.Obj(key) is not { } h)
        {
            return map;
        }

        foreach (var (k, v) in h)
        {
            if (exclude.Contains(k, StringComparer.OrdinalIgnoreCase) || v is null)
            {
                continue;
            }

            map[k] = v is JsonArray a
                ? string.Join(",", a.Select(x => x?.ToString()))
                : v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        }

        return map;
    }

    public static string? HeaderValue(this JsonObject? o, string key, string header)
    {
        if (o.Obj(key) is not { } h)
        {
            return null;
        }

        foreach (var (k, v) in h)
        {
            if (string.Equals(k, header, StringComparison.OrdinalIgnoreCase) && v is not null)
            {
                return v is JsonArray a ? a.FirstOrDefault()?.ToString() : v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            }
        }

        return null;
    }
}
