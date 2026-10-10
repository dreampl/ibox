using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing.Json;
using JackiVpn.Subscriptions.Parsing.Uris;
using JackiVpn.Subscriptions.Parsing.Yaml;

namespace JackiVpn.Subscriptions.Parsing;

public interface IConfigParser
{
    ParseResult Parse(string text, ParseContext context);
}

/// <summary>
/// Format detection + dispatch. Never throws for bad input: every problem becomes a <see cref="ParseIssue"/>.
/// Detection order: HTML → JSON (sing-box / Xray, full / single / arrays) → Clash YAML → URI list →
/// Base64 (recursing at most <see cref="ParseLimits.MaxNestedDecodes"/> times).
/// </summary>
public sealed class ConfigParserPipeline : IConfigParser
{
    public ParseResult Parse(string text, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        text ??= string.Empty;
        if (text.Length > context.Limits.MaxInputChars)
        {
            var tooBig = new ParseResult(ConfigFormat.Unknown);
            tooBig.Error(null, $"Input is too large ({text.Length:N0} chars > {context.Limits.MaxInputChars:N0}).");
            return tooBig;
        }

        var trimmed = text.Trim().TrimStart((char)0xFEFF);
        if (trimmed.Length == 0)
        {
            var empty = new ParseResult(ConfigFormat.Unknown);
            empty.Error(null, "Input is empty.");
            return empty;
        }

        try
        {
            return ParseCore(trimmed, context);
        }
        catch (Exception ex) when (ex is ProfileParseException or JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            var failed = new ParseResult(DetectFormat(trimmed));
            failed.Error(null, "Could not parse input: " + ex.Message);
            return failed;
        }
    }

    public static ConfigFormat DetectFormat(string trimmed)
    {
        if (LooksLikeHtml(trimmed))
        {
            return ConfigFormat.Html;
        }

        if (trimmed[0] is '{' or '[')
        {
            return ConfigFormat.SingBoxFull; // refined during JSON parsing
        }

        if (ClashYamlParser.LooksLikeClash(trimmed))
        {
            return ConfigFormat.ClashYaml;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return ConfigFormat.UriList;
        }

        return Base64Text.LooksLikeBase64(trimmed) ? ConfigFormat.Base64UriList : ConfigFormat.Unknown;
    }

