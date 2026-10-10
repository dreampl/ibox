using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Health;

namespace JackiVpn.Networking.Probes;

public sealed record EndpointTarget(string Host, int Port, bool UseTls, string? Sni, IReadOnlyList<string> Alpn, bool AllowInsecure);

public sealed record DirectProbeResult(bool Success, ProbeFailureKind Failure, double? DnsMs, double? TcpMs, double? TlsMs, IPAddress? Address, string? Detail);

public sealed record HttpProbeResult(bool Success, ProbeFailureKind Failure, double? TtfbMs, double? TotalMs, int? StatusCode, string? Detail);

/// <summary>
/// Direct path measurements to a node: DNS resolve, TCP connect and (optionally) a TLS handshake with
/// the node's SNI/ALPN. These measure the path to the server and are independent of the proxy protocol.
/// </summary>
public static class DirectProbe
{
    public static async Task<DirectProbeResult> RunAsync(EndpointTarget target, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        IPAddress address;
        double? dnsMs = null;
        try
        {
            if (IPAddress.TryParse(NetText.TrimBrackets(target.Host), out var literal))
            {
                address = literal;
            }
            else
            {
                var addrs = await Dns.GetHostAddressesAsync(target.Host, cts.Token).ConfigureAwait(false);
                dnsMs = sw.Elapsed.TotalMilliseconds;
                address = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault()
                    ?? throw new SocketException((int)SocketError.HostNotFound);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DirectProbeResult(false, ProbeFailureKind.Timeout, null, null, null, null, "DNS timed out");
        }
        catch (SocketException ex)
        {
            return new DirectProbeResult(false, ProbeFailureKind.DnsFailed, null, null, null, null, ex.SocketErrorCode.ToString());
        }

        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        sw.Restart();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, target.Port), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DirectProbeResult(false, ProbeFailureKind.Timeout, dnsMs, null, null, address, "TCP connect timed out");
        }
        catch (SocketException ex)
        {
            return new DirectProbeResult(false, ProbeFailureKind.TcpFailed, dnsMs, null, null, address, ex.SocketErrorCode.ToString());
        }

        var tcpMs = sw.Elapsed.TotalMilliseconds;
        if (!target.UseTls)
        {
            return new DirectProbeResult(true, ProbeFailureKind.None, dnsMs, tcpMs, null, address, null);
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrEmpty(target.Sni) ? target.Host : target.Sni,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = target.Alpn.Count > 0 ? target.Alpn.Select(a => new SslApplicationProtocol(a)).ToList() : null,
            // Never weaken validation globally: only a profile that itself opted into insecure mode skips it.
            RemoteCertificateValidationCallback = target.AllowInsecure ? static (_, _, _, _) => true : null,
        };
        sw.Restart();
        try
        {
            await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
            return new DirectProbeResult(true, ProbeFailureKind.None, dnsMs, tcpMs, sw.Elapsed.TotalMilliseconds, address, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DirectProbeResult(false, ProbeFailureKind.Timeout, dnsMs, tcpMs, null, address, "TLS handshake timed out");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return new DirectProbeResult(false, ProbeFailureKind.TlsFailed, dnsMs, tcpMs, null, address, ex.Message);
        }
    }
}

/// <summary>A real HTTP request through a local SOCKS5/HTTP proxy inbound (the user-visible "ping").</summary>
public static class ProxyHttpProbe
{
    public static async Task<HttpProbeResult> RunAsync(Uri proxy, Uri url, TimeSpan timeout, CancellationToken ct, long? downloadBytes = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(url);
        using var handler = new SocketsHttpHandler
        {
            Proxy = LoopbackProxyCredentials.Resolve(proxy),
            UseProxy = true,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = timeout,
            AutomaticDecompression = DecompressionMethods.None,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", Domain.ProductInfo.UserAgentProduct);
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var ttfb = sw.Elapsed.TotalMilliseconds;
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[32 * 1024];
            long read = 0;
            int n;
            var limit = downloadBytes ?? 1024 * 1024;
            while ((n = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read >= limit)
                {
                    break;
                }
            }

            var ok = (int)response.StatusCode is >= 200 and < 400;
            return new HttpProbeResult(ok, ok ? ProbeFailureKind.None : ProbeFailureKind.HttpFailed, ttfb, sw.Elapsed.TotalMilliseconds, (int)response.StatusCode, ok ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpProbeResult(false, ProbeFailureKind.Timeout, null, null, null, $"timed out after {timeout.TotalSeconds:0.#}s");
        }
        catch (HttpRequestException ex)
        {
            var kind = ex.InnerException is SocketException or IOException ? ProbeFailureKind.ProxyHandshakeFailed : ProbeFailureKind.HttpFailed;
            return new HttpProbeResult(false, kind, null, null, null, Security.SecretRedactor.Redact(ex.Message));
        }
    }

    /// <summary>Throughput in kbit/s from a bounded download through the proxy.</summary>
    public static async Task<double?> ThroughputKbpsAsync(Uri proxy, Uri url, long bytes, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var r = await RunAsync(proxy, url, timeout, ct, bytes).ConfigureAwait(false);
        if (!r.Success || r.TtfbMs is null)
        {
            return null;
        }

        var seconds = Math.Max(0.001, (sw.Elapsed.TotalMilliseconds - r.TtfbMs.Value) / 1000.0);
        return bytes * 8 / 1000.0 / seconds;
    }
}
