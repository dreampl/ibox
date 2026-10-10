using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace JackiVpn.Security;

/// <summary>
/// Removes secrets from free text before it reaches a log sink, the UI log viewer, a crash report or a
/// diagnostic bundle. Redaction is mandatory and layered:
/// <list type="number">
/// <item>Exact-match of secrets registered at runtime (active subscription URL/token, active credentials).</item>
/// <item>Proxy share links (<c>vless://</c>, <c>vmess://</c>, …): user-info and secret query parameters.</item>
/// <item>HTTP(S) URLs: long opaque path segments (subscription tokens) and secret query parameters.</item>
/// <item>JSON / YAML / key=value pairs whose key names a secret.</item>
/// <item>UUIDs, Authorization/Cookie headers, PEM private keys, long Base64 blobs.</item>
/// </list>
/// The class is thread-safe and allocation-light when nothing matches.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    private static readonly ConcurrentDictionary<string, byte> Registered = new(StringComparer.Ordinal);

    private static readonly string[] SecretKeyNames =
    [
        "uuid", "id", "password", "pass", "passwd", "secret", "token", "auth", "auth_str", "authorization",
        "private_key", "privatekey", "secretkey", "secret_key", "pre_shared_key", "presharedkey", "psk",
        "public_key", "publickey", "pbk", "short_id", "shortid", "sid", "spx", "spider_x", "obfs-password",
        "obfs_password", "obfspassword", "seed", "key", "cookie", "access_token", "refresh_token", "api_key",
        "apikey", "signature", "sig", "credential", "credentials", "username", "user",
    ];

    private static readonly HashSet<string> SecretKeySet = new(SecretKeyNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a secret for exact-match redaction (e.g. the active subscription token). Short values are ignored.</summary>
    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 6)
        {
            Registered.TryAdd(secret, 0);
        }
    }

    public static void Unregister(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            Registered.TryRemove(secret, out _);
        }
    }

    public static int RegisteredCount => Registered.Count;

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var s = text;
        if (!Registered.IsEmpty)
        {
            foreach (var secret in Registered.Keys)
            {
                if (s.Contains(secret, StringComparison.Ordinal))
                {
                    s = s.Replace(secret, Mask, StringComparison.Ordinal);
                }
            }
        }

        s = PemPrivateKeyRegex().Replace(s, "-----BEGIN PRIVATE KEY-----" + Mask + "-----END PRIVATE KEY-----");
        s = VmessLinkRegex().Replace(s, m => m.Groups["scheme"].Value + "://" + Mask);
        s = ShareLinkRegex().Replace(s, RedactShareLink);
        s = HttpUrlRegex().Replace(s, m => RedactHttpUrl(m.Value));
        s = HeaderRegex().Replace(s, m => m.Groups["name"].Value + m.Groups["sep"].Value + Mask);
        s = JsonPairRegex().Replace(s, RedactJsonPair);
        s = KeyValueRegex().Replace(s, RedactKeyValue);
        s = UuidRegex().Replace(s, "********-****-****-****-************");
        s = LongBase64Regex().Replace(s, m => m.Value.Length >= 64 ? "[base64:" + m.Value.Length + "]" : m.Value);
        return s;
    }

    /// <summary>Masks a subscription/HTTP URL for display: keeps scheme, host and short segments.</summary>
    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Redact(url);
        }

        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://");
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            sb.Append(Mask).Append('@');
        }

        sb.Append(uri.IdnHost);
        if (!uri.IsDefaultPort)
        {
            sb.Append(':').Append(uri.Port);
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append('/').Append(MaskSegment(Uri.UnescapeDataString(segment)));
        }

        if (uri.AbsolutePath.EndsWith('/') && uri.AbsolutePath.Length > 1)
        {
            sb.Append('/');
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            sb.Append('?').Append(Mask);
        }

        return sb.ToString();
    }

    internal static string MaskSegment(string segment)
    {
        // Short, human-readable segments ("sub", "links", "sing_box") are kept; opaque tokens are masked.
        if (segment.Length < 12 && !UuidRegex().IsMatch(segment))
        {
            return segment;
        }

        return segment.Length <= 6 ? Mask : segment[..2] + "••••" + segment[^2..];
    }

    private static string RedactHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var hasSecretPath = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s.Length >= 12 && OpaqueTokenRegex().IsMatch(s));
        if (!hasSecretPath && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query))
        {
            return url;
        }

        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://");
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            sb.Append(Mask).Append('@');
        }

        sb.Append(uri.Authority.Contains('@', StringComparison.Ordinal) ? uri.Authority[(uri.Authority.IndexOf('@', StringComparison.Ordinal) + 1)..] : uri.Authority);
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append('/').Append(segment.Length >= 12 && OpaqueTokenRegex().IsMatch(segment) ? MaskSegment(segment) : segment);
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            sb.Append('?').Append(RedactQuery(uri.Query.TrimStart('?')));
        }

        return sb.ToString();
    }

    private static string RedactShareLink(Match m)
    {
        var scheme = m.Groups["scheme"].Value;
        var rest = m.Groups["rest"].Value;

        // Drop the fragment (node name may be fine, but it is not needed in logs and can carry tokens).
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        var fragment = string.Empty;
        if (hash >= 0)
        {
            fragment = "#…";
            rest = rest[..hash];
        }

        var at = rest.LastIndexOf('@');
        var hostPart = at >= 0 ? rest[(at + 1)..] : rest;
        var userPart = at >= 0 ? Mask + "@" : string.Empty;

        // Legacy base64 "ss://BASE64" with no '@' — the whole body is secret.
        if (at < 0 && (scheme.Equals("ss", StringComparison.OrdinalIgnoreCase) || scheme.Equals("ssr", StringComparison.OrdinalIgnoreCase)))
        {
            return scheme + "://" + Mask + fragment;
        }

        var q = hostPart.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            hostPart = hostPart[..q] + "?" + RedactQuery(hostPart[(q + 1)..]);
        }

        return scheme + "://" + userPart + hostPart + fragment;
    }

    private static string RedactQuery(string query)
    {
        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(parts[i][..eq]);
            if (SecretKeySet.Contains(key) || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("pass", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = parts[i][..eq] + "=" + Mask;
            }
        }

        return string.Join('&', parts);
    }

    private static string RedactJsonPair(Match m)
    {
        var key = m.Groups["key"].Value;
        return IsSecretKey(key) ? m.Groups["prefix"].Value + "\"" + Mask + "\"" : m.Value;
    }

    private static string RedactKeyValue(Match m)
    {
        var key = m.Groups["key"].Value;
        return IsSecretKey(key) ? m.Groups["prefix"].Value + Mask : m.Value;
    }

    private static bool IsSecretKey(string key)
    {
        var k = key.Trim().Trim('"', '\'');
        return SecretKeySet.Contains(k)
            || k.EndsWith("password", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("_key", StringComparison.OrdinalIgnoreCase) && !k.Equals("cache_key", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("token", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("secret", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKeyRegex();

    [GeneratedRegex(@"(?<scheme>vmess)://[A-Za-z0-9+/=_\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VmessLinkRegex();

    [GeneratedRegex(@"(?<scheme>vless|trojan|ss|ssr|socks5?|hysteria2|hy2|hysteria|tuic|wireguard|wg|naive\+https|anytls)://(?<rest>[^\s""'<>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShareLinkRegex();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"(?<name>(?:Proxy-)?Authorization|Cookie|Set-Cookie|X-Api-Key)(?<sep>\s*[:=]\s*)[^\r\n,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?<prefix>""(?<key>[A-Za-z0-9_\-]{1,40})""\s*:\s*)""(?:[^""\\]|\\.)*""", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPairRegex();

    [GeneratedRegex(@"(?<prefix>(?<![A-Za-z0-9_\-])(?<key>[A-Za-z][A-Za-z0-9_\-]{0,39})\s*[:=]\s*)(?![\s""{\[])[^\s,;&""'}\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{64,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex LongBase64Regex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-=.~%]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueTokenRegex();
}
