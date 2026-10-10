using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Metadata;
using JackiVpn.Subscriptions.Parsing;

namespace JackiVpn.Subscriptions.Providers;

/// <summary>
/// First-class PasarGuard provider.
/// <list type="number">
/// <item>HEAD on the base URL for metadata headers (subscription-userinfo, profile-title, profile-update-interval,
/// support-url, profile-web-page-url, announce, custom marker). Falls back to GET when HEAD is not allowed.</item>
/// <item><c>/info</c> JSON for status / quota / expiry (optional; older panels may not have it).</item>
/// <item>Content endpoints in order: previously working endpoint, then sing_box, xray, links, links_base64,
/// clash_meta, base. HTML responses are never treated as configs. Auth-level failures (401/403/404/429 on
/// the base) stop the walk; a 404 on a single endpoint only skips that endpoint.</item>
/// <item>Conditional requests (ETag / Last-Modified) are sent for the previously resolved endpoint; a content
/// hash comparison additionally avoids no-op updates when the panel sends neither.</item>
/// </list>
/// </summary>
public sealed class PasarGuardSubscriptionProvider(IHttpFetcher fetcher, IConfigParser parser, TimeProvider? time = null) : ISubscriptionProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public SubscriptionProviderKind Kind => SubscriptionProviderKind.PasarGuard;

    /// <summary>
    /// Account check only (<c>/info</c>): device limit, online time, status, usage. Returns the parsed account and
    /// the provider's device-limit answer for this device (null when it said nothing).
    /// </summary>
    public async Task<(SubscriptionMetadata? Account, DeviceLimitState? Device)> FetchAccountAsync(
        string url, Domain.Settings.SubscriptionSettings settings, IReadOnlyDictionary<string, string>? deviceHeaders, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PasarGuardEndpoints.TryParse(uri, out var pg))
        {
            return (null, null);
        }

        var response = await fetcher.SendAsync(ProviderSupport.Get(pg!.For(PasarGuardEndpoints.Info), settings, headers: deviceHeaders) with { MaxBytes = 256 * 1024 }, ct).ConfigureAwait(false);
        var limit = ProviderSupport.ReadDeviceLimit(response, deviceHeaders is not null);
        var account = response.StatusCode is >= 200 and < 300 && response.Body is not null
            ? SubscriptionMetadataParser.FromPasarGuardInfo(response.Body, (time ?? TimeProvider.System).GetUtcNow())
            : null;
        return (account, limit?.State);
    }
    public async Task<SubscriptionFetchResult> FetchAsync(SubscriptionFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trace = new List<string>();
        var check = UrlSafetyPolicy.ValidateInitial(request.Url, out var url);
        if (!check.Allowed)
        {
            return SubscriptionFetchResult.Fail(FetchFailureKind.InvalidUrl, check.Reason!);
        }

        if (!PasarGuardEndpoints.TryParse(url, out var pg))
        {
            return SubscriptionFetchResult.Fail(FetchFailureKind.InvalidUrl, "The URL does not look like a PasarGuard subscription (…/{token}).");
        }

        var now = _time.GetUtcNow();
        var metadata = new SubscriptionMetadata();
        var providerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var device = request.DeviceHeaders;
        var sentDeviceId = device is not null;
        (DeviceLimitState State, FetchFailureKind Failure)? deviceLimit = null;

        // 1) HEAD for metadata (GET fallback when HEAD is unsupported). A refusal (403/404) is re-read with GET:
        //    HEAD has no body, and the body is where PasarGuard says "Device limit reached" / "HWID header required".
        FetchResponse? head = null;
        try
        {
            head = await fetcher.SendAsync(ProviderSupport.Head(pg!.BaseUrl, request.Settings, device), ct).ConfigureAwait(false);
            trace.Add($"HEAD base -> {head.StatusCode}");
            if (head.StatusCode is 405 or 501 or 400 or 403 or 404)
            {
                head = await fetcher.SendAsync(ProviderSupport.Get(pg.BaseUrl, request.Settings, headers: device), ct).ConfigureAwait(false);
                trace.Add($"GET base (HEAD fallback) -> {head.StatusCode}");
            }

            deviceLimit = ProviderSupport.ReadDeviceLimit(head, sentDeviceId);
            if (deviceLimit is { Failure: not FetchFailureKind.None } refused)
            {
                trace.Add($"device limit: {refused.State}");
                return SubscriptionFetchResult.Fail(refused.Failure, ProviderSupport.DescribeDeviceLimit(refused.Failure), head.StatusCode, trace) with
                {
                    DeviceLimit = refused.State,
                };
            }
        }
        catch (FetchException ex) when (ex.Kind is FetchFailureKind.Timeout or FetchFailureKind.Network or FetchFailureKind.Blocked or FetchFailureKind.TooLarge)
        {
            trace.Add($"HEAD base failed: {ex.Kind}");
            if (ex.Kind is FetchFailureKind.Blocked)
            {
                return SubscriptionFetchResult.Fail(ex.Kind, ex.Message, ex.StatusCode, trace);
            }
        }

        if (head is not null)
        {
            if (head.StatusCode is 401 or 403 or 404 or 410 or 429)
            {
                return SubscriptionFetchResult.Fail(ProviderSupport.KindForStatus(head.StatusCode), ProviderSupport.DescribeStatus(head.StatusCode), head.StatusCode, trace) with
                {
                    RetryAfter = head.RetryAfter,
                };
            }

            if (head.StatusCode is >= 200 and < 300)
            {
                metadata = SubscriptionMetadataParser.FromHeaders(head.Headers, now);
                foreach (var (k, v) in ProviderSupport.CaptureProviderHeaders(head.Headers, request.Ownership))
                {
                    providerHeaders[k] = v;
                }
            }
        }

        // 2) /info (best effort).
        try
        {
            var info = await fetcher.SendAsync(ProviderSupport.Get(pg!.For(PasarGuardEndpoints.Info), request.Settings, headers: device) with { MaxBytes = 256 * 1024 }, ct).ConfigureAwait(false);
            trace.Add($"GET info -> {info.StatusCode}");
            if (info.StatusCode is >= 200 and < 300 && info.Body is not null)
            {
                metadata = SubscriptionMetadataParser.Merge(metadata, SubscriptionMetadataParser.FromPasarGuardInfo(info.Body, now));
            }
        }
        catch (FetchException ex) when (ex.Kind is not FetchFailureKind.Cancelled)
        {
            trace.Add($"GET info failed: {ex.Kind}");
        }

        // 3) Content endpoints.
        SubscriptionFetchResult? lastFailure = null;
        foreach (var endpoint in PasarGuardEndpoints.ContentOrder(request.PreferredEndpoint, pg!.Endpoint))
        {
            ct.ThrowIfCancellationRequested();
            var conditional = endpoint == request.PreferredEndpoint;
            FetchResponse response;
            try
            {
                response = await fetcher.SendAsync(
                    ProviderSupport.Get(pg.For(endpoint), request.Settings, conditional ? request.ETag : null, conditional ? request.LastModified : null, device), ct).ConfigureAwait(false);
            }
            catch (FetchException ex)
            {
                trace.Add($"GET {endpoint} failed: {ex.Kind}");
                lastFailure = SubscriptionFetchResult.Fail(ex.Kind, ex.Message, ex.StatusCode, trace, metadata);
                if (ex.Kind is FetchFailureKind.Blocked or FetchFailureKind.Timeout)
                {
                    break;
                }

                continue;
            }

            trace.Add($"GET {endpoint} -> {response.StatusCode} ({response.BodyBytes} bytes)");
            if (ProviderSupport.ReadDeviceLimit(response, sentDeviceId) is { } signal)
            {
                if (signal.Failure != FetchFailureKind.None)
                {
                    trace.Add($"device limit: {signal.State}");
                    return SubscriptionFetchResult.Fail(signal.Failure, ProviderSupport.DescribeDeviceLimit(signal.Failure), response.StatusCode, trace, metadata) with
                    {
                        DeviceLimit = signal.State,
                    };
                }

                deviceLimit = signal;
            }
            foreach (var (k, v) in ProviderSupport.CaptureProviderHeaders(response.Headers, request.Ownership))
            {
                providerHeaders.TryAdd(k, v);
            }

            if (metadata.RetrievedUtc is null)
            {
                metadata = SubscriptionMetadataParser.Merge(metadata, SubscriptionMetadataParser.FromHeaders(response.Headers, now));
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
                    ResolvedEndpoint = endpoint,
                    ProviderHeaders = providerHeaders,
                    WasHttps = response.FinalUrl.Scheme == Uri.UriSchemeHttps,
                    Trace = trace,
                    HttpStatus = 304,
                    DeviceLimit = deviceLimit?.State,
                };
            }

            if (response.StatusCode is 401 or 403 or 429)
            {
                return SubscriptionFetchResult.Fail(ProviderSupport.KindForStatus(response.StatusCode), ProviderSupport.DescribeStatus(response.StatusCode), response.StatusCode, trace, metadata) with
                {
                    RetryAfter = response.RetryAfter,
                };
            }

            if (response.StatusCode is < 200 or >= 300)
            {
                lastFailure = SubscriptionFetchResult.Fail(ProviderSupport.KindForStatus(response.StatusCode), ProviderSupport.DescribeStatus(response.StatusCode), response.StatusCode, trace, metadata);
                continue;
            }

            var ctx = request.ParseContext with { ProviderId = Kind.ToString() };
            var (parsed, failure, error) = ProviderSupport.ParseBody(parser, response.Body, ctx);
            if (failure != FetchFailureKind.None)
            {
                trace.Add($"{endpoint}: {failure}");
                lastFailure = SubscriptionFetchResult.Fail(failure, error!, response.StatusCode, trace, metadata) with { Parsed = parsed };
                continue;
            }

            var hash = ProviderSupport.Hash(response.Body!);
            return new SubscriptionFetchResult
            {
                Outcome = hash == request.PreviousContentHash && endpoint == request.PreferredEndpoint ? FetchOutcome.NotModified : FetchOutcome.Updated,
                Metadata = metadata,
                Parsed = parsed,
                ETag = response.Header("ETag"),
                LastModified = response.Header("Last-Modified"),
                ContentHash = hash,
                ResolvedEndpoint = endpoint,
                Format = parsed!.Format,
                ProviderHeaders = providerHeaders,
                WasHttps = response.FinalUrl.Scheme == Uri.UriSchemeHttps,
                Trace = trace,
                HttpStatus = response.StatusCode,
                DeviceLimit = deviceLimit?.State,
            };
        }

        return lastFailure ?? SubscriptionFetchResult.Fail(FetchFailureKind.ParseFailed, "No PasarGuard endpoint returned usable configs.", null, trace, metadata);
    }
}
