using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace JackiVpn.Subscriptions.Parsing.Yaml;

/// <summary>
/// Loads YAML and converts it to a <see cref="JsonNode"/> tree under hard limits (node count, depth), which
/// neutralizes alias-expansion ("billion laughs") and deeply nested documents.
/// </summary>
internal static class SafeYaml
{
    public static JsonNode? Load(string text, int maxNodes, int maxDepth)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ProfileParseException($"Invalid YAML: {ex.Message}");
        }

        if (stream.Documents.Count == 0)
        {
            return null;
        }

        var budget = maxNodes;
        return Convert(stream.Documents[0].RootNode, 0, maxDepth, ref budget);
    }

    private static JsonNode? Convert(YamlNode node, int depth, int maxDepth, ref int budget)
    {
        if (--budget < 0)
        {
            throw new ProfileParseException("YAML document is too large (node limit exceeded).");
        }

        if (depth > maxDepth)
        {
            throw new ProfileParseException("YAML document is nested too deeply.");
        }

        switch (node)
        {
            case YamlMappingNode map:
            {
                var obj = new JsonObject();
                foreach (var (k, v) in map.Children)
                {
                    var key = (k as YamlScalarNode)?.Value;
                    if (key is null)
                    {
                        continue;
                    }

                    obj[key] = Convert(v, depth + 1, maxDepth, ref budget);
                }

                return obj;
            }

            case YamlSequenceNode seq:
            {
                var arr = new JsonArray();
                foreach (var child in seq.Children)
                {
                    arr.Add(Convert(child, depth + 1, maxDepth, ref budget));
                }

                return arr;
            }

            case YamlScalarNode scalar:
                return Scalar(scalar);

            default:
                return null;
        }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var v = scalar.Value;
        if (v is null)
        {
            return null;
        }

        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return JsonValue.Create(v);
        }

        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        return JsonValue.Create(v);
    }
}
