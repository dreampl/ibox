using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using JackiVpn.Security;

namespace JackiVpn.Subscriptions.Fetching;

public enum FetchFailureKind
{
    None = 0,
    InvalidUrl,
    Blocked,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    ServerError,
    HttpError,
    Timeout,
    Network,
    TooLarge,
    Html,
    Empty,
    ParseFailed,
    Cancelled,

    /// <summary>The provider refused this device: the subscription's device limit is reached.</summary>
    DeviceLimitReached,

    /// <summary>The provider enforces a device limit and needs a device ID that was not sent.</summary>
    DeviceIdRequired,
}

public sealed class FetchException(FetchFailureKind kind, string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public FetchFailureKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
}

public sealed record FetchRequest
{
    public required Uri Url { get; init; }
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public string? IfNoneMatch { get; init; }
    public string? IfModifiedSince { get; init; }

    /// <summary>Never include text/html — PasarGuard serves its web page to browsers.</summary>
    public string Accept { get; init; } = "*/*";

    public required string UserAgent { get; init; }
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Return the raw body bytes (rule-sets, archives) instead of decoding text.</summary>
    public bool Binary { get; init; }

    /// <summary>
    /// Extra request headers (e.g. the device ID for device-limited providers). Sent only to the original host —
    /// never to a redirect target on another host.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public sealed record FetchResponse
{
    public required int StatusCode { get; init; }
    public required Uri FinalUrl { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public string? Body { get; init; }

    /// <summary>Raw body when <see cref="FetchRequest.Binary"/> was requested.</summary>
    public byte[]? RawBody { get; init; }

    public string? ContentType { get; init; }
    public bool NotModified => StatusCode == (int)HttpStatusCode.NotModified;
    public long BodyBytes { get; init; }
    public int Redirects { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

public interface IHttpFetcher
{
    Task<FetchResponse> SendAsync(FetchRequest request, CancellationToken ct);
}

/// <summary>
/// HTTP client for untrusted remote content: manual redirect handling with SSRF checks on every hop,
/// connect-time address validation (DNS rebinding), automatic decompression with a post-decompression size
/// cap (compression bombs), overall timeout, and conditional request headers.
/// </summary>
public sealed class SafeHttpFetcher : IHttpFetcher, IDisposable
{
    private readonly HttpClient _client;
    private static readonly HttpRequestOptionsKey<Uri> OriginKey = new("jackivpn.origin");
    private readonly IWebProxy? _proxy;

    /// <param name="proxy">Optional upstream proxy (e.g. the app's own local proxy while connected).</param>
    public SafeHttpFetcher(IWebProxy? proxy = null, bool useSystemProxy = false)
    {
        _proxy = proxy;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxResponseHeadersLength = 64,
            UseCookies = false,
            UseProxy = proxy is not null || useSystemProxy,
            Proxy = proxy,
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Addresses our own tunnel hands out (FakeIP while a TUN session runs). The app's own requests then resolve to
    /// them and the tunnel connects by the original name, so they are not checked as local addresses. Null = none.
    /// </summary>
    public Func<IPAddress, bool>? IsTunnelAddress { get; set; }

    public async Task<FetchResponse> SendAsync(FetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var check = UrlSafetyPolicy.ValidateInitial(request.Url.ToString(), out var origin);
        if (!check.Allowed)
        {
            throw new FetchException(FetchFailureKind.InvalidUrl, check.Reason!);
        }

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var current = origin!;
        var method = request.Method;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var msg = new HttpRequestMessage(method, current);
                msg.Options.Set(OriginKey, origin!);
                msg.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
                msg.Headers.TryAddWithoutValidation("Accept", request.Accept);
                msg.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                msg.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                msg.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                if (hop == 0 || IsSameOrigin(current, origin!))
                {
                    foreach (var (name, value) in request.Headers ?? new Dictionary<string, string>())
                    {
                        if (!Domain.Common.NetText.HasControlChars(name) && !Domain.Common.NetText.HasControlChars(value))
                        {
                            msg.Headers.TryAddWithoutValidation(name, value);
                        }
                    }

                    if (request.IfNoneMatch is { Length: > 0 } etag)
                    {
                        msg.Headers.TryAddWithoutValidation("If-None-Match", etag);
                    }

                    if (request.IfModifiedSince is { Length: > 0 } ims)
                    {
                        msg.Headers.TryAddWithoutValidation("If-Modified-Since", ims);
                    }
                }

                using var response = await _client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    var target = location is null ? null : location.IsAbsoluteUri ? location : new Uri(current, location);
                    var redirectCheck = UrlSafetyPolicy.ValidateRedirect(origin!, current, target, hop + 1);
                    if (!redirectCheck.Allowed)
                    {
                        throw new FetchException(FetchFailureKind.Blocked, redirectCheck.Reason!, status);
                    }

                    if (status == 303 && method != HttpMethod.Head)
                    {
                        method = HttpMethod.Get;
                    }

                    current = target!;
                    continue;
                }

                var headers = FlattenHeaders(response);
                string? body = null;
                byte[]? raw = null;
                long bytes = 0;
                if (method != HttpMethod.Head && status != 304)
                {
                    raw = await ReadBodyAsync(response, request.MaxBytes, linked.Token).ConfigureAwait(false);
                    bytes = raw.LongLength;
                    if (!request.Binary)
                    {
                        body = Decode(raw, response.Content.Headers.ContentType?.CharSet);
                        raw = null;
                    }
                }

                return new FetchResponse
                {
                    StatusCode = status,
                    FinalUrl = current,
                    Headers = headers,
                    Body = body,
                    RawBody = raw,
                    BodyBytes = bytes,
                    ContentType = response.Content.Headers.ContentType?.MediaType,
                    Redirects = hop,
                    RetryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null),
                };
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new FetchException(FetchFailureKind.Timeout, $"Request timed out after {request.Timeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.InnerException is FetchException fe)
        {
            throw fe;
        }
        catch (HttpRequestException ex)
        {
            throw new FetchException(FetchFailureKind.Network, "Network error: " + SecretRedactor.Redact(ex.Message), null, ex);
        }
    }

    /// <summary>Scheme, host and port all equal — only then do credentials-like headers follow a redirect.</summary>
    internal static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static string Decode(byte[] raw, string? charset)
    {
        var span = raw.AsSpan();
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            span = span[3..];
        }

        return GetEncoding(charset).GetString(span);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new FetchException(FetchFailureKind.TooLarge, $"Response is larger than the configured limit ({maxBytes:N0} bytes).", (int)response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new FetchException(FetchFailureKind.TooLarge, $"Response exceeds the configured limit ({maxBytes:N0} bytes).", (int)response.StatusCode);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return new UTF8Encoding(false, false);
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(false, false);
        }
    }

    private static Dictionary<string, string> FlattenHeaders(HttpResponseMessage response)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
        {
            d[h.Key] = string.Join(", ", h.Value);
        }

        foreach (var h in response.Content.Headers)
        {
            d[h.Key] = string.Join(", ", h.Value);
        }

        return d;
    }

    /// <summary>Resolves, validates every candidate address against the SSRF policy, then connects.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var origin = context.InitialRequestMessage.Options.TryGetValue(OriginKey, out var o) ? o : context.InitialRequestMessage.RequestUri;
        var isProxyHop = _proxy is not null && IsProxyEndpoint(endpoint);

        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            if (!isProxyHop && origin is not null && IsTunnelAddress?.Invoke(address) != true)
            {
                var check = UrlSafetyPolicy.ValidateResolvedAddress(origin, address);
                if (!check.Allowed)
                {
                    last = new FetchException(FetchFailureKind.Blocked, check.Reason!);
                    continue;
                }
            }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        if (last is FetchException fe)
        {
            throw fe;
        }

        throw new HttpRequestException($"Could not connect to {endpoint.Host}:{endpoint.Port}.", last);
    }

    private bool IsProxyEndpoint(DnsEndPoint endpoint)
    {
        var proxyUri = _proxy?.GetProxy(new Uri("https://example.invalid/"));
        return proxyUri is not null
            && string.Equals(proxyUri.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && proxyUri.Port == endpoint.Port;
    }

    public void Dispose() => _client.Dispose();
}
