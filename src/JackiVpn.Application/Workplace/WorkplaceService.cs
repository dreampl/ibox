using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Workplace;

/// <summary>Reads facts about the current network (platform specific).</summary>
public interface INetworkFactsProvider
{
    NetworkFacts Read();
}

/// <summary>A network that looks like a company network but is not set up yet.</summary>
public sealed record WorkplaceSuggestion(NetworkFacts Facts, string SuggestedName);

/// <summary>
/// Recognises workplace networks (DNS suffix, Active Directory domain, gateway MAC, Wi-Fi name) and activates the
/// matching profile: internal names and subnets stay outside the tunnel and Work mode applies. Networks that look
/// corporate but are unknown are only suggested — nothing changes until the user (or a signed company policy) says so.
/// </summary>
public sealed class WorkplaceService
{
    private static readonly HashSet<string> HomeSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "lan", "local", "home", "localdomain", "router", "domain", "home.arpa", "gateway", "modem", "dlink", "tplinkwifi.net", "fritz.box", "station", "Huawei",
    };

    private readonly INetworkFactsProvider _facts;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _logger;

    public WorkplaceService(INetworkFactsProvider facts, Func<AppSettings> settings, ILogger logger)
    {
        _facts = facts;
        _settings = settings;
        _logger = logger;
    }

    public NetworkFacts Facts { get; private set; } = NetworkFacts.None;

    /// <summary>The workplace the device is on now (null = none).</summary>
    public WorkplaceProfile? Active { get; private set; }

    public WorkplaceSuggestion? Suggestion { get; private set; }

    /// <summary>Raised when the active workplace or the suggestion changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads the network (on start and after every network change).</summary>
    public void Refresh()
    {
        NetworkFacts facts;
        try
        {
            facts = _facts.Read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Network facts unavailable: {Error}", ex.Message);
            facts = NetworkFacts.None;
        }

        var settings = _settings().Workplace;
        var active = Match(settings.Profiles, facts);
        var suggestion = active is null && settings.Detect && LooksCorporate(facts) && !settings.DismissedNetworks.Contains(facts.Key, StringComparer.Ordinal)
            ? new WorkplaceSuggestion(facts, SuggestName(facts))
            : null;
        var changed = active?.Id != Active?.Id || active != Active || suggestion?.Facts.Key != Suggestion?.Facts.Key;
        Facts = facts;
        Active = active;
        Suggestion = suggestion;
        if (changed)
        {
            _logger.LogInformation("Workplace: {Name}.", active?.Name ?? (suggestion is null ? "none" : "suggested " + suggestion.SuggestedName));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public static WorkplaceProfile? Match(IReadOnlyList<WorkplaceProfile> profiles, NetworkFacts f)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(f);
        if (string.IsNullOrEmpty(f.Key))
        {
            return null;
        }

        return profiles.FirstOrDefault(p =>
            (p.PolicyExpiresUtc is null || p.PolicyExpiresUtc > DateTimeOffset.UtcNow)
            && (p.NetworkKeys.Contains(f.Key, StringComparer.Ordinal)
                || (f.Ssid is { Length: > 0 } ssid && p.Ssids.Contains(ssid, StringComparer.Ordinal))
                || (f.GatewayMac is { Length: > 0 } mac && p.GatewayMacs.Contains(mac, StringComparer.OrdinalIgnoreCase))
                || (f.DnsSuffix is { Length: > 0 } suffix && p.DnsSuffixes.Any(s => NetText.IsHostInDomains(suffix, [s])))
                || (f.AdDomain is { Length: > 0 } ad && p.DnsSuffixes.Any(s => NetText.IsHostInDomains(ad, [s])))));
    }

    /// <summary>A company network: domain-joined, or a DNS suffix that is not a home router default.</summary>
    public static bool LooksCorporate(NetworkFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (!string.IsNullOrEmpty(f.AdDomain))
        {
            return true;
        }

        var suffix = f.DnsSuffix?.Trim().Trim('.');
        return !string.IsNullOrEmpty(suffix) && !HomeSuffixes.Contains(suffix) && !HomeSuffixes.Contains(suffix.Split('.')[^1]) && suffix.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>"corp.acme.com" → "Acme"; the company's real name comes from its policy or the user.</summary>
    public static string SuggestName(NetworkFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var source = f.AdDomain ?? f.DnsSuffix ?? f.Ssid ?? "Workplace";
        var labels = source.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Length > 1 && l is not ("corp" or "ad" or "local" or "lan" or "internal" or "intra" or "office" or "com" or "net" or "org" or "ir" or "co"))
            .ToList();
        var name = labels.Count > 0 ? labels[^1] : source;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>A workplace profile for the current network (the user confirmed it is their workplace).</summary>
    public static WorkplaceProfile FromFacts(NetworkFacts f, string name)
    {
        ArgumentNullException.ThrowIfNull(f);
        var domains = new[] { f.DnsSuffix, f.AdDomain }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!.Trim('.').ToLowerInvariant()).Distinct().ToList();
        return new WorkplaceProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? SuggestName(f) : name.Trim(),
            NetworkKeys = string.IsNullOrEmpty(f.Key) ? [] : [f.Key],
            DnsSuffixes = domains,
            Ssids = f.Ssid is { Length: > 0 } s ? [s] : [],
            GatewayMacs = f.GatewayMac is { Length: > 0 } m ? [m] : [],
            InternalDomains = domains,
            InternalCidrs = f.LocalCidrs.Where(NetText.IsCidr).ToList(),
            InternalDns = f.DnsServers.Where(d => System.Net.IPAddress.TryParse(d, out var ip) && NetText.IsPrivateOrLocal(ip)).ToList(),
        };
    }

    /// <summary>Stable key of a network from its gateway, DNS suffix and Wi-Fi name (no personal data).</summary>
    public static string KeyOf(string? gatewayMac, string? gatewayIp, string? dnsSuffix, string? ssid)
    {
        var raw = $"{gatewayMac?.ToLowerInvariant()}|{(gatewayMac is null ? gatewayIp : null)}|{dnsSuffix?.ToLowerInvariant()}|{ssid}";
        return raw == "|||" ? string.Empty : "net:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }
}

