using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Common;

/// <summary>Shared serializer options (cached per CA1869).</summary>
public static class JsonDefaults
{
    /// <summary>Hard cap on JSON nesting accepted from any untrusted source.</summary>
    public const int MaxDepth = 64;

    /// <summary>For persisted state and IPC payloads.</summary>
    public static readonly JsonSerializerOptions Storage = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaxDepth,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>For generated core configs (readable, stable field order).</summary>
    public static readonly JsonSerializerOptions CoreConfig = new()
    {
        MaxDepth = MaxDepth,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>For parsing untrusted configs: tolerant of comments/trailing commas, strict on depth.</summary>
    public static readonly JsonDocumentOptions UntrustedDocument = new()
    {
        MaxDepth = MaxDepth,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static readonly System.Text.Json.Nodes.JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
}
