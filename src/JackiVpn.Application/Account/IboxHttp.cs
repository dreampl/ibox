using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JackiVpn.Application.Account;

/// <summary>A server answer: status, JSON body (or null), ETag.</summary>
public sealed record ApiResult(HttpStatusCode Status, JsonObject? Body, string? ETag)
{
    public bool Ok => (int)Status is >= 200 and < 300 && Status != HttpStatusCode.Accepted;

    public bool NotModified => Status == HttpStatusCode.NotModified;

    /// <summary>The server's own message (Persian / English), or a generic one.</summary>
    public string Error => Body?["error"]?.GetValue<string>() ?? $"HTTP {(int)Status}";

    public bool SignedOut => Status == HttpStatusCode.Unauthorized;
}

/// <summary>Thrown when no base address of a service answers.</summary>
public sealed class ServiceUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// JSON POSTs to one IBOX service (panel or music) with an app session (Bearer). Several base addresses may be given
/// (e.g. the US address and the Iranian relay): the first that answers is remembered.
/// </summary>
public sealed class IboxHttp
{
    private readonly HttpClient _http;
    private readonly Uri[] _bases;
    private int _good;

    public IboxHttp(HttpClient http, params Uri[] bases)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (bases is not { Length: > 0 })
        {
            throw new ArgumentException("At least one base address is needed.", nameof(bases));
        }

        foreach (var b in bases)
        {
            if (b.Scheme != Uri.UriSchemeHttps || !b.AbsolutePath.EndsWith('/'))
            {
                throw new ArgumentException("Base addresses must be https and end with '/'.", nameof(bases));
            }
        }

        _http = http;
        _bases = bases;
    }

    /// <summary>The address that answered last (for relative links such as avatars).</summary>
    public Uri Base => _bases[_good];

    public async Task<ApiResult> PostAsync(string path, object? body, string? bearer = null, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var i = 0; i < _bases.Length; i++)
        {
            var index = (_good + i) % _bases.Length;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_bases[index], path))
                {
                    Content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json"),
                };
                if (bearer is { Length: > 0 })
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }

                if (ifNoneMatch is { Length: > 0 })
                {
                    req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
                }

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is 502 or 503 or 504 && i < _bases.Length - 1)
                {
                    continue;
                }

                _good = index;
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                JsonObject? json = null;
                if (text.Length > 0)
                {
                    try
                    {
                        json = JsonNode.Parse(text) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        json = null;
                    }
                }

                return new ApiResult(resp.StatusCode, json, resp.Headers.ETag?.ToString());
            }
            catch (HttpRequestException e)
            {
                last = e;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                last = e;
            }
        }

        throw new ServiceUnreachableException("The server did not answer. Check the connection and try again.", last);
    }
}
