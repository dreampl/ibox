using JackiVpn.Routing;
using JackiVpn.Subscriptions.Fetching;

namespace JackiVpn.Application.Routing;

/// <summary>Adapts the SSRF-safe fetcher to binary rule-set downloads.</summary>
public sealed class RuleSetDownloader(Func<IHttpFetcher> fetcherFactory) : IRuleSetDownloader
{
    public async Task<RuleSetDownload> DownloadAsync(Uri url, string? etag, string? lastModified, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        var response = await fetcherFactory().SendAsync(new FetchRequest
        {
            Url = url,
            UserAgent = Domain.ProductInfo.UserAgentProduct,
            IfNoneMatch = etag,
            IfModifiedSince = lastModified,
            MaxBytes = maxBytes,
            Timeout = timeout,
            Binary = true,
        }, ct).ConfigureAwait(false);
        return new RuleSetDownload(response.StatusCode, response.RawBody, response.Header("ETag"), response.Header("Last-Modified"));
    }
}
