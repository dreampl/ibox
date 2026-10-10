using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using JackiVpn.Application.Health;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;
using JackiVpn.Networking.Probes;

namespace JackiVpn.Application.Connection;

/// <summary>Recent disconnect/crash events per node (feeds the score penalty).</summary>
public sealed class DisconnectTracker(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _events = new(StringComparer.Ordinal);

    public static TimeSpan Window { get; } = TimeSpan.FromHours(1);

    public void Record(string profileId) => _events.GetOrAdd(profileId, _ => new ConcurrentQueue<DateTimeOffset>()).Enqueue(_time.GetUtcNow());

    public int Recent(string profileId)
    {
        if (!_events.TryGetValue(profileId, out var q))
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow() - Window;
        while (q.TryPeek(out var t) && t < cutoff)
        {
            q.TryDequeue(out _);
        }

        return q.Count;
    }
}

/// <summary>Real requests through the active connection (not ICMP): health, exit location and live latency.</summary>
public interface ILiveProbe
{
    Task<HealthSample> ProbeAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct);

    Task<ExitInfo?> LocateAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct);

    /// <summary>Round trip on a kept-alive connection (handshake excluded after the first call), or null on failure.</summary>
    Task<double?> PingAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct);

    /// <summary>Drops the kept-alive connection (new session, new route).</summary>
    void ResetPing();
}

/// <summary>HTTP clients that go through the active connection: the local proxy in proxy mode, the TUN otherwise.</summary>
public static class ConnectionHttp
{
    public static HttpClient Create(ConnectionMode mode, int? localProxyPort, TimeSpan timeout, bool keepAlive = false)
    {
        var handler = new SocketsHttpHandler
        {
            // Through the local inbound whenever there is one: system-proxy mode, and Android where the app itself
            // is excluded from the VPN (so the core's sockets cannot loop) and must reach the tunnel explicitly.
            UseProxy = localProxyPort is not null,
            Proxy = localProxyPort is { } p ? LoopbackProxyCredentials.Resolve(new Uri($"http://127.0.0.1:{p}")) : null,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = keepAlive ? TimeSpan.FromMinutes(10) : TimeSpan.Zero,
            PooledConnectionIdleTimeout = keepAlive ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(1),
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }
}

public sealed class HttpLiveProbe : ILiveProbe, IDisposable
{
    private readonly Lock _gate = new();
    private HttpClient? _pingClient;
    private (ConnectionMode Mode, int? Port) _pingKey;

    public async Task<HealthSample> ProbeAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        HttpProbeResult? last = null;
        foreach (var u in settings.ProbeUrls.Take(2))
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var url))
            {
                continue;
            }

            last = localProxyPort is { } port
                ? await ProxyHttpProbe.RunAsync(new Uri($"http://127.0.0.1:{port}"), url, settings.Timeout, ct).ConfigureAwait(false)
                : await DirectHttpAsync(url, settings.Timeout, ct).ConfigureAwait(false);
            if (last.Success)
            {
                break;
            }
        }

        last ??= new HttpProbeResult(false, ProbeFailureKind.HttpFailed, null, null, null, "no probe URL configured");
        return new HealthSample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Success = last.Success,
            Failure = last.Failure,
            TtfbMs = last.TtfbMs,
            TotalMs = last.TotalMs,
            Detail = last.Detail,
        };
    }

    /// <summary>
    /// Exit IP + country from Cloudflare's trace endpoint (fast, no API key, nearest edge), then optional
    /// city / ISP from <see cref="ProbeSettings.GeoDetailsUrl"/>. Only the exit IP is revealed, through the tunnel.
    /// </summary>
    public async Task<ExitInfo?> LocateAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(settings.PublicIpUrl, UriKind.Absolute, out var traceUrl))
        {
            return null;
        }

        using var http = ConnectionHttp.Create(mode, localProxyPort, settings.Timeout);
        string? ip;
        string? cc;
        try
        {
            var text = await http.GetStringAsync(traceUrl, ct).ConfigureAwait(false);
            (ip, cc) = ParseTrace(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }

        if (ip is null)
        {
            return null;
        }

        string? city = null;
        string? isp = null;
        if (Uri.TryCreate(settings.GeoDetailsUrl, UriKind.Absolute, out var geo) && geo.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                var json = await http.GetStringAsync(new Uri(geo, Uri.EscapeDataString(ip)), ct).ConfigureAwait(false);
                (city, isp, var geoCc) = ParseGeo(json);
                cc ??= geoCc;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Details are optional.
            }
        }

        return new ExitInfo(ip, cc, CountryName(cc), city, isp, DateTimeOffset.UtcNow);
    }

    public async Task<double?> PingAsync(ConnectionMode mode, int? localProxyPort, ProbeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(settings.ProbeUrls.FirstOrDefault(), UriKind.Absolute, out var url))
        {
            return null;
        }

        HttpClient client;
        lock (_gate)
        {
            if (_pingClient is null || _pingKey != (mode, localProxyPort))
            {
                _pingClient?.Dispose();
                _pingClient = ConnectionHttp.Create(mode, localProxyPort, TimeSpan.FromSeconds(6), keepAlive: true);
                _pingKey = (mode, localProxyPort);
            }

            client = _pingClient;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 400 ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void ResetPing()
    {
        lock (_gate)
        {
            _pingClient?.Dispose();
            _pingClient = null;
        }
    }

    public static (string? Ip, string? CountryCode) ParseTrace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? ip = null;
        string? cc = null;
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("ip=", StringComparison.Ordinal) && IPAddress.TryParse(line[3..], out var addr))
            {
                ip = addr.ToString();
            }
            else if (line.StartsWith("loc=", StringComparison.Ordinal) && line.Length == 6 && char.IsAsciiLetter(line[4]) && char.IsAsciiLetter(line[5]))
            {
                cc = line[4..].ToUpperInvariant();
            }
        }

        return (ip, cc is "XX" or "T1" ? null : cc);
    }

    public static (string? City, string? Isp, string? CountryCode) ParseGeo(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False))
        {
            return (null, null, null);
        }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and < 120 } s ? s : null;

        var isp = root.TryGetProperty("connection", out var conn) ? Str(conn, "isp") ?? Str(conn, "org") : null;
        var cc = Str(root, "country_code");
        return (Str(root, "city"), isp, cc is { Length: 2 } ? cc.ToUpperInvariant() : null);
    }

    public static string? CountryName(string? code)
    {
        if (code is not { Length: 2 })
        {
            return null;
        }

        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>In TUN mode the app's own traffic is routed through the tunnel like everything else.</summary>
    private static async Task<HttpProbeResult> DirectHttpAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        using var http = ConnectionHttp.Create(ConnectionMode.Tun, null, Timeout.InfiniteTimeSpan);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var ttfb = sw.Elapsed.TotalMilliseconds;
            await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var ok = (int)response.StatusCode is >= 200 and < 400;
            return new HttpProbeResult(ok, ok ? ProbeFailureKind.None : ProbeFailureKind.HttpFailed, ttfb, sw.Elapsed.TotalMilliseconds, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpProbeResult(false, ProbeFailureKind.Timeout, null, null, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new HttpProbeResult(false, ProbeFailureKind.HttpFailed, null, null, null, ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pingClient?.Dispose();
            _pingClient = null;
        }
    }
}
