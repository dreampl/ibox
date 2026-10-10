using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using JackiVpn.Domain.Common;

namespace JackiVpn.Application.Connectivity;

/// <summary>
/// Resolves server names without the network's own resolver: DNS-over-HTTPS to resolvers addressed by IP (no
/// bootstrap lookup that could itself be poisoned), raced in parallel; plain DNS over TCP as a last resort.
/// Answers pointing at block pages, private ranges or the fake-IP range are treated as poisoned and dropped.
/// </summary>
public sealed class SecureDns
{
    /// <summary>DoH endpoints by IP; the certificates of these services carry their IP addresses.</summary>
    public static IReadOnlyList<string> DohEndpoints { get; } =
    [
        "https://1.1.1.1/dns-query", "https://1.0.0.1/dns-query", "https://8.8.8.8/dns-query", "https://8.8.4.4/dns-query",
        "https://9.9.9.9/dns-query", "https://149.112.112.112/dns-query", "https://94.140.14.14/dns-query",
    ];

    public static IReadOnlyList<string> TcpResolvers { get; } = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];

    private readonly HttpClient _http;
    private readonly Dictionary<string, (IReadOnlyList<IPAddress> Addresses, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public SecureDns(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(4) };
    }

    public static TimeSpan CacheTtl { get; } = TimeSpan.FromMinutes(10);

    /// <summary>IPv4 addresses of <paramref name="host"/>, or empty when every resolver failed.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        if (IPAddress.TryParse(NetText.TrimBrackets(host), out var literal))
        {
            return [literal];
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(host, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl)
            {
                return hit.Addresses;
            }
        }

        var query = BuildQuery(NetText.ToAsciiHost(host), 1);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(TimeSpan.FromSeconds(5));
        var tasks = DohEndpoints.Select(e => DohAsync(e, query, race.Token)).ToList();
        IReadOnlyList<IPAddress> result = [];
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            var answer = await done.ConfigureAwait(false);
            if (answer.Count > 0)
            {
                result = answer;
                break;
            }
        }

        await race.CancelAsync().ConfigureAwait(false);
        if (result.Count == 0)
        {
            foreach (var r in TcpResolvers)
            {
                result = await TcpAsync(r, query, ct).ConfigureAwait(false);
                if (result.Count > 0)
                {
                    break;
                }
            }
        }

        if (result.Count > 0)
        {
            lock (_gate)
            {
                _cache[host] = (result, DateTimeOffset.UtcNow);
            }
        }

        return result;
    }

    /// <summary>Block pages (10.10.34.x), private/reserved ranges and the fake-IP range never are a server's address.</summary>
    public static bool IsPoisoned(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        return NetText.IsPrivateOrLocal(ip) || Sessions.SessionPlanFactory.IsFakeIp(ip) || ip.Equals(IPAddress.Any);
    }

    /// <summary>True when the system resolver's answer for a name is missing or obviously poisoned.</summary>
    public static async Task<bool> LocalAnswerIsBadAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            return addresses.Length == 0 || addresses.All(IsPoisoned);
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private async Task<IReadOnlyList<IPAddress>> DohAsync(string endpoint, byte[] query, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(query);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content, Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            request.Headers.Accept.ParseAdd("application/dns-message");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length > 4096 ? [] : ParseAnswer(bytes, query);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> TcpAsync(string resolver, byte[] query, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(IPAddress.Parse(resolver), 53, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, cts.Token).ConfigureAwait(false);
            var len = new byte[2];
            await stream.ReadExactlyAsync(len, cts.Token).ConfigureAwait(false);
            var size = BinaryPrimitives.ReadUInt16BigEndian(len);
            if (size is 0 or > 4096)
            {
                return [];
            }

            var body = new byte[size];
            await stream.ReadExactlyAsync(body, cts.Token).ConfigureAwait(false);
            return ParseAnswer(body, query);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or EndOfStreamException)
        {
            return [];
        }
    }

    /// <summary>A recursive query (random id) for one name and type (1 = A, 28 = AAAA).</summary>
    public static byte[] BuildQuery(string host, ushort type)
    {
        ArgumentNullException.ThrowIfNull(host);
        var labels = host.TrimEnd('.').Split('.');
        var q = new List<byte>(32 + host.Length);
        var id = (ushort)Random.Shared.Next(1, 65535);
        q.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                throw new ArgumentException("Invalid host name.", nameof(host));
            }

            q.Add((byte)label.Length);
            q.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        q.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return [.. q];
    }

    /// <summary>A/AAAA records of a response to <paramref name="query"/> (same id), poisoned answers removed.</summary>
    public static IReadOnlyList<IPAddress> ParseAnswer(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> query)
    {
        if (msg.Length < 12 || (query.Length >= 2 && (msg[0] != query[0] || msg[1] != query[1])) || (msg[3] & 0x0F) != 0)
        {
            return [];
        }

        var qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            pos = SkipName(msg, pos);
            if (pos < 0 || pos + 4 > msg.Length)
            {
                return [];
            }

            pos += 4;
        }

        var list = new List<IPAddress>();
        for (var i = 0; i < an && pos >= 0 && pos < msg.Length; i++)
        {
            pos = SkipName(msg, pos);
            if (pos < 0 || pos + 10 > msg.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            if (pos + rdlen > msg.Length)
            {
                break;
            }

            if ((type == 1 && rdlen == 4) || (type == 28 && rdlen == 16))
            {
                var ip = new IPAddress(msg.Slice(pos, rdlen));
                if (!IsPoisoned(ip))
                {
                    list.Add(ip);
                }
            }

            pos += rdlen;
        }

        return list;
    }

    private static int SkipName(ReadOnlySpan<byte> msg, int pos)
    {
        for (var guard = 0; guard < 128 && pos < msg.Length; guard++)
        {
            var len = msg[pos];
            if (len == 0)
            {
                return pos + 1;
            }

            if ((len & 0xC0) == 0xC0)
            {
                return pos + 2;
            }

            pos += len + 1;
        }

        return -1;
    }
}
