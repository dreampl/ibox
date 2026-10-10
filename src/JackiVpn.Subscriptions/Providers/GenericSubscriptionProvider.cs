using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Metadata;
using JackiVpn.Subscriptions.Parsing;

namespace JackiVpn.Subscriptions.Providers;

/// <summary>Any provider serving a plain/Base64/JSON/YAML subscription at one URL.</summary>
public sealed class GenericSubscriptionProvider(IHttpFetcher fetcher, IConfigParser parser, TimeProvider? time = null) : ISubscriptionProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public SubscriptionProviderKind Kind => SubscriptionProviderKind.Generic;

    public async Task<SubscriptionFetchResult> FetchAsync(SubscriptionFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var check = UrlSafetyPolicy.ValidateInitial(request.Url, out var url);
        if (!check.Allowed)
        {
            return SubscriptionFetchResult.Fail(FetchFailureKind.InvalidUrl, check.Reason!);
        }

        FetchResponse response;
        try
        {
            response = await fetcher.SendAsync(ProviderSupport.Get(url!, request.Settings, request.ETag, request.LastModified, request.DeviceHeaders), ct).ConfigureAwait(false);
        }
        catch (FetchException ex)
        {
            return SubscriptionFetchResult.Fail(ex.Kind, ex.Message, ex.StatusCode);
        }

        var now = _time.GetUtcNow();
        var metadata = SubscriptionMetadataParser.FromHeaders(response.Headers, now);
        var providerHeaders = ProviderSupport.CaptureProviderHeaders(response.Headers, request.Ownership);
        var https = response.FinalUrl.Scheme == Uri.UriSchemeHttps;
        var deviceLimit = ProviderSupport.ReadDeviceLimit(response, request.DeviceHeaders is not null);
        if (deviceLimit is { Failure: not FetchFailureKind.None } refused)
        {
            // A refusal (even one served as 200 with placeholder configs) never replaces the stored servers.
            return SubscriptionFetchResult.Fail(refused.Failure, ProviderSupport.DescribeDeviceLimit(refused.Failure), response.StatusCode, metadata: metadata) with
            {
                DeviceLimit = refused.State,
            };
        }

        if (response.NotModified)
        {
            return new SubscriptionFetchResult
            {
                Outcome = FetchOutcome.NotModified,
                Metadata = metadata,
                ETag = response.Header("ETag") ?? request.ETag,
                LastModified = response.Header("Last-Modified") ?? request.LastModified,
                ContentHash = request.PreviousContentHash,
                ResolvedEndpoint = "base",
                ProviderHeaders = providerHeaders,
                WasHttps = https,
                DeviceLimit = deviceLimit?.State,
            };
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return SubscriptionFetchResult.Fail(ProviderSupport.KindForStatus(response.StatusCode), ProviderSupport.DescribeStatus(response.StatusCode), response.StatusCode, metadata: metadata) with
            {
                RetryAfter = response.RetryAfter,
            };
        }

        var hash = ProviderSupport.Hash(response.Body ?? string.Empty);
        var (parsed, failure, error) = ProviderSupport.ParseBody(parser, response.Body, request.ParseContext);
        if (failure != FetchFailureKind.None)
        {
            return SubscriptionFetchResult.Fail(failure, error!, response.StatusCode, metadata: metadata) with { Parsed = parsed, Format = parsed?.Format ?? default };
        }

        return new SubscriptionFetchResult
        {
            Outcome = hash == request.PreviousContentHash ? FetchOutcome.NotModified : FetchOutcome.Updated,
            Metadata = metadata,
            Parsed = parsed,
            ETag = response.Header("ETag"),
            LastModified = response.Header("Last-Modified"),
            ContentHash = hash,
            ResolvedEndpoint = "base",
            Format = parsed!.Format,
            ProviderHeaders = providerHeaders,
            WasHttps = https,
            HttpStatus = response.StatusCode,
            DeviceLimit = deviceLimit?.State,
        };
    }
}
