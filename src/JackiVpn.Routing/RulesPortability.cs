using System.Text.Json;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Routing;

namespace JackiVpn.Routing;

public sealed record RulesDocument
{
    public int FormatVersion { get; init; } = 1;
    public string Product { get; init; } = Domain.ProductInfo.ProductName;
    public RuleActionType Final { get; init; } = RuleActionType.Proxy;
    public IReadOnlyList<RoutingRule> Rules { get; init; } = [];
    public IReadOnlyList<RuleSetDefinition> RuleSets { get; init; } = [];
}

/// <summary>Export/import of the full rule configuration (every rule — including preset ones — is exportable).</summary>
public static class RulesPortability
{
    private static readonly JsonSerializerOptions Options = new(JsonDefaults.Storage) { WriteIndented = true };

    public static string Export(RulesDocument doc) => JsonSerializer.Serialize(doc, Options);

    public static (RulesDocument? Document, ValidationResult Validation) Import(string json)
    {
        var v = new ValidationResult();
        RulesDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<RulesDocument>(json, JsonDefaults.Storage);
        }
        catch (JsonException ex)
        {
            v.Error("Document", "Invalid rules file: " + ex.Message);
            return (null, v);
        }

        if (doc is null || doc.FormatVersion != 1)
        {
            v.Error("Document", "Unsupported rules file version.");
            return (null, v);
        }

        var tags = doc.RuleSets.Select(r => r.Tag).ToArray();
        v.Merge(RuleValidator.ValidateAll(doc.Rules, tags));
        foreach (var rs in doc.RuleSets)
        {
            if (!Uri.TryCreate(rs.Url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            {
                v.Error($"RuleSets[{rs.Tag}]", "Rule-set URL must be http(s).");
            }
        }

        return (v.IsValid ? doc : null, v);
    }
}
