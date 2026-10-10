using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Parsing.Json;

namespace JackiVpn.Subscriptions.Metadata;

public sealed record UserInfoHeader(long? Upload, long? Download, long? Total, long? ExpireUnixSeconds);

/// <summary>Parses the de-facto standard subscription response headers and PasarGuard's <c>/info</c> JSON.</summary>
public static class SubscriptionMetadataParser
{
    public const string UserInfoHeaderName = "subscription-userinfo";
    public const string TitleHeaderName = "profile-title";
    public const string UpdateIntervalHeaderName = "profile-update-interval";
    public const string SupportUrlHeaderName = "support-url";
    public const string WebPageUrlHeaderName = "profile-web-page-url";
    public const string AnnounceHeaderName = "announce";

    /// <summary>
    /// Parses <c>upload=0; download=123; total=456; expire=1700000000</c>. Tolerates spaces, missing keys,
    /// floats/exponents and unknown keys. Returns null when nothing usable is present.
    /// </summary>
    public static UserInfoHeader? ParseUserInfo(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 1024)
        {
            return null;
        }

        long? upload = null, download = null, total = null, expire = null;
        foreach (var part in header.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim().ToLowerInvariant();
            var value = ParseNumber(part[(eq + 1)..].Trim());
            switch (key)
            {
                case "upload":
                    upload = value;
                    break;
                case "download":
                    download = value;
                    break;
                case "total":
                    total = value;
                    break;
                case "expire":
                    expire = value;
                    break;
            }
        }