/// <summary>A verified company policy.</summary>
public sealed record CompanyPolicy(WorkplaceProfile Profile, string KeySha256);

public sealed class CompanyPolicyException(string message) : Exception(message);

/// <summary>
/// Company policies are JSON documents signed by the company's IT (ECDSA P-256, SHA-256). Envelope:
/// <c>{"format":"jacki-company-policy/1","policy":"&lt;base64 JSON&gt;","key":"&lt;base64 SubjectPublicKeyInfo&gt;","sig":"&lt;base64 P1363&gt;"}</c>.
/// The key is pinned when the user first accepts a policy; later updates must be signed by the same key.
/// Created with <c>scripts/company-policy.cs</c>.
/// </summary>
public static class CompanyPolicyReader
{
    public const string Format = "jacki-company-policy/1";
    public const int MaxBytes = 256 * 1024;

    public static CompanyPolicy Read(string envelopeJson, string? pinnedKeySha256, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        if (envelopeJson.Length > MaxBytes)
        {
            throw new CompanyPolicyException("The policy file is too large.");
        }

        JsonNode? env;
        try
        {
            env = JsonNode.Parse(envelopeJson, documentOptions: JsonDefaults.UntrustedDocument);
        }
        catch (JsonException)
        {
            throw new CompanyPolicyException("The policy file is not valid JSON.");
        }

        if (env?["format"]?.GetValue<string>() != Format)
        {
            throw new CompanyPolicyException("Not a Jacki company policy.");
        }

        byte[] policyBytes, key, sig;
        try
        {
            policyBytes = Convert.FromBase64String(env["policy"]!.GetValue<string>());
            key = Convert.FromBase64String(env["key"]!.GetValue<string>());
            sig = Convert.FromBase64String(env["sig"]!.GetValue<string>());
        }
        catch (Exception ex) when (ex is FormatException or NullReferenceException or InvalidOperationException)
        {
            throw new CompanyPolicyException("The policy file is incomplete.");
        }

        var keySha = Convert.ToHexStringLower(SHA256.HashData(key));
        if (pinnedKeySha256 is { Length: > 0 } pinned && !string.Equals(pinned, keySha, StringComparison.OrdinalIgnoreCase))
        {
            throw new CompanyPolicyException("This policy is signed by a different key than the one you accepted before.");
        }

        using (var ecdsa = ECDsa.Create())
        {
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            }
            catch (CryptographicException)
            {
                throw new CompanyPolicyException("The policy's public key is invalid.");
            }

            if (ecdsa.KeySize != 256 || !ecdsa.VerifyData(policyBytes, sig, HashAlgorithmName.SHA256))
            {
                throw new CompanyPolicyException("The policy signature is not valid.");
            }
        }

