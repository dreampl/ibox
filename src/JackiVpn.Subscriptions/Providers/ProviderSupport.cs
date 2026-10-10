using System.Security.Cryptography;
using System.Text;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;

namespace JackiVpn.Subscriptions.Providers;

internal static class ProviderSupport
{
    public static string Hash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public static FetchFailureKind KindForStatus(int status) => status switch
    {
        401 => FetchFailureKind.Unauthorized,
        403 => FetchFailureKind.Forbidden,
        404 or 410 => FetchFailureKind.NotFound,
        429 => FetchFailureKind.RateLimited,
        >= 500 => FetchFailureKind.ServerError,
        _ => FetchFailureKind.HttpError,
    };

    public static string DescribeStatus(int status) => status switch
    {
        401 => "HTTP 401: the provider rejected the subscription (unauthorized).",
        403 => "HTTP 403: access to the subscription is forbidden (disabled or blocked).",
        404 => "HTTP 404: subscription not found (token invalid or revoked).",
        410 => "HTTP 410: subscription was removed.",
        429 => "HTTP 429: the provider is rate-limiting requests; try again later.",
        >= 500 => $"HTTP {status}: provider server error.",
        _ => $"Unexpected HTTP status {status}.",
    };

    public static FetchRequest Get(Uri url, SubscriptionSettings s, string? etag = null, string? lastModified = null, IReadOnlyDictionary<string, string>? headers = null) => new()
    {
        Url = url,
        Method = HttpMethod.Get,
        UserAgent = s.UserAgent,
        MaxBytes = s.MaxResponseBytes,
        Timeout = s.Timeout,
        IfNoneMatch = etag,
        IfModifiedSince = lastModified,
        Headers = headers,
    };

    public static FetchRequest Head(Uri url, SubscriptionSettings s, IReadOnlyDictionary<string, string>? headers = null) => new()
    {
        Url = url,
        Method = HttpMethod.Head,
        UserAgent = s.UserAgent,
        MaxBytes = s.MaxResponseBytes,
        Timeout = s.Timeout,
        Headers = headers,
    };

    public const string DeviceLimitReachedMessage = "This subscription's device limit is reached, so the provider refused this device. Remove an old device in your account (or ask the provider), then update again.";
    public const string DeviceIdRequiredMessage = "This provider limits devices and only accepts apps that send a device ID. Turn on \"Send device ID\" for this subscription, then update again.";

    /// <summary>
    /// Reads a provider's device-limit (HWID) signals. PasarGuard ≥ 5.1 answers 403 with "Device limit reached" or
    /// "HWID header required"; Remnawave sends x-hwid-active / x-hwid-not-supported / x-hwid-max-devices-reached
    /// (x-hwid-limit on older versions) and 404 without a device ID. Returns null when the response says nothing.
    /// </summary>
    public static (DeviceLimitState State, FetchFailureKind Failure)? ReadDeviceLimit(FetchResponse response, bool sentDeviceId)
    {
        ArgumentNullException.ThrowIfNull(response);
        static bool Flag(FetchResponse r, string name) => r.Header(name) is { } v && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        var detail = response.Body is { Length: > 0 and < 4096 } body ? body : string.Empty;
        if ((response.StatusCode == 403 && detail.Contains("device limit", StringComparison.OrdinalIgnoreCase))
            || Flag(response, "x-hwid-max-devices-reached") || Flag(response, "x-hwid-limit"))
        {
            return (DeviceLimitState.LimitReached, FetchFailureKind.DeviceLimitReached);
        }

        if ((response.StatusCode == 403 && detail.Contains("hwid", StringComparison.OrdinalIgnoreCase))
            || Flag(response, "x-hwid-not-supported")
            || (!sentDeviceId && response.StatusCode == 404 && Flag(response, "x-hwid-active")))
        {
            return (DeviceLimitState.DeviceIdRequired, FetchFailureKind.DeviceIdRequired);
        }

        if (sentDeviceId && response.StatusCode is >= 200 and < 300 && Flag(response, "x-hwid-active"))
        {
            return (DeviceLimitState.Registered, FetchFailureKind.None);
        }

        return null;
    }

    public static string DescribeDeviceLimit(FetchFailureKind kind) =>
        kind == FetchFailureKind.DeviceLimitReached ? DeviceLimitReachedMessage : DeviceIdRequiredMessage;

    /// <summary>Headers used for ownership detection (never contain config secrets).</summary>
    public static Dictionary<string, string> CaptureProviderHeaders(IReadOnlyDictionary<string, string> headers, OwnershipPolicy? policy)
    {
        var names = new List<string>
        {
            policy?.MarkerHeaderName ?? Domain.ProductInfo.DefaultProviderMarkerHeader,
            policy?.ManifestHeaderName ?? Domain.ProductInfo.DefaultProviderManifestHeader,
        };
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (headers.TryGetValue(n, out var v) && v.Length <= 16 * 1024)
            {
                d[n] = v;
            }
        }

        return d;
    }

    /// <summary>Parses a body and classifies the outcome; HTML and empty results are failures (old data kept).</summary>
    public static (ParseResult? Parsed, FetchFailureKind Failure, string? Error) ParseBody(IConfigParser parser, string? body, ParseContext ctx)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, FetchFailureKind.Empty, "The provider returned an empty response.");
        }

        var parsed = parser.Parse(body, ctx);
        if (parsed.Format == ConfigFormat.Html)
        {
            return (parsed, FetchFailureKind.Html, "The provider returned an HTML page instead of a subscription.");
        }

        if (!parsed.HasProfiles)
        {
            var reason = parsed.Issues.FirstOrDefault()?.Message ?? "no usable configs";
            return (parsed, FetchFailureKind.ParseFailed, "No usable configs in the response: " + SecretRedactor.Redact(reason));
        }

        return (parsed, FetchFailureKind.None, null);
    }
}
