using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using JackiVpn.Cores;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Traffic;

/// <summary>
/// The traffic relay behind speed limits and Gaming/Streaming priority. The core sends connections of a shaping
/// class here over SOCKS5 (user name = class, password = per-session secret); the relay opens the same destination
/// through the core's back inbound and copies bytes through a token bucket shared by the class. Limits change at
/// run time, without reconnecting. A blocked class is refused at once. Loopback only; ~32 KB per open connection.
/// </summary>
public sealed class TrafficShaper : IAsyncDisposable
{
    public const int MaxConnections = 2048;
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly ShaperEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<CoreConnections?>>? _connections;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (TokenBucket Down, TokenBucket Up)> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lookupGate = new(1, 1);
    private TcpListener? _listener;
    private volatile TrafficLimits _limits = TrafficLimits.None;
    private int _limitsVersion;
    private int _open;
    private CoreConnections? _snapshot;
    private DateTimeOffset _snapshotAt;

    public TrafficShaper(ShaperEndpoint endpoint, ILogger logger, Func<CancellationToken, Task<CoreConnections?>>? connections = null)
    {
        _endpoint = endpoint;
        _logger = logger;
        _connections = connections;
    }

    public int OpenConnections => Volatile.Read(ref _open);

    /// <summary>Connections relayed since start.</summary>
    public long Relayed => Interlocked.Read(ref _relayed);

    private long _relayed;

    public TrafficLimits Limits => _limits;

