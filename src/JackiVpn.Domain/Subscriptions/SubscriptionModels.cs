using System.Text.Json.Serialization;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Domain.Subscriptions;

[JsonConverter(typeof(JsonStringEnumConverter<SubscriptionProviderKind>))]
public enum SubscriptionProviderKind
{
    Generic = 0,
    PasarGuard,
}

[JsonConverter(typeof(JsonStringEnumConverter<AutoUpdateInterval>))]
public enum AutoUpdateInterval
{
    ProviderDefault = 0,
    Off,
    Minutes15,
    Minutes30,
    Hour1,
    Hours6,
    Hours12,
    Hours24,
}

[JsonConverter(typeof(JsonStringEnumConverter<SubscriptionStatus>))]
public enum SubscriptionStatus
{
    Unknown = 0,
    Active,
    Expired,
    Limited,
    Disabled,
    OnHold,
}

[JsonConverter(typeof(JsonStringEnumConverter<OwnershipMethod>))]
public enum OwnershipMethod
{
    None = 0,
    DomainAllowList,
    ProviderHeader,
    SignedManifest,
    SingleConfigHostAllowList,

    /// <summary>One of the built-in official provider domains, fetched over HTTPS.</summary>
    OfficialDomain,
}

/// <summary>What the provider told us about its per-subscription device limit (HWID), as of the last update.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLimitState>))]
public enum DeviceLimitState
{
    /// <summary>The provider gave no signal (most providers, and PasarGuard while under its limit).</summary>
    Unknown = 0,

    /// <summary>The provider confirmed a device limit is enforced and this device is accepted.</summary>
    Registered,

    /// <summary>The provider refused this device: the subscription's device limit is reached.</summary>
    LimitReached,

    /// <summary>The provider enforces a device limit and refuses clients that do not send a device ID.</summary>
    DeviceIdRequired,
}

/// <summary>The owner's official providers (see <see cref="ProductInfo.OfficialProviderDomains"/>).</summary>
public static class OfficialProviders
{
    /// <summary>HTTPS URL on an official domain or one of its sub-domains (label match, never substring).</summary>
    public static bool IsOfficialUrl(Uri? url) =>
        url is { IsAbsoluteUri: true } u && u.Scheme == Uri.UriSchemeHttps && Common.NetText.IsHostInDomains(u.IdnHost, ProductInfo.OfficialProviderDomains);

    public static bool IsOfficialUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && IsOfficialUrl(u);

    /// <summary>Port of the official panels' Iran entry (<c>https://sub.&lt;domain&gt;:2083/sub/…</c>).</summary>
    public const int IranEntryPort = 2083;

    /// <summary>
    /// The same official subscription through the Iran entry server. It sees the user's real carrier, so users on
    /// networks where direct paths are throttled also receive the relay configs. Null when not applicable.
    /// </summary>
    public static string? IranEntryUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsOfficialUrl(u) || !u.AbsolutePath.StartsWith("/sub/", StringComparison.Ordinal))
        {
            return null;
        }

        var labels = u.IdnHost.Split('.');
        if (labels.Length < 2 || (labels.Length > 2 && labels[0] == "sub" && u.Port == IranEntryPort))
        {
            return null;
        }

        var domain = labels[^2] + "." + labels[^1];
        return new UriBuilder(u) { Host = "sub." + domain, Port = IranEntryPort }.Uri.ToString();
    }
}

/// <summary>Result of verifying whether a subscription/profile belongs to the owner's infrastructure.</summary>
public sealed record ProfileOwnership(bool IsOwned, OwnershipMethod Method, string? Detail)
{
    public static readonly ProfileOwnership NotOwned = new(false, OwnershipMethod.None, null);
}

/// <summary>
/// Everything the provider told us about the subscription. Every field is nullable: when a value was not
/// provided it stays null and the UI shows "Unknown" — values are never guessed.
/// </summary>
public sealed record SubscriptionMetadata
{
    public long? UploadBytes { get; init; }
    public long? DownloadBytes { get; init; }

    /// <summary>Quota in bytes. Null = unknown. See <see cref="IsUnlimitedTraffic"/>.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>True when the provider explicitly reported no traffic limit (e.g. <c>total=0</c>).</summary>
    public bool? IsUnlimitedTraffic { get; init; }

    public DateTimeOffset? ExpireUtc { get; init; }

    /// <summary>True when the provider explicitly reported no expiry (e.g. <c>expire=0</c>).</summary>
    public bool? IsUnlimitedExpiry { get; init; }

    public string? Title { get; init; }

    public TimeSpan? ProviderUpdateInterval { get; init; }

    public string? SupportUrl { get; init; }

    public string? WebPageUrl { get; init; }

    public string? Announcement { get; init; }

    /// <summary>Provider-reported status string (PasarGuard: active/disabled/limited/expired/on_hold).</summary>
    public string? ProviderStatus { get; init; }

    public string? Username { get; init; }

    /// <summary>Simultaneous devices the provider allows (PasarGuard <c>hwid_limit</c>; 0/absent = no limit).</summary>
    public int? DeviceLimit { get; init; }

    /// <summary>When the provider last saw this account online (any device; PasarGuard <c>online_at</c>).</summary>
    public DateTimeOffset? OnlineAtUtc { get; init; }

    /// <summary>When the account information (/info) was last read.</summary>
    public DateTimeOffset? AccountCheckedUtc { get; init; }

    public DateTimeOffset? RetrievedUtc { get; init; }

    [JsonIgnore]
    public long? UsedBytes => UploadBytes is null && DownloadBytes is null ? null : (UploadBytes ?? 0) + (DownloadBytes ?? 0);