    public static bool LooksLikeHtml(string text)
    {
        var head = text.Length > 512 ? text[..512] : text;
        head = head.TrimStart();
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith('<') && head.Contains("<html", StringComparison.OrdinalIgnoreCase));
    }

    private ParseResult ParseCore(string text, ParseContext ctx)
    {
        if (LooksLikeHtml(text))
        {
            var html = new ParseResult(ConfigFormat.Html);
            html.Error(null, "The response is an HTML page, not a subscription/config.");
            return html;
        }

        if (text[0] is '{' or '[')
        {
            return ParseJson(text, ctx);
        }

        if (ClashYamlParser.LooksLikeClash(text))
        {
            var yaml = new ParseResult(ConfigFormat.ClashYaml);
            ClashYamlParser.Parse(text, ctx, yaml);
            return yaml;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return ParseUriList(text, ctx, ctx.DecodeDepth > 0 ? ConfigFormat.Base64UriList : ConfigFormat.UriList);
        }

        if (ctx.DecodeDepth < ctx.Limits.MaxNestedDecodes && Base64Text.LooksLikeBase64(text)
            && Base64Text.TryDecodeToString(text, ctx.Limits.MaxInputChars, out var decoded))
        {
            var inner = ParseCore(decoded.Trim().TrimStart((char)0xFEFF), ctx with { DecodeDepth = ctx.DecodeDepth + 1 });
            if (inner.Format == ConfigFormat.UriList)
            {
                inner.Format = ConfigFormat.Base64UriList;
            }

            return inner;
        }

        var unknown = new ParseResult(ConfigFormat.Unknown);
        unknown.Error(null, "Unrecognized format (not a share link list, Base64, JSON or Clash YAML).", text);
        return unknown;
    }

    private static ParseResult ParseUriList(string text, ParseContext ctx, ConfigFormat format)
    {
        var result = new ParseResult(format);
        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || (line[0] == '#' && !line.Contains("://", StringComparison.Ordinal)))
            {
                continue;
            }

            if (result.TotalEntries >= ctx.Limits.MaxEntries)
            {
                result.Warn(lineNo, $"Entry limit ({ctx.Limits.MaxEntries}) reached; remaining lines ignored.");
                break;
            }

            if (!ShareLinkParser.LooksLikeShareLink(line))
            {
                var scheme = line.Contains("://", StringComparison.Ordinal) ? line[..line.IndexOf("://", StringComparison.Ordinal)] : "?";
                result.TotalEntries++;
                result.Warn(lineNo, $"Unsupported link scheme '{ProfileFactory.Truncate(scheme)}'; skipped.");
                continue;
            }

            result.TotalEntries++;
            try
            {
                result.Profiles.Add(ShareLinkParser.Parse(line, ctx, format));
            }
            catch (ProfileParseException ex)
            {
                result.Error(lineNo, ex.Message, line);
            }
        }

        return result;
    }

    private static ParseResult ParseJson(string text, ParseContext ctx)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, JsonDefaults.NodeOptions, new JsonDocumentOptions
            {
                MaxDepth = ctx.Limits.MaxJsonDepth,
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            var bad = new ParseResult(ConfigFormat.Unknown);
            bad.Error(null, "Malformed JSON: " + FirstLine(ex.Message));
            return bad;
        }

        if (root is null)
        {
            var nil = new ParseResult(ConfigFormat.Unknown);
            nil.Error(null, "JSON document is empty.");
            return nil;
        }

        var dialect = DetectJsonDialect(root);
        var result = new ParseResult(dialect);
        switch (dialect)
        {
            case ConfigFormat.XrayFull or ConfigFormat.XrayOutbound:
                XrayJsonParser.ParseDocument(root, ctx, result, dialect);
                break;
            case ConfigFormat.SingBoxFull or ConfigFormat.SingBoxOutbound:
                SingBoxJsonParser.ParseDocument(root, ctx, result, dialect);
                break;
            default:
                result.Error(null, "JSON is neither a sing-box nor an Xray configuration.");
                break;
        }

        return result;
    }

    public static ConfigFormat DetectJsonDialect(JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        static ConfigFormat FromOutbound(JsonObject o) =>
            o.ContainsKey("protocol") ? ConfigFormat.XrayOutbound
            : o.ContainsKey("type") ? ConfigFormat.SingBoxOutbound
            : ConfigFormat.Unknown;

        switch (root)
        {
            case JsonObject o when o.Arr("outbounds") is { } outs:
            {
                var first = outs.OfType<JsonObject>().FirstOrDefault();
                if (first is null)
                {
                    return o.Arr("endpoints") is not null ? ConfigFormat.SingBoxFull : ConfigFormat.Unknown;
                }

                return FromOutbound(first) == ConfigFormat.XrayOutbound ? ConfigFormat.XrayFull : ConfigFormat.SingBoxFull;
            }

            case JsonObject o when o.Arr("endpoints") is not null:
                return ConfigFormat.SingBoxFull;
            case JsonObject o:
                return FromOutbound(o);
            case JsonArray arr:
            {
                var first = arr.OfType<JsonObject>().FirstOrDefault();
                if (first is null)
                {
                    return ConfigFormat.Unknown;
                }

                if (first.Arr("outbounds") is not null)
                {
                    return DetectJsonDialect(first);
                }

                var single = FromOutbound(first);
                return single;
            }

            default:
                return ConfigFormat.Unknown;
        }
    }

    private static string FirstLine(string s)
    {
        var nl = s.IndexOf('\n', StringComparison.Ordinal);
        return nl >= 0 ? s[..nl] : s;
    }
}
