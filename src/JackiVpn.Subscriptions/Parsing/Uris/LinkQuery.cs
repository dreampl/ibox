using System.Globalization;
using System.Text.Json.Nodes;

namespace JackiVpn.Subscriptions.Parsing.Uris;

/// <summary>
/// Case-insensitive query parameter bag for share links. Tracks which keys were consumed so that
/// unknown parameters can be preserved in the profile's extension data instead of being lost.
/// </summary>
internal sealed class LinkQuery
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    public LinkQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(eq >= 0 ? part[..eq] : part).Trim();
            var value = eq >= 0 ? Decode(part[(eq + 1)..]) : string.Empty;
            if (key.Length > 0)
            {
                _values[key] = value;
            }
        }
    }

    public string? Get(params string[] keys)
    {
        foreach (var k in keys)
        {
            if (_values.TryGetValue(k, out var v))
            {
                _consumed.Add(k);
                if (v.Length > 0)
                {
                    return v;
                }
            }
        }

        return null;
    }

    public bool GetBool(params string[] keys)
    {
        var v = Get(keys);
        return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    public int? GetInt(params string[] keys)
    {
        var v = Get(keys);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    public IReadOnlyList<string> GetList(params string[] keys)
    {
        var v = Get(keys);
        return string.IsNullOrWhiteSpace(v)
            ? []
            : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Parameters that no parser consumed, preserved as <c>{"uri_params": {...}}</c>.</summary>
    public JsonObject? UnconsumedAsExtensions()
    {
        var obj = new JsonObject();
        foreach (var (k, v) in _values)
        {
            if (!_consumed.Contains(k))
            {
                obj[k] = v;
            }
        }

        return obj.Count == 0 ? null : new JsonObject { ["uri_params"] = obj };
    }

    public static string Decode(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }
}
