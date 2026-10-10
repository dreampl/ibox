using System.Net;
using JackiVpn.Domain.Common;

namespace JackiVpn.Security;

public sealed record UrlCheck(bool Allowed, string? Reason)
{
    public static readonly UrlCheck Ok = new(true, null);

    public static UrlCheck Deny(string reason) => new(false, reason);
}

/// <summary>
/// SSRF policy for remote fetches (subscriptions, rule-sets). The user may enter any http(s) URL —
/// including one on their own LAN — but a fetch that <i>starts</i> on a public host must never be
/// redirected or DNS-rebound onto loopback/private/link-local addresses, and schemes other than
/// http/https are always refused.
/// </summary>
public static class UrlSafetyPolicy
{
    public const int MaxRedirects = 5;
    public const int MaxUrlLength = 4096;

    public static UrlCheck ValidateInitial(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return UrlCheck.Deny("URL is empty.");
        }

        url = url.Trim();
        if (url.Length > MaxUrlLength || NetText.HasControlChars(url))
        {
            return UrlCheck.Deny("URL is too long or contains control characters.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return UrlCheck.Deny("URL is not a valid absolute URL.");
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return UrlCheck.Deny($"Scheme '{parsed.Scheme}' is not allowed; only http and https are accepted.");
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return UrlCheck.Deny("URL has no host.");
        }

        uri = parsed;
        return UrlCheck.Ok;
    }

    /// <summary>True when the URL's host is a literal (or well-known) loopback/private/link-local address.</summary>
    public static bool IsPrivateHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(NetText.TrimBrackets(host), out var ip) && NetText.IsPrivateOrLocal(ip);
    }

    public static UrlCheck ValidateRedirect(Uri origin, Uri current, Uri? target, int hop)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(current);
        if (target is null)
        {
            return UrlCheck.Deny("Redirect without a Location header.");
        }

        if (hop > MaxRedirects)
        {
            return UrlCheck.Deny($"Too many redirects (>{MaxRedirects}).");
        }

        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
        {
            return UrlCheck.Deny($"Redirect to scheme '{target.Scheme}' refused.");
        }

        if (current.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
        {
            return UrlCheck.Deny("Redirect from https to http refused (downgrade).");
        }

        if (!IsPrivateHost(origin) && IsPrivateHost(target))
        {
            return UrlCheck.Deny("Redirect from a public host to a private/loopback address refused.");
        }

        return UrlCheck.Ok;
    }

    /// <summary>Connect-time check of the resolved address (defends against DNS rebinding).</summary>
    public static UrlCheck ValidateResolvedAddress(Uri origin, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(address);
        if (!IsPrivateHost(origin) && NetText.IsPrivateOrLocal(address))
        {
            return UrlCheck.Deny("Public host resolved to a private/loopback address; connection refused.");
        }

        return UrlCheck.Ok;
    }
}
