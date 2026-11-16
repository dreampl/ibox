using System.Globalization;
using JackiVpn.Domain.Common;

namespace JackiVpn.Domain.Profiles;

public enum DnsttResolverKind
{
    /// <summary>Plain DNS over UDP (dnstt <c>-udp</c>).</summary>
    Udp = 0,

    /// <summary>DNS over HTTPS (dnstt <c>-doh</c>).</summary>
    Doh,

    /// <summary>DNS over TLS (dnstt <c>-dot</c>).</summary>
    Dot,

    /// <summary>The network's own DNS (the carrier's), found when connecting; plain UDP.</summary>
    System,
}

/// <summary>A parsed DNS tunnel resolver: what kind it is and the exact value the dnstt client is given.</summary>
public sealed record DnsttResolver(DnsttResolverKind Kind, string Host, int Port, string Value)
{
    /// <summary>Stands for the network's own DNS servers in a resolver list.</summary>
    public const string SystemToken = "system";

    /// <summary>Stands for <see cref="AutoOrder"/> in a resolver list.</summary>
    public const string AutoToken = "auto";

    /// <summary>
    /// What "auto" tries, best first: the carrier's own DNS, the big public resolvers, Shecan, then DNS over HTTPS
    /// (names are encrypted there, so filters on long names do not see them).
    /// </summary>
    public static IReadOnlyList<string> AutoOrder { get; } =
    [
        SystemToken, "8.8.8.8", "1.1.1.1", "178.22.122.100", "https://dns.google/dns-query", "https://cloudflare-dns.com/dns-query",
    ];

    /// <summary>
    /// A resolver setting is one resolver or several separated by commas, tried in that order; <c>auto</c> and
    /// <c>system</c> expand. Null when any entry is invalid or the list is empty.
    /// </summary>
    public static IReadOnlyList<string>? ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var raw in text.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals(AutoToken, StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(AutoOrder);
            }
            else if (raw.Equals(SystemToken, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(SystemToken);
            }
            else if (TryParse(raw) is { } r)
            {
                result.Add(r.Kind == DnsttResolverKind.Doh ? r.Value : raw);
            }
            else
            {
                return null;
            }
        }

        var distinct = result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count is > 0 and <= 12 ? distinct : null;
    }

    /// <summary>
    /// Accepts <c>1.2.3.4</c>, <c>1.2.3.4:53</c>, <c>[2001:db8::1]:53</c>, <c>udp://…</c>, <c>https://host/path</c>
    /// and <c>tls://host[:853]</c>. Returns null for anything else.
    /// </summary>
    public static DnsttResolver? TryParse(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 512 || NetText.HasControlChars(s) || s.Contains('"', StringComparison.Ordinal))
        {
            return null;
        }

        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(s, UriKind.Absolute, out var u) && NetText.IsValidServerAddress(NetText.TrimBrackets(u.Host))
                ? new DnsttResolver(DnsttResolverKind.Doh, NetText.TrimBrackets(u.Host), u.Port, u.AbsoluteUri)
                : null;
        }

        var kind = DnsttResolverKind.Udp;
        if (s.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
        {
            kind = DnsttResolverKind.Dot;
            s = s[6..];
        }
        else if (s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            s = s[6..];
        }

        s = s.TrimEnd('/');
        var defaultPort = kind == DnsttResolverKind.Dot ? 853 : 53;
        string host;
        var port = defaultPort;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port)))
            {
                return null;
            }
        }
        else if (s.Count(c => c == ':') == 1)
        {
            var colon = s.IndexOf(':', StringComparison.Ordinal);
            host = s[..colon];
            if (!int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return null;
            }
        }
        else
        {
            host = s;
        }

        if (!NetText.IsValidPort(port) || !NetText.IsValidServerAddress(host))
        {
            return null;
        }

        // Plain DNS needs an address: the client cannot look up its own resolver without DNS.
        if (kind == DnsttResolverKind.Udp && !NetText.IsIpLiteral(host))
        {
            return null;
        }

        var hostPart = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return new DnsttResolver(kind, host, port, $"{hostPart}:{port.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>True for a 32-byte key written as 64 hex characters.</summary>
    public static bool IsPublicKey(string? key) =>
        key is { Length: 64 } && key.All(Uri.IsHexDigit);
}
