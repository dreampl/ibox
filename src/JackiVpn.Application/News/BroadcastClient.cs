using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.Application.News;

/// <summary>One message as the provider's news endpoint sends it.</summary>
public sealed record BroadcastItem(string Id, long T, string? Title, string Text, string Lvl, bool Read);

/// <summary>An answer of news/list: <see cref="NotModified"/> (the ETag still holds), the items, or a failure.</summary>
public sealed record BroadcastList(bool NotModified, IReadOnlyList<BroadcastItem> Items, string? ETag, bool UnknownSubscription = false);

/// <summary>
/// The owner's news for one official subscription, through the panel's Iran entry
/// (<c>https://sub.&lt;domain&gt;:2083/sub/_jp/api/news/list|read</c>). The subscription token proves who asks; it goes
/// in the POST body, never in a URL, and is never logged. Polling is cheap: If-None-Match with the last ETag.
/// </summary>
public sealed partial class BroadcastClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Za-z0-9_=.-]{16,200}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[0-9a-f]{8}$")]
    private static partial Regex IdPattern();

    /// <summary>The news endpoint and token of an official subscription URL; null for anything else.</summary>
    public static (Uri Endpoint, string Token)? Target(string? subscriptionUrl)
    {
        if ((OfficialProviders.IranEntryUrl(subscriptionUrl) ?? (IsIranEntry(subscriptionUrl) ? subscriptionUrl : null)) is not { } entry)
        {
            return null;
        }

        var u = new Uri(entry);
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "sub" || !TokenPattern().IsMatch(parts[1]) || parts[1].StartsWith('_'))
        {
            return null;
        }

        return (new UriBuilder(u.Scheme, u.Host, u.Port, "/sub/_jp/api/news/").Uri, parts[1]);
    }

    /// <summary>Already the Iran entry (IranEntryUrl answers null for those).</summary>
    private static bool IsIranEntry(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && OfficialProviders.IsOfficialUrl(u) && u.Port == OfficialProviders.IranEntryPort
        && u.IdnHost.StartsWith("sub.", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/sub/", StringComparison.Ordinal);

    public async Task<BroadcastList> ListAsync(Uri endpoint, string token, string? etag, CancellationToken ct)
    {
        using var request = Post(endpoint, "list", new { sub = token });
        if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new BroadcastList(true, [], etag);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            // Not a subscription this panel knows, or a panel without news: ask again much later.
            return new BroadcastList(false, [], null, UnknownSubscription: true);
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ListBody>(Json, ct).ConfigureAwait(false);
        var items = (body?.Items ?? []).OfType<ItemBody>().Where(i => IdPattern().IsMatch(i.Id ?? string.Empty) && !string.IsNullOrWhiteSpace(i.Text))
            .Select(i => new BroadcastItem(i.Id!, i.T, string.IsNullOrWhiteSpace(i.Title) ? null : i.Title, i.Text!, i.Lvl ?? "info", i.Read))
            .ToList();
        return new BroadcastList(false, items, response.Headers.ETag?.ToString());
    }

    /// <summary>Marks ids read at the provider; returns how many stay unread.</summary>
    public async Task<int?> ReadAsync(Uri endpoint, string token, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        using var request = Post(endpoint, "read", new { sub = token, ids = ids.Where(i => IdPattern().IsMatch(i)).Take(100).ToArray() });
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ReadBody>(Json, ct).ConfigureAwait(false);
        return body?.Unread;
    }

    private static HttpRequestMessage Post(Uri endpoint, string action, object body) => new(HttpMethod.Post, new Uri(endpoint, action))
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
    };

    private sealed record ListBody(List<ItemBody?>? Items, int Unread);

    private sealed record ItemBody(string? Id, long T, string? Title, string? Text, string? Lvl, bool Read);

    private sealed record ReadBody(bool Ok, int? Unread);
}
