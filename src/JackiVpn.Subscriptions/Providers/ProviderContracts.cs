using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;

namespace JackiVpn.Subscriptions.Providers;

public enum FetchOutcome
{
    Updated = 0,
    NotModified,
    Failed,
}

public sealed record SubscriptionFetchRequest
{
    /// <summary>The real subscription URL. Secret — never log; use <see cref="Security.SecretRedactor.MaskUrl"/>.</summary>
    public required string Url { get; init; }

    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public string? PreviousContentHash { get; init; }

    /// <summary>Endpoint that worked last time (tried first).</summary>
    public string? PreferredEndpoint { get; init; }

    public required SubscriptionSettings Settings { get; init; }
    public required ParseContext ParseContext { get; init; }
    public OwnershipPolicy? Ownership { get; init; }

    /// <summary>Device-ID headers for device-limited providers; null = do not identify this device.</summary>
    public IReadOnlyDictionary<string, string>? DeviceHeaders { get; init; }
}

public sealed record SubscriptionFetchResult
{
    public required FetchOutcome Outcome { get; init; }
    public SubscriptionMetadata Metadata { get; init; } = new();
    public ParseResult? Parsed { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public string? ContentHash { get; init; }
    public string? ResolvedEndpoint { get; init; }
    public ConfigFormat Format { get; init; }

    /// <summary>Response headers relevant for ownership verification (marker/manifest), captured verbatim.</summary>
    public IReadOnlyDictionary<string, string> ProviderHeaders { get; init; } = new Dictionary<string, string>();

    public bool WasHttps { get; init; }
    public FetchFailureKind Failure { get; init; }
    public int? HttpStatus { get; init; }

    /// <summary>Redacted, user-presentable error.</summary>
    public string? Error { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Per-endpoint trace (redacted) for diagnostics.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    /// <summary>The provider's device-limit signal from this update; null when it said nothing.</summary>
    public DeviceLimitState? DeviceLimit { get; init; }

    public static SubscriptionFetchResult Fail(FetchFailureKind kind, string error, int? status = null, IReadOnlyList<string>? trace = null, SubscriptionMetadata? metadata = null) => new()
    {
        Outcome = FetchOutcome.Failed,
        Failure = kind,
        Error = error,
        HttpStatus = status,
        Trace = trace ?? [],
        Metadata = metadata ?? new SubscriptionMetadata(),
    };
}

public interface ISubscriptionProvider
{
    SubscriptionProviderKind Kind { get; }

    Task<SubscriptionFetchResult> FetchAsync(SubscriptionFetchRequest request, CancellationToken ct);
}