        return upload is null && download is null && total is null && expire is null
            ? null
            : new UserInfoHeader(upload, download, total, expire);
    }

    private static long? ParseNumber(string s)
    {
        if (s.Length == 0)
        {
            return null;
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l < 0 ? null : l;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d < 9.2e18
            ? (long)d
            : null;
    }

    /// <summary>Decodes <c>base64:…</c> titles (PasarGuard/Marzban) or plain/percent-encoded titles.</summary>
    public static string? DecodeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            return Base64Text.TryDecodeToString(value[7..], 4096, out var decoded) ? Clean(decoded) : null;
        }

        try
        {
            return Clean(Uri.UnescapeDataString(value));
        }
        catch (UriFormatException)
        {
            return Clean(value);
        }
    }

    private static string? Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        var r = sb.ToString().Trim();
        return r.Length == 0 ? null : r.Length > 200 ? r[..200] : r;
    }

    /// <summary>Builds metadata from response headers. Missing values stay null (Unknown).</summary>
    public static SubscriptionMetadata FromHeaders(IReadOnlyDictionary<string, string> headers, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var info = ParseUserInfo(Get(headers, UserInfoHeaderName));
        TimeSpan? interval = null;
        if (Get(headers, UpdateIntervalHeaderName) is { } iv
            && double.TryParse(iv, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours > 0 && hours < 24 * 365)
        {
            interval = TimeSpan.FromHours(hours);
        }

        return new SubscriptionMetadata
        {
            UploadBytes = info?.Upload,
            DownloadBytes = info?.Download,
            TotalBytes = info?.Total is > 0 ? info.Total : null,
            IsUnlimitedTraffic = info?.Total switch
            {
                null => null,
                0 => true,
                _ => false,
            },
            ExpireUtc = info?.ExpireUnixSeconds is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(info.ExpireUnixSeconds.Value) : null,
            IsUnlimitedExpiry = info?.ExpireUnixSeconds switch
            {
                null => null,
                0 => true,
                _ => false,
            },
            Title = DecodeTitle(Get(headers, TitleHeaderName)),
            ProviderUpdateInterval = interval,
            SupportUrl = SafeHttpUrl(Get(headers, SupportUrlHeaderName)),
            WebPageUrl = SafeHttpUrl(Get(headers, WebPageUrlHeaderName)),
            Announcement = DecodeTitle(Get(headers, AnnounceHeaderName)),
            RetrievedUtc = nowUtc,
        };
    }

    /// <summary>
    /// Parses PasarGuard <c>GET /sub/{token}/info</c>. Fields: status, used_traffic, lifetime_used_traffic,
    /// data_limit (null/0 = unlimited), expire (ISO-8601, unix seconds, null/0 = never), username.
    /// </summary>
    public static SubscriptionMetadata? FromPasarGuardInfo(string json, DateTimeOffset nowUtc)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(json, JsonDefaults.NodeOptions, JsonDefaults.UntrustedDocument) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (o is null || (o.Str("username") is null && o.Str("status") is null && o.Str("used_traffic") is null))
        {
            return null;
        }

        var used = ToLong(o.Str("used_traffic"));
        var limitRaw = o.TryGetPropertyValue("data_limit", out var dl) ? dl : null;
        var limit = ToLong(o.Str("data_limit"));
        var expireRaw = o.TryGetPropertyValue("expire", out var ex) ? ex : null;
        var (expire, unlimitedExpiry) = ParseExpire(expireRaw, o.ContainsKey("expire"));

        return new SubscriptionMetadata
        {
            UploadBytes = used is null ? null : 0,
            DownloadBytes = used,
            TotalBytes = limit is > 0 ? limit : null,
            IsUnlimitedTraffic = o.ContainsKey("data_limit") ? limitRaw is null || limit is 0 : null,
            ExpireUtc = expire,
            IsUnlimitedExpiry = unlimitedExpiry,
            ProviderStatus = o.Str("status"),
            Username = o.Str("username"),
            DeviceLimit = ToLong(o.Str("hwid_limit")) is { } hl and >= 0 and <= 10_000 ? (int)hl : null,
            OnlineAtUtc = ParseTimestamp(o.Str("online_at")),
            AccountCheckedUtc = nowUtc,
            RetrievedUtc = nowUtc,
        };
    }

    /// <summary>ISO-8601 (PasarGuard sends naive UTC like "2026-10-02T10:20:30") or unix seconds.</summary>
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var unix) && unix > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(Math.Min(unix, 253402300799));
        }

        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    private static (DateTimeOffset? Expire, bool? Unlimited) ParseExpire(JsonNode? node, bool present)
    {
        if (!present)
        {
            return (null, null);
        }

        if (node is null)
        {
            return (null, true);
        }

        var kind = node.GetValueKind();
        if (kind == JsonValueKind.Number && node is JsonValue v && v.TryGetValue<long>(out var unix))
        {
            return unix <= 0 ? (null, true) : (DateTimeOffset.FromUnixTimeSeconds(Math.Min(unix, 253402300799)), false);
        }

        if (kind == JsonValueKind.String)
        {
            var s = node.GetValue<string>();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
            {
                return u <= 0 ? (null, true) : (DateTimeOffset.FromUnixTimeSeconds(Math.Min(u, 253402300799)), false);
            }

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return (dto, false);
            }
        }

        return (null, null);
    }

    private static long? ToLong(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? (long)d : null;

    /// <summary>Header-supplied links are displayed to the user; only accept http(s).</summary>
    private static string? SafeHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && !NetText.HasControlChars(value)
            ? u.ToString()
            : null;

    private static string? Get(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>Overlays <paramref name="overlay"/> values on top of <paramref name="baseline"/> (non-null wins).</summary>
    public static SubscriptionMetadata Merge(SubscriptionMetadata baseline, SubscriptionMetadata? overlay)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (overlay is null)
        {
            return baseline;
        }

        return baseline with
        {
            UploadBytes = overlay.UploadBytes ?? baseline.UploadBytes,
            DownloadBytes = overlay.DownloadBytes ?? baseline.DownloadBytes,
            TotalBytes = overlay.IsUnlimitedTraffic == true ? null : overlay.TotalBytes ?? baseline.TotalBytes,
            IsUnlimitedTraffic = overlay.IsUnlimitedTraffic ?? baseline.IsUnlimitedTraffic,
            ExpireUtc = overlay.IsUnlimitedExpiry == true ? null : overlay.ExpireUtc ?? baseline.ExpireUtc,
            IsUnlimitedExpiry = overlay.IsUnlimitedExpiry ?? baseline.IsUnlimitedExpiry,
            Title = overlay.Title ?? baseline.Title,
            ProviderUpdateInterval = overlay.ProviderUpdateInterval ?? baseline.ProviderUpdateInterval,
            SupportUrl = overlay.SupportUrl ?? baseline.SupportUrl,
            WebPageUrl = overlay.WebPageUrl ?? baseline.WebPageUrl,
            Announcement = overlay.Announcement ?? baseline.Announcement,
            ProviderStatus = overlay.ProviderStatus ?? baseline.ProviderStatus,
            Username = overlay.Username ?? baseline.Username,
            DeviceLimit = overlay.AccountCheckedUtc is not null ? overlay.DeviceLimit : baseline.DeviceLimit,
            OnlineAtUtc = overlay.OnlineAtUtc ?? baseline.OnlineAtUtc,
            AccountCheckedUtc = overlay.AccountCheckedUtc ?? baseline.AccountCheckedUtc,
            RetrievedUtc = overlay.RetrievedUtc ?? baseline.RetrievedUtc,
        };
    }
}
