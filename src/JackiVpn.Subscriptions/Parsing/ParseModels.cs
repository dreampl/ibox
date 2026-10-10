using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Security;

namespace JackiVpn.Subscriptions.Parsing;

/// <summary>Resource limits applied to every parse so hostile input cannot exhaust memory/CPU.</summary>
public sealed record ParseLimits
{
    public int MaxInputChars { get; init; } = 16 * 1024 * 1024;
    public int MaxEntries { get; init; } = 20_000;
    public int MaxLineLength { get; init; } = 32 * 1024;
    public int MaxJsonDepth { get; init; } = JsonDefaults.MaxDepth;
    public int MaxNestedDecodes { get; init; } = 2;
    public int MaxYamlNodes { get; init; } = 500_000;
    public int MaxYamlDepth { get; init; } = 64;

    public static ParseLimits Default { get; } = new();
}

public sealed record ParseContext
{
    public ProfileOriginKind OriginKind { get; init; } = ProfileOriginKind.Manual;
    public string? SubscriptionId { get; init; }
    public string? ProviderId { get; init; }
    public ParseLimits Limits { get; init; } = ParseLimits.Default;
    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Keep the raw source text of each node (encrypted at rest by the repository).</summary>
    public bool KeepRawSource { get; init; } = true;

    internal int DecodeDepth { get; init; }
}

public sealed record ParseIssue(IssueSeverity Severity, int? Line, string Message, string? RedactedSnippet)
{
    public override string ToString() =>
        Line is { } l ? $"{Severity} (line {l}): {Message}" : $"{Severity}: {Message}";
}

public sealed class ParseResult
{
    public ParseResult(ConfigFormat format)
    {
        Format = format;
    }

    public ConfigFormat Format { get; internal set; }

    public List<ProxyProfile> Profiles { get; } = [];

    public List<ParseIssue> Issues { get; } = [];

    public int TotalEntries { get; internal set; }

    public int Failed => Issues.Count(i => i.Severity == IssueSeverity.Error);

    public bool IsHtml => Format == ConfigFormat.Html;

    public bool HasProfiles => Profiles.Count > 0;

    internal void Error(int? line, string message, string? snippet = null) =>
        Issues.Add(new ParseIssue(IssueSeverity.Error, line, message, snippet is null ? null : SafeSnippet(snippet)));

    internal void Warn(int? line, string message, string? snippet = null) =>
        Issues.Add(new ParseIssue(IssueSeverity.Warning, line, message, snippet is null ? null : SafeSnippet(snippet)));

    internal void Info(int? line, string message) =>
        Issues.Add(new ParseIssue(IssueSeverity.Info, line, message, null));

    internal void Absorb(ParseResult other)
    {
        Profiles.AddRange(other.Profiles);
        Issues.AddRange(other.Issues);
        TotalEntries += other.TotalEntries;
    }

    private static string SafeSnippet(string s)
    {
        var redacted = SecretRedactor.Redact(s);
        return redacted.Length > 120 ? redacted[..120] + "…" : redacted;
    }
}

/// <summary>Thrown for a single malformed entry; the pipeline records it and continues with the next entry.</summary>
public sealed class ProfileParseException(string message) : FormatException(message);
