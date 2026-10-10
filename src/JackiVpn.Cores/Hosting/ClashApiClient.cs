using System.Net.Http.Headers;
using System.Text.Json;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Cores.Hosting;

public sealed record CoreTrafficTotals(long UploadTotal, long DownloadTotal, int ActiveConnections);

/// <summary>Minimal client for sing-box's loopback Clash API (traffic totals + liveness). Bearer-secret protected.</summary>
public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ClashApiClient(ClashApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.Port}/"),
            Timeout = TimeSpan.FromSeconds(3),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Secret);
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("version", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
    }

    public async Task<CoreTrafficTotals?> GetTotalsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("connections", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var up = root.TryGetProperty("uploadTotal", out var u) && u.TryGetInt64(out var uv) ? uv : 0;
        var down = root.TryGetProperty("downloadTotal", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
        var count = root.TryGetProperty("connections", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
        return new CoreTrafficTotals(up, down, count);
    }

    /// <summary>Every open connection with its process, destination, route and byte counters.</summary>
    public async Task<CoreConnections?> GetConnectionsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("connections", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        return ParseConnections(doc.RootElement);
    }

    public static CoreConnections ParseConnections(JsonElement root)
    {
        var up = root.TryGetProperty("uploadTotal", out var u) && u.TryGetInt64(out var uv) ? uv : 0;
        var down = root.TryGetProperty("downloadTotal", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
        var list = new List<CoreConnection>();
        if (root.TryGetProperty("connections", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || Str(c, "id") is not { } id)
                {
                    continue;
                }

                var m = c.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object ? meta : default;
                var chains = c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0 ? ch[0].GetString() : null;
                var type = m.ValueKind == JsonValueKind.Object ? Str(m, "type") : null;
                list.Add(new CoreConnection
                {
                    Id = id,
                    Network = (m.ValueKind == JsonValueKind.Object ? Str(m, "network") : null) ?? "tcp",
                    Host = m.ValueKind == JsonValueKind.Object ? Str(m, "host") : null,
                    DestinationIp = m.ValueKind == JsonValueKind.Object ? Str(m, "destinationIP") : null,
                    DestinationPort = m.ValueKind == JsonValueKind.Object && int.TryParse(Str(m, "destinationPort"), System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : 0,
                    ProcessPath = m.ValueKind == JsonValueKind.Object ? Str(m, "processPath") : null,
                    Upload = c.TryGetProperty("upload", out var cu) && cu.TryGetInt64(out var cuv) ? cuv : 0,
                    Download = c.TryGetProperty("download", out var cd) && cd.TryGetInt64(out var cdv) ? cdv : 0,
                    Outbound = chains ?? string.Empty,
                    Inbound = type is { } t && t.IndexOf('/', StringComparison.Ordinal) is var slash and >= 0 ? t[(slash + 1)..] : type ?? string.Empty,
                    StartUtc = c.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.String && st.TryGetDateTimeOffset(out var at) ? at : default,
                });
            }
        }

        return new CoreConnections(up, down, list);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    public void Dispose() => _http.Dispose();
}
