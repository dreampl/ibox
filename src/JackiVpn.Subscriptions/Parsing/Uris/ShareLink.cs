using System.Globalization;
using JackiVpn.Domain.Common;

namespace JackiVpn.Subscriptions.Parsing.Uris;

/// <summary>
/// Manual, lossless decomposition of <c>scheme://userinfo@host:port/path?query#fragment</c>.
/// <see cref="Uri"/> is deliberately not used: it rejects/mangles many real-world share links
/// (unusual schemes, raw Unicode names, '@' in passwords, IPv6 without brackets, port lists).
/// </summary>
internal sealed class ShareLink
{
    private ShareLink(string scheme, string? userInfo, string host, string? portText, string? path, LinkQuery query, string? fragment, string raw)
    {
        Scheme = scheme;
        UserInfo = userInfo;
        Host = host;
        PortText = portText;
        Path = path;
        Query = query;
        Fragment = fragment;
        Raw = raw;
    }

    public string Scheme { get; }
    public string? UserInfo { get; }
    public string Host { get; }
    public string? PortText { get; }
    public string? Path { get; }
    public LinkQuery Query { get; }
    public string? Fragment { get; }
    public string Raw { get; }

    public static ShareLink Parse(string raw)
    {
        var s = raw.Trim();
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new ProfileParseException("Not a share link (missing scheme).");
        }

        var scheme = s[..schemeEnd].ToLowerInvariant();
        var rest = s[(schemeEnd + 3)..];

        string? fragment = null;
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = LinkQuery.Decode(rest[(hash + 1)..]).Trim();
            rest = rest[..hash];
        }

        string? query = null;
        var q = rest.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        string? path = null;
        var at = rest.LastIndexOf('@');
        var slashSearchStart = at >= 0 ? at + 1 : 0;
        var slash = rest.IndexOf('/', slashSearchStart);
        if (slash >= 0)
        {
            path = rest[slash..];
            rest = rest[..slash];
        }

        string? userInfo = null;
        if (at >= 0)
        {
            userInfo = rest[..at];
            rest = rest[(at + 1)..];
        }

        var (host, port) = SplitHostPort(rest);
        return new ShareLink(scheme, userInfo, host, port, path, new LinkQuery(query), fragment, raw);
    }

    public int RequirePort(int? defaultPort = null)
    {
        if (string.IsNullOrEmpty(PortText))
        {
            return defaultPort ?? throw new ProfileParseException("Port is missing.");
        }

        // "443,20000-30000" (Hysteria2 multi-port): the first entry is the primary port.
        var first = PortText.Split(',')[0].Split('-')[0];
        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || !NetText.IsValidPort(port))
        {
            throw new ProfileParseException("Port is invalid.");
        }

        return port;
    }

    public string RequireHost()
    {
        if (!NetText.IsValidServerAddress(Host))
        {
            throw new ProfileParseException("Server address is missing or invalid.");
        }

        return NetText.TrimBrackets(Host);
    }

    private static (string Host, string? Port) SplitHostPort(string authority)
    {
        authority = authority.Trim();
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ProfileParseException("Unterminated IPv6 literal.");
            }

            var host = authority[1..close];
            var after = authority[(close + 1)..];
            return (host, after.StartsWith(':') ? after[1..] : null);
        }

        // Unbracketed IPv6 (more than one ':') — only accept if the whole thing is an address.
        if (authority.Count(c => c == ':') > 1)
        {
            if (NetText.IsIpLiteral(authority))
            {
                return (authority, null);
            }

            var lastColon = authority.LastIndexOf(':');
            var candidateHost = authority[..lastColon];
            if (NetText.IsIpLiteral(candidateHost))
            {
                return (candidateHost, authority[(lastColon + 1)..]);
            }

            throw new ProfileParseException("Ambiguous host:port.");
        }

        var colon = authority.LastIndexOf(':');
        return colon >= 0 ? (authority[..colon], authority[(colon + 1)..]) : (authority, null);
    }
}