    public void Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, _endpoint.Port);
        listener.Server.NoDelay = true;
        listener.Start(512);
        _listener = listener;
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void SetLimits(TrafficLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        Interlocked.Increment(ref _limitsVersion);
        lock (_gate)
        {
            foreach (var (cls, buckets) in _buckets)
            {
                var rate = limits.For(cls);
                buckets.Down.SetRate(rate.DownBytesPerSecond);
                buckets.Up.SetRate(rate.UpBytesPerSecond);
            }
        }
    }

    private (TokenBucket Down, TokenBucket Up) BucketsFor(string cls)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(cls, out var b))
            {
                var rate = _limits.For(cls);
                b = (new TokenBucket(rate.DownBytesPerSecond), new TokenBucket(rate.UpBytesPerSecond));
                _buckets[cls] = b;
            }

            return b;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Relay accept failed: {Error}", ex.SocketErrorCode);
                continue;
            }

            if (Interlocked.Increment(ref _open) > MaxConnections)
            {
                Interlocked.Decrement(ref _open);
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(Socket client, CancellationToken ct)
    {
        Socket? upstream = null;
        try
        {
            client.NoDelay = true;
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(HandshakeTimeout);
            var (cls, request) = await AcceptSocksAsync(client, handshake.Token).ConfigureAwait(false);
            if (cls is null || request is null)
            {
                return;
            }

            if (_limits.For(cls).Block)
            {
                await client.SendAsync(Reply(0x02), SocketFlags.None, handshake.Token).ConfigureAwait(false);
                return;
            }

            var owner = cls == ShapeClasses.Background ? await OwnerAsync(request, handshake.Token).ConfigureAwait(false) : null;
            upstream = await ConnectBackAsync(request, handshake.Token).ConfigureAwait(false);
            if (upstream is null)
            {
                await client.SendAsync(Reply(0x05), SocketFlags.None, handshake.Token).ConfigureAwait(false);
                return;
            }

            await client.SendAsync(Reply(0x00), SocketFlags.None, handshake.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _relayed);
            var (down, up) = BucketsFor(cls);
            var exempt = new Exemption(this, cls, owner);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var a = PumpAsync(client, upstream, up, exempt, session.Token);
            var b = PumpAsync(upstream, client, down, exempt, session.Token);
            await Task.WhenAny(a, b).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(a, b).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Normal end of a connection (reset, timeout, shutdown).
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Relay connection failed: {Error}", ex.Message);
        }
        finally
        {
            upstream?.Dispose();
            client.Dispose();
            Interlocked.Decrement(ref _open);
        }
    }

    /// <summary>Whether a background connection belongs to the app that has priority right now (evaluated per limits change).</summary>
    private sealed class Exemption(TrafficShaper owner, string cls, string? processPath)
    {
        private int _version = -1;
        private bool _exempt;

        public bool Exempt
        {
            get
            {
                if (cls != ShapeClasses.Background || processPath is null)
                {
                    return false;
                }

                var v = Volatile.Read(ref owner._limitsVersion);
                if (v != _version)
                {
                    _exempt = IsPriority(owner._limits, processPath);
                    _version = v;
                }

                return _exempt;
            }
        }
    }

    public static bool IsPriority(TrafficLimits limits, string processPath)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var name = TrafficCatalog.AppName(processPath);
        return limits.PriorityProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)
            || limits.PriorityFolders.Any(f => f.Length > 3 && processPath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task PumpAsync(Socket from, Socket to, TokenBucket bucket, Exemption exemption, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var limited = bucket.Rate > 0 && !exemption.Exempt;
                // Small reads while limited keep the shaping smooth (no long bursts and pauses).
                var max = limited ? (int)Math.Clamp(bucket.Rate / 20, 1460, BufferSize) : BufferSize;
                var n = await from.ReceiveAsync(buffer.AsMemory(0, max), SocketFlags.None, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    return;
                }

                if (limited)
                {
                    await bucket.ConsumeAsync(n, ct).ConfigureAwait(false);
                }

                await to.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            try
            {
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception inner) when (inner is SocketException or ObjectDisposedException)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ------------------------------------------------------------------ SOCKS5

    /// <summary>Client side: user/password auth (password must match), CONNECT only. Returns (class, raw request address).</summary>
    private async Task<(string? Class, byte[]? Request)> AcceptSocksAsync(Socket s, CancellationToken ct)
    {
        var head = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
        if (head[0] != 5)
        {
            return (null, null);
        }

        var methods = await ReadExactAsync(s, head[1], ct).ConfigureAwait(false);
        if (!methods.Contains((byte)0x02))
        {
            await s.SendAsync(new byte[] { 5, 0xFF }, SocketFlags.None, ct).ConfigureAwait(false);
            return (null, null);
        }

        await s.SendAsync(new byte[] { 5, 2 }, SocketFlags.None, ct).ConfigureAwait(false);
        var ver = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
        var user = Encoding.ASCII.GetString(await ReadExactAsync(s, ver[1], ct).ConfigureAwait(false));
        var plen = await ReadExactAsync(s, 1, ct).ConfigureAwait(false);
        var pass = await ReadExactAsync(s, plen[0], ct).ConfigureAwait(false);
        var ok = CryptographicOperations.FixedTimeEquals(pass, Encoding.ASCII.GetBytes(_endpoint.Password));
        await s.SendAsync(new byte[] { 1, (byte)(ok ? 0 : 1) }, SocketFlags.None, ct).ConfigureAwait(false);
        if (!ok)
        {
            return (null, null);
        }

        var req = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        if (req[0] != 5 || req[1] != 1)
        {
            await s.SendAsync(Reply(0x07), SocketFlags.None, ct).ConfigureAwait(false);
            return (null, null);
        }

        var address = await ReadAddressAsync(s, req[3], ct).ConfigureAwait(false);
        return address is null ? (null, null) : (user, address);
    }

    /// <summary>Upstream: the same destination through the core's back inbound.</summary>
    private async Task<Socket?> ConnectBackAsync(byte[] address, CancellationToken ct)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _endpoint.BackPort), ct).ConfigureAwait(false);
            await s.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None, ct).ConfigureAwait(false);
            var m = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
            if (m[1] != 2)
            {
                s.Dispose();
                return null;
            }

            var u = Encoding.ASCII.GetBytes(_endpoint.BackAuth.Username);
            var p = Encoding.ASCII.GetBytes(_endpoint.BackAuth.Password);
            var auth = new byte[3 + u.Length + p.Length];
            auth[0] = 1;
            auth[1] = (byte)u.Length;
            u.CopyTo(auth, 2);
            auth[2 + u.Length] = (byte)p.Length;
            p.CopyTo(auth, 3 + u.Length);
            await s.SendAsync(auth, SocketFlags.None, ct).ConfigureAwait(false);
            var a = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
            if (a[1] != 0)
            {
                s.Dispose();
                return null;
            }

            var req = new byte[3 + address.Length];
            req[0] = 5;
            req[1] = 1;
            address.CopyTo(req, 3);
            await s.SendAsync(req, SocketFlags.None, ct).ConfigureAwait(false);
            var rep = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
            if (rep[1] != 0 || await ReadAddressAsync(s, rep[3], ct).ConfigureAwait(false) is null)
            {
                s.Dispose();
                return null;
            }

            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>ATYP + address + port as raw bytes (re-sent unchanged upstream).</summary>
    private static async Task<byte[]?> ReadAddressAsync(Socket s, byte atyp, CancellationToken ct)
    {
        byte[] body;
        switch (atyp)
        {
            case 1:
                body = await ReadExactAsync(s, 6, ct).ConfigureAwait(false);
                break;
            case 4:
                body = await ReadExactAsync(s, 18, ct).ConfigureAwait(false);
                break;
            case 3:
                var len = await ReadExactAsync(s, 1, ct).ConfigureAwait(false);
                var rest = await ReadExactAsync(s, len[0] + 2, ct).ConfigureAwait(false);
                body = new byte[1 + rest.Length];
                body[0] = len[0];
                rest.CopyTo(body, 1);
                break;
            default:
                return null;
        }

        var result = new byte[1 + body.Length];
        result[0] = atyp;
        body.CopyTo(result, 1);
        return result;
    }

    /// <summary>Host (domain or IP text) and port of a raw SOCKS address.</summary>
    public static (string Host, int Port) Describe(byte[] address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var port = (address[^2] << 8) | address[^1];
        return address[0] switch
        {
            1 => (new IPAddress(address.AsSpan(1, 4)).ToString(), port),
            4 => (new IPAddress(address.AsSpan(1, 16)).ToString(), port),
            _ => (Encoding.ASCII.GetString(address, 2, address[1]), port),
        };
    }

    private static byte[] Reply(byte code) => [5, code, 0, 1, 0, 0, 0, 0, 0, 0];

    private static async Task<byte[]> ReadExactAsync(Socket s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("Connection closed during the SOCKS handshake.");
            }

            read += n;
        }

        return buffer;
    }

    // ------------------------------------------------------------------ owner lookup

    /// <summary>
    /// Which program opened this background connection: the core already tracks it (routed to the relay, same
    /// destination) when it dials us. Needed so the running game and its helpers are never slowed, even when they
    /// are not on any list.
    /// </summary>
    private async Task<string?> OwnerAsync(byte[] address, CancellationToken ct)
    {
        if (_connections is null)
        {
            return null;
        }

        var (host, port) = Describe(address);
        var tag = Cores.Generation.SingBoxConfigBuilder.ShapeTag(ShapeClasses.Background);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await SnapshotAsync(fresh: attempt > 0, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                return null;
            }

            lock (_claimed)
            {
                var match = snapshot.Connections
                    .Where(c => c.Outbound == tag && c.DestinationPort == port && !_claimed.Contains(c.Id)
                        && (string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) || string.Equals(c.DestinationIp, host, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(c => c.StartUtc)
                    .FirstOrDefault();
                if (match is not null)
                {
                    _claimed.Add(match.Id);
                    if (_claimed.Count > 4096)
                    {
                        _claimed.IntersectWith(snapshot.Connections.Select(c => c.Id));
                    }

                    return match.ProcessPath;
                }
            }
        }

        return null;
    }

    private async Task<CoreConnections?> SnapshotAsync(bool fresh, CancellationToken ct)
    {
        await _lookupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var age = DateTimeOffset.UtcNow - _snapshotAt;
            if (_snapshot is null || age > TimeSpan.FromMilliseconds(fresh ? 60 : 400))
            {
                try
                {
                    _snapshot = await _connections!(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    _snapshot = null;
                }

                _snapshotAt = DateTimeOffset.UtcNow;
            }

            return _snapshot;
        }
        finally
        {
            _lookupGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _cts.Dispose();
    }
}

/// <summary>Bytes-per-second budget shared by every connection of a class (debt model: overdraw, then wait).</summary>
public sealed class TokenBucket
{
    private readonly Lock _gate = new();
    private long _rate;
    private double _tokens;
    private long _last = Environment.TickCount64;

    public TokenBucket(long bytesPerSecond) => SetRate(bytesPerSecond);

    /// <summary>0 = unlimited.</summary>
    public long Rate => Volatile.Read(ref _rate);

    public void SetRate(long bytesPerSecond)
    {
        lock (_gate)
        {
            _rate = Math.Max(0, bytesPerSecond);
            _tokens = Math.Min(_tokens, Burst);
        }
    }

    private double Burst => Math.Max(16 * 1024, _rate / 4.0);

    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct)
    {
        double waitMs;
        lock (_gate)
        {
            if (_rate <= 0)
            {
                return;
            }

            var now = Environment.TickCount64;
            _tokens = Math.Min(Burst, _tokens + ((now - _last) * _rate / 1000.0));
            _last = now;
            _tokens -= bytes;
            waitMs = _tokens >= 0 ? 0 : -_tokens * 1000.0 / _rate;
        }

        if (waitMs >= 1)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, 2000)), ct).ConfigureAwait(false);
        }
    }
}
