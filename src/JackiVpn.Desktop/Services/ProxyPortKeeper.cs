using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Keeps IBOX's proxy port answering while IBOX is open but not connected. Some apps — Claude Desktop and Claude
/// Code among them — take the system proxy once, when a session starts, and keep using it: when IBOX disconnects (or
/// another VPN takes over) the port they hold would be dead and every request would fail until they are restarted.
/// Here connections on that port are passed on to what the system uses without IBOX: the proxy that was set before
/// (another VPN app's), or a direct connection when there was none. Only on loopback; stopped before IBOX connects.
/// </summary>
public sealed class ProxyPortKeeper : IDisposable
{
    private const int MaxHeader = 16 * 1024;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;

    public ProxyPortKeeper(ILogger logger) => _logger = logger;

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _listener is not null;
            }
        }
    }

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_listener is not null && _port == port)
            {
                return;
            }

            StopLocked();
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(64);
                _listener = listener;
                _port = port;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => AcceptAsync(listener, token), CancellationToken.None);
                _logger.LogInformation("Proxy port {Port} kept open while disconnected (apps that remember it keep working).", port);
            }
            catch (SocketException ex)
            {
                // Someone else has it (another copy, another app): nothing to keep.
                _logger.LogDebug("Proxy port {Port} not kept: {Error}", port, ex.Message);
                _listener = null;
            }
        }
    }

    /// <summary>Releases the port at once (IBOX's own core is about to take it).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var downstream = client.GetStream();
                if (Upstream(_port) is { } upstream)
                {
                    // Another proxy is the system's choice: hand it the bytes as they come (it speaks the same protocol).
                    using var up = new TcpClient { NoDelay = true };
                    await up.ConnectAsync(upstream.Host, upstream.Port, ct).ConfigureAwait(false);
                    await PipeAsync(downstream, up.GetStream(), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    return;
                }

                await DirectAsync(downstream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                // the client or the target went away
            }
        }
    }

    /// <summary>The system proxy other than IBOX's own port (WinINet), or null for direct.</summary>
    internal static (string Host, int Port)? Upstream(int ownPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0 || key.GetValue("ProxyServer") is not string server)
        {
            return null;
        }

        return ParseProxy(server, ownPort);
    }

    internal static (string Host, int Port)? ParseProxy(string server, int ownPort)
    {
        // "host:port", or "http=host:port;https=host:port;socks=host:port": the https entry, then http.
        string? pick = null;
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                pick ??= part;
            }
            else if (part[..eq].Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                pick = part[(eq + 1)..];
            }
            else if (part[..eq].Equals("http", StringComparison.OrdinalIgnoreCase) && pick is null)
            {
                pick = part[(eq + 1)..];
            }
        }

        if (pick is null)
        {
            return null;
        }

        pick = pick.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        var colon = pick.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(pick[(colon + 1)..], out var port) || port is <= 0 or > 65535)
        {
            return null;
        }

        var host = pick[..colon].Trim('[', ']');
        var own = port == ownPort && (host is "127.0.0.1" or "localhost" or "::1");
        return own ? null : (host, port);
    }

    /// <summary>No other proxy: be a small one (HTTP CONNECT, plain HTTP, SOCKS5 without a password).</summary>
    private static async Task DirectAsync(NetworkStream client, CancellationToken ct)
    {
        var first = new byte[1];
        if (await client.ReadAsync(first, ct).ConfigureAwait(false) == 0)
        {
            return;
        }

        if (first[0] == 0x05)
        {
            await SocksAsync(client, ct).ConfigureAwait(false);
            return;
        }

        // HTTP: read the request head.
        var head = new MemoryStream();
        head.WriteByte(first[0]);
        var buffer = new byte[1];
        while (!EndsWithBlankLine(head))
        {
            if (head.Length > MaxHeader || await client.ReadAsync(buffer, ct).ConfigureAwait(false) == 0)
            {
                return;
            }

            head.WriteByte(buffer[0]);
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        var requestLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        var parts = requestLine.Split(' ');
        if (parts.Length < 3)
        {
            return;
        }

        if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = HostPort(parts[1], 443);
            using var target = new TcpClient { NoDelay = true };
            await target.ConnectAsync(host, port, ct).ConfigureAwait(false);
            await client.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            await PipeAsync(client, target.GetStream(), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

        // Plain HTTP through a proxy: the request names the whole address; the server wants only the path.
        var rewritten = parts[0] + " " + uri.PathAndQuery + " " + parts[2] + text[requestLine.Length..];
        using var http = new TcpClient { NoDelay = true };
        await http.ConnectAsync(uri.Host, uri.Port, ct).ConfigureAwait(false);
        await PipeAsync(client, http.GetStream(), Encoding.ASCII.GetBytes(rewritten), ct).ConfigureAwait(false);
    }

    private static async Task SocksAsync(NetworkStream client, CancellationToken ct)
    {
        var n = new byte[1];
        await ReadExactAsync(client, n, ct).ConfigureAwait(false);
        await ReadExactAsync(client, new byte[n[0]], ct).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 0x05, 0x00 }, ct).ConfigureAwait(false);
        var req = new byte[4];
        await ReadExactAsync(client, req, ct).ConfigureAwait(false);
        if (req[1] != 0x01)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
            return;
        }

        string host;
        switch (req[3])
        {
            case 0x01:
                var v4 = new byte[4];
                await ReadExactAsync(client, v4, ct).ConfigureAwait(false);
                host = new IPAddress(v4).ToString();
                break;
            case 0x04:
                var v6 = new byte[16];
                await ReadExactAsync(client, v6, ct).ConfigureAwait(false);
                host = new IPAddress(v6).ToString();
                break;
            case 0x03:
                var len = new byte[1];
                await ReadExactAsync(client, len, ct).ConfigureAwait(false);
                var name = new byte[len[0]];
                await ReadExactAsync(client, name, ct).ConfigureAwait(false);
                host = Encoding.ASCII.GetString(name);
                break;
            default:
                return;
        }

        var portBytes = new byte[2];
        await ReadExactAsync(client, portBytes, ct).ConfigureAwait(false);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        using var target = new TcpClient { NoDelay = true };
        try
        {
            await target.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
            return;
        }

        await client.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
        await PipeAsync(client, target.GetStream(), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    private static async Task PipeAsync(Stream a, Stream b, ReadOnlyMemory<byte> firstToB, CancellationToken ct)
    {
        if (!firstToB.IsEmpty)
        {
            await b.WriteAsync(firstToB, ct).ConfigureAwait(false);
        }

        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ab = a.CopyToAsync(b, both.Token);
        var ba = b.CopyToAsync(a, both.Token);
        await Task.WhenAny(ab, ba).ConfigureAwait(false);
        await both.CancelAsync().ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new InvalidDataException("closed");
            }

            read += n;
        }
    }

    private static bool EndsWithBlankLine(MemoryStream m)
    {
        if (m.Length < 4)
        {
            return false;
        }

        var b = m.GetBuffer();
        var i = (int)m.Length;
        return b[i - 4] == '\r' && b[i - 3] == '\n' && b[i - 2] == '\r' && b[i - 1] == '\n';
    }

    private static (string Host, int Port) HostPort(string authority, int defaultPort)
    {
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && !authority.EndsWith(']') && int.TryParse(authority[(colon + 1)..], out var p))
        {
            return (authority[..colon].Trim('[', ']'), p);
        }

        return (authority.Trim('[', ']'), defaultPort);
    }
}
