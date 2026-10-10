using System.Globalization;
using System.Text;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Subscriptions.Parsing;

/// <summary>Shared helpers used by every parser to produce consistent, fully populated profiles.</summary>
public static class ProfileFactory
{
    public static ProxyProfile Finish(ProxyProfile p, ParseContext ctx, ConfigFormat format, string? rawSource)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ctx);
        var name = NormalizeName(p.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = DefaultName(p);
        }

        var finished = p with
        {
            Id = string.IsNullOrEmpty(p.Id) ? NewId() : p.Id,
            Name = name,
            Server = NetText.TrimBrackets(p.Server.Trim()),
            Transport = NormalizeTransport(p.Transport),
            Origin = new ProfileOrigin
            {
                Kind = ctx.OriginKind,
                SubscriptionId = ctx.SubscriptionId,
                ProviderId = ctx.ProviderId,
                SourceFormat = format,
                RawSource = ctx.KeepRawSource ? rawSource : null,
                ImportedUtc = ctx.NowUtc,
            },
        };

        return ProfileIdentity.WithFingerprint(finished);
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string DefaultName(ProxyProfile p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.Protocol} {FormatHost(p.Server)}:{p.Port}");

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    /// <summary>Keeps Unicode (Persian, emoji), strips control characters and collapses whitespace.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        var lastSpace = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
                continue;
            }

            lastSpace = false;
            sb.Append(rune.ToString());
        }

        var result = sb.ToString().Trim();
        if (result.Length > ProfileValidator.MaxNameLength)
        {
            // Cut on a rune boundary.
            var cut = new StringBuilder();
            foreach (var rune in result.EnumerateRunes())
            {
                if (cut.Length + rune.Utf16SequenceLength > ProfileValidator.MaxNameLength)
                {
                    break;
                }

                cut.Append(rune.ToString());
            }

            result = cut.ToString();
        }

        return result;
    }

    public static TransportType ParseTransport(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "tcp" or "raw" or "none" => TransportType.Tcp,
        "ws" or "websocket" => TransportType.WebSocket,
        "grpc" or "gun" => TransportType.Grpc,
        "httpupgrade" or "http-upgrade" => TransportType.HttpUpgrade,
        "h2" or "http" or "http2" => TransportType.Http2,
        "quic" => TransportType.Quic,
        "xhttp" or "splithttp" => TransportType.XHttp,
        "kcp" or "mkcp" => TransportType.Kcp,
        _ => throw new ProfileParseException($"Unsupported transport '{Truncate(value)}'."),
    };

    /// <summary>
    /// Normalizes paths and moves Xray-style early data ("/ws?ed=2048") into structured fields so every
    /// core generator can express it natively.
    /// </summary>
    public static TransportOptions NormalizeTransport(TransportOptions t)
    {
        var path = t.Path;
        var maxEarly = t.MaxEarlyData;
        var earlyHeader = t.EarlyDataHeaderName;
        if (!string.IsNullOrEmpty(path))
        {
            if (!path.StartsWith('/'))
            {
                path = "/" + path;
            }

            if (t.Type == TransportType.WebSocket)
            {
                var q = path.IndexOf('?', StringComparison.Ordinal);
                if (q >= 0)
                {
                    var query = new Uris.LinkQuery(path[(q + 1)..]);
                    var ed = query.GetInt("ed");
                    if (ed is > 0)
                    {
                        maxEarly ??= ed;
                        earlyHeader ??= "Sec-WebSocket-Protocol";
                        path = path[..q];
                    }
                }
            }
        }

        if (t.Type == TransportType.Grpc)
        {
            path = null;
        }

        return t with { Path = path, MaxEarlyData = maxEarly, EarlyDataHeaderName = earlyHeader };
    }

    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static TimeSpan? ParseDuration(string? value) => DurationText.Parse(value);

    public static string FormatDuration(TimeSpan d) => DurationText.Format(d);

    internal static string Truncate(string? s) => s is null ? string.Empty : s.Length <= 24 ? s : s[..24] + "…";
}
