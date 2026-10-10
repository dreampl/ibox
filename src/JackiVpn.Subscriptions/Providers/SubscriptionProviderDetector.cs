using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Metadata;

namespace JackiVpn.Subscriptions.Providers;

/// <summary>Chooses PasarGuard vs Generic when the user leaves the provider on "Auto".</summary>
public sealed class SubscriptionProviderDetector(IHttpFetcher fetcher)
{
    public async Task<SubscriptionProviderKind> DetectAsync(string url, SubscriptionSettings settings, CancellationToken ct, IReadOnlyDictionary<string, string>? deviceHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PasarGuardEndpoints.TryParse(uri, out var pg))
        {
            return SubscriptionProviderKind.Generic;
        }

        // The official providers run PasarGuard: use the full PasarGuard path (quota, expiry, endpoint fallback).
        if (OfficialProviders.IsOfficialUrl(uri))
        {
            return SubscriptionProviderKind.PasarGuard;
        }

        try
        {
            var info = await fetcher.SendAsync(ProviderSupport.Get(pg!.For(PasarGuardEndpoints.Info), settings, headers: deviceHeaders) with { MaxBytes = 256 * 1024 }, ct).ConfigureAwait(false);
            if (info.StatusCode is >= 200 and < 300 && info.Body is not null
                && SubscriptionMetadataParser.FromPasarGuardInfo(info.Body, DateTimeOffset.UtcNow) is not null)
            {
                return SubscriptionProviderKind.PasarGuard;
            }

            var head = await fetcher.SendAsync(ProviderSupport.Head(pg.BaseUrl, settings, deviceHeaders), ct).ConfigureAwait(false);
            if (IsPasarGuardHeaders(head.Headers))
            {
                return SubscriptionProviderKind.PasarGuard;
            }
        }
        catch (FetchException)
        {
            // Unknown: fall through to Generic; the user can still pick PasarGuard explicitly.
        }

        return SubscriptionProviderKind.Generic;
    }

    public static bool IsPasarGuardHeaders(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var title = headers.TryGetValue(SubscriptionMetadataParser.TitleHeaderName, out var t) ? t : null;
        return headers.ContainsKey(SubscriptionMetadataParser.WebPageUrlHeaderName)
            || headers.ContainsKey(SubscriptionMetadataParser.AnnounceHeaderName)
            || (title?.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) == true && headers.ContainsKey(SubscriptionMetadataParser.UserInfoHeaderName));
    }
}
