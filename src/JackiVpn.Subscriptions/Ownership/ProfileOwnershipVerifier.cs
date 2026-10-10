using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Parsing;

namespace JackiVpn.Subscriptions.Ownership;

/// <summary>
/// Decides whether a subscription or single profile belongs to the owner's own infrastructure, which gates
/// Jacki Smart Connect. A node's remark/name is never trusted. Methods, strongest first:
/// <list type="number">
/// <item><b>Signed manifest</b>: header <c>X-Jacki-Manifest: base64url(payload).base64url(ES256 signature)</c>,
/// verified against the configured ECDSA P-256 public key; payload binds the subscription host and expiry.</item>
/// <item><b>Official domain</b>: fetched over HTTPS from (a sub-domain of) a built-in <see cref="Domain.ProductInfo.OfficialProviderDomains"/>.</item>
/// <item><b>Domain allow-list</b>: the subscription host equals or is a sub-domain of an allowed domain.</item>
/// <item><b>Provider header</b>: e.g. <c>X-Jacki-Profile: v1</c>, only over HTTPS (configurable) and optionally
/// only together with the allow-list. Any provider can send a header, so this is the weakest method.</item>
/// </list>
/// Single configs fall back to a server/SNI/Host allow-list.
/// </summary>
public static class ProfileOwnershipVerifier
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static ProfileOwnership VerifySubscription(
        string subscriptionHost,
        bool wasHttps,
        IReadOnlyDictionary<string, string> providerHeaders,
        OwnershipPolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(providerHeaders);
        ArgumentNullException.ThrowIfNull(policy);
        var host = NetText.ToAsciiHost(subscriptionHost ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(policy.ManifestPublicKeyPem)
            && TryGet(providerHeaders, policy.ManifestHeaderName, out var manifest))
        {
            var (ok, detail) = VerifyManifest(manifest, host, policy.ManifestPublicKeyPem!, nowUtc);
            if (ok)
            {
                return new ProfileOwnership(true, OwnershipMethod.SignedManifest, detail);
            }
        }

        // Official only over HTTPS: over plain HTTP anyone on the path could rewrite the configs.
        if (wasHttps && IsHostAllowed(host, Domain.ProductInfo.OfficialProviderDomains))
        {
            return new ProfileOwnership(true, OwnershipMethod.OfficialDomain, $"Host {host} is an official {Domain.ProductInfo.DisplayName} domain.");
        }

        var domainAllowed = IsHostAllowed(host, policy.AllowedProviderDomains);
        if (domainAllowed)
        {
            return new ProfileOwnership(true, OwnershipMethod.DomainAllowList, $"Host {host} is in the provider allow-list.");
        }

        if (!string.IsNullOrWhiteSpace(policy.MarkerHeaderName)
            && TryGet(providerHeaders, policy.MarkerHeaderName, out var marker)
            && string.Equals(marker.Trim(), policy.MarkerHeaderValue.Trim(), StringComparison.OrdinalIgnoreCase)
            && (wasHttps || !policy.RequireHttpsForHeader)
            && (!policy.HeaderRequiresAllowedDomain || domainAllowed))
        {
            return new ProfileOwnership(true, OwnershipMethod.ProviderHeader, $"Provider sent {policy.MarkerHeaderName}.");
        }

        return ProfileOwnership.NotOwned;
    }

    /// <summary>Fallback for single configs: server, SNI or Host header must be on the owner allow-list.</summary>
    public static ProfileOwnership VerifySingleProfile(ProxyProfile profile, OwnershipPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(policy);
        var candidates = new[] { profile.Server, profile.Tls.ServerName, profile.Transport.Host?.Split(',')[0] };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && IsHostAllowed(NetText.ToAsciiHost(c), policy.SingleConfigAllowedHosts))
            {
                return new ProfileOwnership(true, OwnershipMethod.SingleConfigHostAllowList, $"{NetText.ToAsciiHost(c)} is in the single-config allow-list.");
            }
        }

        return ProfileOwnership.NotOwned;
    }

    public static bool IsHostAllowed(string host, IReadOnlyList<string> allowList) => NetText.IsHostInDomains(host, allowList);

    public static (bool Ok, string Detail) VerifyManifest(string headerValue, string host, string publicKeyPem, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > 16 * 1024)
        {
            return (false, "Manifest missing or too large.");
        }

        var dot = headerValue.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == headerValue.Length - 1)
        {
            return (false, "Manifest is not in payload.signature form.");
        }

        var payloadPart = headerValue[..dot].Trim();
        var signaturePart = headerValue[(dot + 1)..].Trim();
        if (!Base64Text.TryDecode(payloadPart, out var payloadBytes) || !Base64Text.TryDecode(signaturePart, out var signature))
        {
            return (false, "Manifest is not valid base64url.");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            if (ecdsa.KeySize != 256)
            {
                return (false, "Manifest public key must be ECDSA P-256.");
            }

            var signed = Encoding.ASCII.GetBytes(payloadPart);
            var valid = signature.Length == 64
                ? ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                : ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (!valid)
            {
                return (false, "Manifest signature is invalid.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return (false, "Manifest could not be verified: " + ex.Message);
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(payloadBytes, JsonDefaults.NodeOptions, JsonDefaults.UntrustedDocument) as JsonObject;
        }
        catch (JsonException)
        {
            return (false, "Manifest payload is not JSON.");
        }

        if (payload is null || payload["v"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version) || version != 1)
        {
            return (false, "Unsupported manifest version.");
        }

        if (payload["exp"] is JsonValue expValue && expValue.TryGetValue<long>(out var exp)
            && DateTimeOffset.FromUnixTimeSeconds(exp) + ClockSkew < nowUtc)
        {
            return (false, "Manifest has expired.");
        }

        var hosts = payload["hosts"] as JsonArray;
        var hostList = hosts?.Select(h => h is JsonValue hv && hv.TryGetValue<string>(out var s) ? s : string.Empty).ToArray() ?? [];
        if (!IsHostAllowed(host, hostList))
        {
            return (false, "Manifest is not valid for this subscription host.");
        }

        return (true, "Signed provider manifest verified.");
    }

    /// <summary>Creates a manifest header value (used by the owner's tooling and by tests).</summary>
    public static string CreateManifest(IReadOnlyList<string> hosts, DateTimeOffset expiresUtc, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(privateKey);
        var payload = new JsonObject
        {
            ["v"] = 1,
            ["hosts"] = new JsonArray(hosts.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = expiresUtc.ToUnixTimeSeconds(),
        };
        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = privateKey.SignData(Encoding.ASCII.GetBytes(payloadPart), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return payloadPart + "." + Base64Url(sig);
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryGet(IReadOnlyDictionary<string, string> headers, string name, out string value)
    {
        foreach (var (k, v) in headers)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
