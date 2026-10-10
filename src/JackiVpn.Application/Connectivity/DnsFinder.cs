using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Connectivity;

/// <summary>A resolver the finder can try.</summary>
public sealed record DnsCandidate(string Name, string Address, DnsTransport Transport, bool Iranian = false)
{
    public DnsServerEndpoint ToEndpoint() => Transport == DnsTransport.Https
        ? new DnsServerEndpoint { Transport = DnsTransport.Https, Server = Address, Path = "/dns-query" }
        : new DnsServerEndpoint { Transport = DnsTransport.Udp, Server = Address };

    public string Label => $"{Name} · {(Transport == DnsTransport.Https ? "encrypted (DoH)" : "plain")} · {Address}";
}

/// <summary>How one resolver did on this network.</summary>
public sealed record DnsResult(DnsCandidate Candidate, bool Reachable, bool Clean, double? LatencyMs, string? Note)
{
    /// <summary>Lower is better: unreachable and poisoned resolvers sink, encrypted ones get a small head start.</summary>
    public double Score => !Reachable ? double.MaxValue : (LatencyMs ?? 9999) + (Clean ? 0 : 5000) - (Candidate.Transport == DnsTransport.Https ? 25 : 0);
}

/// <summary>
/// Looks for the nearest resolver that answers honestly on the current network: every candidate (international and
/// Iranian anti-sanction resolvers, plain and encrypted) is asked for a neutral name three times (speed) and for a
/// filtered one (honesty — the filter's block-page or private answers mean "poisoned"). The fastest clean one is
/// suggested, preferring encrypted DNS when it is about as fast, and can be used automatically to look up server
/// addresses (the bootstrap resolver). Queries go straight out, never through the tunnel; results are kept per network.
/// </summary>
public sealed class DnsFinder
{
    public static IReadOnlyList<DnsCandidate> Candidates { get; } =
    [
        new("Cloudflare", "1.1.1.1", DnsTransport.Https),
        new("Cloudflare", "1.1.1.1", DnsTransport.Udp),
        new("Google", "8.8.8.8", DnsTransport.Https),
        new("Google", "8.8.8.8", DnsTransport.Udp),
        new("Quad9", "9.9.9.9", DnsTransport.Https),
        new("Quad9", "9.9.9.9", DnsTransport.Udp),
        new("AdGuard", "94.140.14.14", DnsTransport.Https),
        new("AdGuard", "94.140.14.14", DnsTransport.Udp),
        new("OpenDNS", "208.67.222.222", DnsTransport.Udp),
        new("Shecan", "178.22.122.100", DnsTransport.Udp, Iranian: true),
        new("Electro", "78.157.42.100", DnsTransport.Udp, Iranian: true),
        new("403.online", "10.202.10.202", DnsTransport.Udp, Iranian: true),
        new("Radar Game", "10.202.10.10", DnsTransport.Udp, Iranian: true),
        new("Begzar", "185.55.226.26", DnsTransport.Udp, Iranian: true),
    ];

    /// <summary>Names the filter is known to block: a resolver that lies about them is "poisoned".</summary>
    private static readonly string[] Probes = ["www.youtube.com", "twitter.com"];

    private const string Neutral = "www.cloudflare.com";

    private readonly string _store;
    private readonly Func<string> _networkKey;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private Dictionary<string, Saved> _saved;