        JsonNode? p;
        try
        {
            p = JsonNode.Parse(policyBytes, documentOptions: JsonDefaults.UntrustedDocument);
        }
        catch (JsonException)
        {
            throw new CompanyPolicyException("The signed policy is not valid JSON.");
        }

        var name = p?["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80 || NetText.HasControlChars(name))
        {
            throw new CompanyPolicyException("The policy has no valid company name.");
        }

        var issued = Date(p!["issued"]);
        var expires = Date(p["expires"]);
        if (expires is { } exp && exp < now)
        {
            throw new CompanyPolicyException("The policy has expired.");
        }

        var match = p["match"];
        var internalNode = p["internal"];
        var rules = p["rules"];
        var refresh = p["refreshUrl"]?.GetValue<string>();
        var profile = new WorkplaceProfile
        {
            Id = "policy-" + keySha[..12],
            Name = name,
            DnsSuffixes = Strings(match?["dnsSuffixes"]).Where(d => NetText.IsValidHostName(d)).ToList(),
            Ssids = Strings(match?["ssids"]).ToList(),
            GatewayMacs = Strings(match?["gatewayMacs"]).ToList(),
            InternalDomains = Strings(internalNode?["domains"]).Where(d => NetText.IsValidHostName(d)).ToList(),
            InternalCidrs = Strings(internalNode?["cidrs"]).Where(NetText.IsCidr).ToList(),
            InternalDns = Strings(internalNode?["dns"]).Where(NetText.IsIpLiteral).ToList(),
            Rules = new CompanyRules
            {
                BlockApps = Strings(rules?["blockApps"]).ToList(),
                BlockDomains = Strings(rules?["blockDomains"]).Where(d => NetText.IsValidHostName(d)).ToList(),
                BlockUpdates = Strings(rules?["blockUpdates"]).Select(g => Enum.TryParse<UpdateGroup>(g, true, out var v) ? v : (UpdateGroup?)null).OfType<UpdateGroup>().ToList(),
                DailyCapMb = Int(rules?["dailyCapMb"]),
                BackgroundKbps = Int(rules?["backgroundKbps"]),
            },
            PolicyKeySha256 = keySha,
            PolicyUrl = refresh is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? u : null,
            PolicyIssuedUtc = issued,
            PolicyExpiresUtc = expires,
        };
        return new CompanyPolicy(profile, keySha);
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>().Where(s => s.Length is > 0 and <= 255 && !NetText.HasControlChars(s)).Take(1000)
            : [];

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? Math.Max(0, i) : 0;

    private static DateTimeOffset? Date(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Signs a policy (used by the IT tool and tests).</summary>
    public static string Sign(string policyJson, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(policyJson);
        ArgumentNullException.ThrowIfNull(key);
        var bytes = Encoding.UTF8.GetBytes(policyJson);
        return new JsonObject
        {
            ["format"] = Format,
            ["policy"] = Convert.ToBase64String(bytes),
            ["key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            ["sig"] = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256)),
        }.ToJsonString();
    }
}
