using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Application.Connectivity;

/// <summary>A Cloudflare edge address that answered on this network.</summary>
public sealed record CleanIp(string Address, double Ms);

/// <summary>
/// For configs behind Cloudflare (WebSocket, HTTPUpgrade, gRPC, XHTTP): when the usual edge addresses are
/// filtered, other edge addresses of the same CDN often are not. Samples addresses across Cloudflare's published
/// ranges, keeps those that complete TCP + TLS with the config's own SNI and answer HTTP for its host, fastest
/// first. Everything runs directly from the device (before or outside the tunnel), bounded in time and sockets.
/// </summary>
public sealed class CleanIpScanner
{
    /// <summary>Cloudflare's published IPv4 ranges.</summary>
    public static IReadOnlyList<string> CloudflareRanges { get; } =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    ];

    public static int[] TlsPorts { get; } = [443, 2053, 2083, 2087, 2096, 8443];

    public static bool IsCloudflare(IPAddress ip) => NetText.InAnyCidr(ip, CloudflareRanges);

    /// <summary>The config travels over a CDN-compatible HTTP transport (an edge address can carry it).</summary>
    public static bool IsCdnTransport(ProxyProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return !p.Tls.IsReality
            && p.Transport.Type is TransportType.WebSocket or TransportType.HttpUpgrade or TransportType.Grpc or TransportType.XHttp or TransportType.Http2
            && p.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Trojan;
    }

    /// <summary>The name the CDN routes by: SNI (TLS) or the HTTP Host.</summary>
    public static string? FrontName(ProxyProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var name = p.Tls.Enabled ? p.Tls.ServerName : null;
        name ??= p.Transport.Host?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(name) && !NetText.IsIpLiteral(p.Server))
        {
            name = p.Server;
        }

        return string.IsNullOrEmpty(name) || NetText.IsIpLiteral(name) ? null : name;
    }

    public int Candidates { get; init; } = 160;
    public int Concurrency { get; init; } = 32;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>Random addresses spread over the ranges (one per /24 at most), weighted by range size.</summary>
    public IReadOnlyList<IPAddress> Sample(int count, Random? random = null)
    {
        var rnd = random ?? Random.Shared;
        var ranges = CloudflareRanges.Select(c => NetText.TryParseCidr(c, out var ip, out var p) ? (Net: BinaryPrimitivesRead(ip), Size: 1u << (32 - p)) : default).ToList();
        var total = ranges.Sum(r => (double)r.Size);
        var seen = new HashSet<uint>();
        var list = new List<IPAddress>(count);
        for (var guard = 0; list.Count < count && guard < count * 20; guard++)
        {
            var pick = rnd.NextDouble() * total;
            var range = ranges[^1];
            foreach (var r in ranges)
            {
                if (pick < r.Size)
                {
                    range = r;
                    break;
                }

                pick -= r.Size;
            }

            var host = range.Net + (uint)rnd.NextInt64(range.Size);
            if ((host & 0xFF) is 0 or 255 || !seen.Add(host >> 8))
            {
                continue;
            }

            list.Add(new IPAddress([(byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host]));
        }

        return list;
    }

    private static uint BinaryPrimitivesRead(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>Finds up to <paramref name="keep"/> edge addresses that serve <paramref name="frontName"/> on this network.</summary>
    public async Task<IReadOnlyList<CleanIp>> ScanAsync(string frontName, int port, bool tls, int keep, CancellationToken ct, IEnumerable<IPAddress>? preferred = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(frontName);
        var candidates = (preferred ?? []).Concat(Sample(Candidates)).Distinct().ToList();

        // Stage 1: TCP connect, many in parallel; keep the fastest few.
        using var gate = new SemaphoreSlim(Concurrency);
        var reachable = new List<(IPAddress Ip, double Ms)>();
        var lockObj = new Lock();
        await Task.WhenAll(candidates.Select(async ip =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await ConnectMsAsync(ip, port, ct).ConfigureAwait(false) is { } ms)
                {
                    lock (lockObj)
                    {
                        reachable.Add((ip, ms));
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        // Stage 2: the real test — TLS with the config's name and an HTTP answer from the CDN for it.
        var best = reachable.OrderBy(r => r.Ms).Take(Math.Max(keep * 4, 12)).ToList();
        var verified = new List<CleanIp>();
        await Task.WhenAll(best.Select(async r =>
        {
            if (await HttpMsAsync(r.Ip, port, tls, frontName, ct).ConfigureAwait(false) is { } ms)
            {
                lock (lockObj)
                {
                    verified.Add(new CleanIp(r.Ip.ToString(), ms));
                }
            }
        })).ConfigureAwait(false);
        return verified.OrderBy(v => v.Ms).Take(keep).ToList();
    }

    private async Task<double?> ConnectMsAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var sw = Stopwatch.StartNew();
        try
        {
            await s.ConnectAsync(new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>TCP + TLS (SNI = the config's name, certificate validated) + one HTTP request; null on any failure.</summary>
    public async Task<double?> HttpMsAsync(IPAddress ip, int port, bool tls, string frontName, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout + HttpTimeout);
        using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        var sw = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = frontName,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, cts.Token).ConfigureAwait(false);
                stream = ssl;
            }

            var request = Encoding.ASCII.GetBytes($"GET /cdn-cgi/trace HTTP/1.1\r\nHost: {frontName}\r\nUser-Agent: Mozilla/5.0\r\nAccept: */*\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);
            var buffer = new byte[1024];
            var read = 0;
            while (read < 12)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            return read >= 12 && Encoding.ASCII.GetString(buffer, 0, 9).StartsWith("HTTP/1.", StringComparison.Ordinal) ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
        {
            return null;
        }
    }
}