    [JsonIgnore]
    public long? RemainingBytes => IsUnlimitedTraffic == true || TotalBytes is null or <= 0 || UsedBytes is null
        ? null
        : Math.Max(0, TotalBytes.Value - UsedBytes.Value);

    /// <summary>Usage percentage in [0,100], or null when the quota is unknown or unlimited.</summary>
    [JsonIgnore]
    public double? UsedPercent => IsUnlimitedTraffic == true || TotalBytes is null or <= 0 || UsedBytes is null
        ? null
        : Math.Clamp(UsedBytes.Value * 100.0 / TotalBytes.Value, 0, 100);

    public TimeSpan? TimeRemaining(DateTimeOffset nowUtc) =>
        IsUnlimitedExpiry == true || ExpireUtc is null ? null : ExpireUtc.Value - nowUtc;

    public SubscriptionStatus ComputeStatus(DateTimeOffset nowUtc)
    {
        var provider = ProviderStatus?.Trim().ToLowerInvariant();
        switch (provider)
        {
            case "expired":
                return SubscriptionStatus.Expired;
            case "limited":
                return SubscriptionStatus.Limited;
            case "disabled":
                return SubscriptionStatus.Disabled;
            case "on_hold":
                return SubscriptionStatus.OnHold;
        }

        if (IsUnlimitedExpiry != true && ExpireUtc is { } exp && exp <= nowUtc)
        {
            return SubscriptionStatus.Expired;
        }

        if (IsUnlimitedTraffic != true && TotalBytes is > 0 && UsedBytes is { } used && used >= TotalBytes.Value)
        {
            return SubscriptionStatus.Limited;
        }

        if (provider == "active")
        {
            return SubscriptionStatus.Active;
        }

        // Without any usable signal we do not claim "Active".
        var hasSignal = ExpireUtc is not null || IsUnlimitedExpiry is not null || TotalBytes is not null || IsUnlimitedTraffic is not null;
        return hasSignal ? SubscriptionStatus.Active : SubscriptionStatus.Unknown;
    }
}

public sealed record Subscription
{
    public required string Id { get; init; }

    /// <summary>User-given name; falls back to provider title.</summary>
    public required string Name { get; init; }

    public SubscriptionProviderKind Provider { get; init; } = SubscriptionProviderKind.Generic;

    /// <summary>Masked URL safe for display and logs. The real URL lives only in the secret store.</summary>
    public required string MaskedUrl { get; init; }

    /// <summary>Host of the subscription URL (not secret) — used for ownership checks.</summary>
    public required string Host { get; init; }

    public AutoUpdateInterval AutoUpdate { get; init; } = AutoUpdateInterval.ProviderDefault;

    public bool Enabled { get; init; } = true;

    public SubscriptionMetadata Metadata { get; init; } = new();

    public DateTimeOffset? LastUpdateUtc { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    /// <summary>Redacted error of the last failed attempt.</summary>
    public string? LastError { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    /// <summary>SHA-256 of the last accepted body, used to skip no-op updates.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Which endpoint produced the configs (e.g. <c>base</c>, <c>links</c>, <c>sing_box</c>).</summary>
    public string? ResolvedEndpoint { get; init; }

    public ConfigFormat LastFormat { get; init; } = ConfigFormat.Unknown;

    public int ProfileCount { get; init; }

    public ProfileOwnership Ownership { get; init; } = ProfileOwnership.NotOwned;

    /// <summary>An official provider subscription (verified over HTTPS on a built-in official domain).</summary>
    [JsonIgnore]
    public bool IsOfficial => Ownership.IsOwned && Ownership.Method == OwnershipMethod.OfficialDomain;

    /// <summary>
    /// Send this device's ID (x-hwid…) with subscription requests. null = default: official providers only —
    /// a device ID lets a provider count devices, so third parties get it only when the user allows it.
    /// </summary>
    public bool? SendDeviceId { get; init; }

    public DeviceLimitState DeviceLimit { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public TimeSpan? EffectiveUpdateInterval => AutoUpdate switch
    {
        AutoUpdateInterval.Off => null,
        AutoUpdateInterval.Minutes15 => TimeSpan.FromMinutes(15),
        AutoUpdateInterval.Minutes30 => TimeSpan.FromMinutes(30),
        AutoUpdateInterval.Hour1 => TimeSpan.FromHours(1),
        AutoUpdateInterval.Hours6 => TimeSpan.FromHours(6),
        AutoUpdateInterval.Hours12 => TimeSpan.FromHours(12),
        AutoUpdateInterval.Hours24 => TimeSpan.FromHours(24),
        // Provider default, clamped to a sane range; 12 h when the provider says nothing.
        _ => Metadata.ProviderUpdateInterval is { } p
            ? TimeSpan.FromTicks(Math.Clamp(p.Ticks, TimeSpan.FromMinutes(15).Ticks, TimeSpan.FromDays(7).Ticks))
            : TimeSpan.FromHours(12),
    };
}

/// <summary>A fully computed, validated change set applied to storage in a single transaction.</summary>
/// <param name="RemovedIds">Nodes the provider dropped that the user marked: kept and flagged RemovedByProvider.</param>
/// <param name="DeletedIds">Nodes the provider dropped (or stale leftovers) without user data: deleted.</param>
public sealed record SubscriptionSyncPlan(
    IReadOnlyList<ProxyProfile> Added,
    IReadOnlyList<ProxyProfile> Changed,
    IReadOnlyList<ProxyProfile> Unchanged,
    IReadOnlyList<string> RemovedIds,
    IReadOnlyList<string>? DeletedIds = null)
{
    public IReadOnlyList<string> Deleted => DeletedIds ?? [];

    public bool HasChanges => Added.Count > 0 || Changed.Count > 0 || RemovedIds.Count > 0 || Deleted.Count > 0;
}