    public DnsFinder(string storePath, Func<string> networkKey, ILogger logger)
    {
        _store = storePath;
        _networkKey = networkKey;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2.5) }) { Timeout = TimeSpan.FromSeconds(3) };
        _saved = Load();
    }

    /// <summary>The last results for the current network (newest search), if any.</summary>
    public IReadOnlyList<DnsResult> Last { get; private set; } = [];

    /// <summary>The best clean resolver found for this network (kept for a day), or null.</summary>
    public DnsServerEndpoint? BestForNetwork()
    {
        lock (_gate)
        {
            return _saved.TryGetValue(_networkKey(), out var s) && DateTimeOffset.UtcNow - s.At < TimeSpan.FromHours(24) ? s.Endpoint : null;
        }
    }

    /// <summary>True when this network has no fresh result (worth a background search).</summary>
    public bool NeedsSearch() => BestForNetwork() is null;

    /// <summary>Tests every candidate in parallel (a few seconds) and remembers the winner for this network.</summary>
    public async Task<IReadOnlyList<DnsResult>> SearchAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(Candidates.Select(c => TestAsync(c, ct))).ConfigureAwait(false);

        // Something on this network answering every plain query itself (a transparent DNS proxy, another VPN): when
        // far-away resolvers all "answer" in a few milliseconds, port 53 is intercepted — their honesty is unknown.
        var instant = results.Where(r => r.Reachable && r.Candidate.Transport == DnsTransport.Udp && r.LatencyMs < 4).ToList();
        if (instant.Count >= 4)
        {
            results = results.Select(r => instant.Contains(r) ? r with { Clean = false, Note = "intercepted on this network" } : r).ToArray();
            _logger.LogInformation("Plain DNS is intercepted on this network ({Count} resolvers answered instantly); encrypted DNS preferred.", instant.Count);
        }

        var ordered = results.OrderBy(r => r.Score).ToList();
        Last = ordered;
        if (ordered.FirstOrDefault(r => r.Reachable && r.Clean) is { } best)
        {
            lock (_gate)
            {
                _saved[_networkKey()] = new Saved(best.Candidate.ToEndpoint(), best.Candidate.Label, DateTimeOffset.UtcNow);
                Save();
            }

            _logger.LogInformation("Best DNS on this network: {Dns} ({Ms:0} ms).", best.Candidate.Label, best.LatencyMs);
        }
        else
        {
            _logger.LogWarning("No clean DNS resolver answered on this network.");
        }

        return ordered;
    }

    private async Task<DnsResult> TestAsync(DnsCandidate c, CancellationToken ct)
    {
        try
        {
            var times = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var (ok, ms, _) = await QueryAsync(c, Neutral, ct).ConfigureAwait(false);
                if (ok)
                {
                    times.Add(ms);
                }
            }

            if (times.Count == 0)
            {
                return new DnsResult(c, false, false, null, "no answer");
            }

            var clean = true;
            string? note = null;
            foreach (var probe in Probes)
            {
                var (ok, _, addresses) = await QueryAsync(c, probe, ct).ConfigureAwait(false);
                if (ok && addresses.Count > 0 && addresses.All(SecureDns.IsPoisoned))
                {
                    clean = false;
                    note = $"filtered answers ({addresses[0]})";
                    break;
                }
            }

            times.Sort();
            return new DnsResult(c, true, clean, times[times.Count / 2], note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new DnsResult(c, false, false, null, ex.GetType().Name);
        }
    }

    private async Task<(bool Ok, double Ms, IReadOnlyList<IPAddress> Addresses)> QueryAsync(DnsCandidate c, string host, CancellationToken ct)
    {
        var query = SecureDns.BuildQuery(host, 1);
        var watch = Stopwatch.StartNew();
        byte[]? answer = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2.5));
        try
        {
            if (c.Transport == DnsTransport.Https)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{c.Address}/dns-query") { Content = new ByteArrayContent(query) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    answer = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            else
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(query, new IPEndPoint(IPAddress.Parse(c.Address), 53), timeout.Token).ConfigureAwait(false);
                var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                answer = received.Buffer;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or OperationCanceledException or TaskCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return (false, 0, []);
        }

        if (answer is null)
        {
            return (false, 0, []);
        }

        var addresses = SecureDns.ParseAnswer(answer, query);
        return (addresses.Count > 0, watch.Elapsed.TotalMilliseconds, addresses);
    }

    // ------------------------------------------------------------------ persistence (per network)

    private sealed record Saved(DnsServerEndpoint Endpoint, string Label, DateTimeOffset At);

    /// <summary>Label of the remembered resolver for this network (for the UI).</summary>
    public string? BestLabel()
    {
        lock (_gate)
        {
            return _saved.TryGetValue(_networkKey(), out var s) ? s.Label : null;
        }
    }

    private Dictionary<string, Saved> Load()
    {
        try
        {
            if (File.Exists(_store))
            {
                return JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(_store)) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug("DNS choices unreadable: {Error}", ex.Message);
        }

        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
            File.WriteAllText(_store, JsonSerializer.Serialize(_saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("DNS choices not saved: {Error}", ex.Message);
        }
    }
}
