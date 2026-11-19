using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Cores.Hosting;

/// <summary>
/// The dnstt client of one DNS tunnel server: listens on <paramref name="Port"/> (loopback TCP) and carries every
/// connection as DNS queries to the tunnel's domain. The SOCKS5 proxy at the far end is reached through it.
/// </summary>
public sealed record DnsTunnelEndpoint(int Port, string ExecutablePath, IReadOnlyList<string> Arguments, string? ResolverAddress);

/// <summary>
/// Runs DNS tunnel servers (<see cref="ProxyProtocol.Dnstt"/>): the dnstt client starts first, and the profile is
/// handed to the cores as a plain SOCKS5 server on the client's loopback port, so routing, DNS, TUN and the kill
/// switch work exactly as for any other server.
/// </summary>
public static class DnsTunnel
{
    /// <summary>The first connection over a DNS tunnel takes seconds (several round trips through the resolver).</summary>
    public static TimeSpan ProbeTimeout { get; } = TimeSpan.FromSeconds(30);

    public static bool IsDnsTunnel(ProxyProfile profile) => profile?.Protocol == ProxyProtocol.Dnstt;

    /// <param name="resolver">One concrete resolver (from <see cref="Candidates"/>); null = the first candidate.</param>
    public static DnsTunnelEndpoint Plan(CoreBinaryCatalog catalog, ProxyProfile profile, int port, string? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(profile);
        _ = profile.Dnstt ?? throw new ArgumentException("Not a DNS tunnel profile.", nameof(profile));
        resolver ??= Candidates(profile).FirstOrDefault();
        return Plan(catalog, profile, port, DnsttResolver.TryParse(resolver)
            ?? throw new ArgumentException("The DNS tunnel has no usable resolver.", nameof(profile)));
    }

    private static DnsTunnelEndpoint Plan(CoreBinaryCatalog catalog, ProxyProfile profile, int port, DnsttResolver resolver)
    {
        var options = profile.Dnstt!;
        var mode = resolver.Kind switch
        {
            DnsttResolverKind.Doh => "-doh",
            DnsttResolverKind.Dot => "-dot",
            _ => "-udp",
        };
        string[] args =
        [
            mode, resolver.Value,
            "-pubkey", options.PublicKey.ToLowerInvariant(),
            profile.Server.Trim().TrimEnd('.'),
            "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
        ];
        return new DnsTunnelEndpoint(
            port,
            catalog.PathOf(CoreKind.Dnstt),
            args,
            IPAddress.TryParse(resolver.Host, out _) ? resolver.Host : null);
    }

    /// <summary>
    /// The concrete resolvers to try, in the profile's order: <c>auto</c> expanded, <c>system</c> replaced by this
    /// network's own DNS servers (dropped when there are none).
    /// </summary>
    public static IReadOnlyList<string> Candidates(ProxyProfile profile, Func<IReadOnlyList<string>>? systemDns = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = new List<string>();
        foreach (var r in DnsttResolver.ParseList(profile.Dnstt?.Resolver) ?? [])
        {
            if (r == DnsttResolver.SystemToken)
            {
                result.AddRange((systemDns ?? SystemResolvers)());
            }
            else
            {
                result.Add(r);
            }
        }

        return result.Where(r => DnsttResolver.TryParse(r) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The DNS servers the network handed out (the carrier's or the router's): IPv4 on adapters with a gateway, never
    /// this app's tunnel, loopback or fake-IP addresses.
    /// </summary>
    public static IReadOnlyList<string> SystemResolvers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !n.Name.Equals(Domain.ProductInfo.TunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    && !n.Name.Equals(Domain.ProductInfo.LegacyTunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    && !n.Name.Equals(Domain.ProductInfo.PreviousTunInterfaceName, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.DnsAddresses)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !IsFake(a) && !a.Equals(IPAddress.Any))
                .Select(a => a.ToString())
                .Distinct()
                .Take(3)
                .ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    private static bool IsFake(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 198 && (b[1] & 0xFE) == 18;
    }

    private static readonly ConcurrentDictionary<string, (string Resolver, DateTimeOffset At)> Winners = new(StringComparer.Ordinal);

    /// <summary>Once a resolver works, how long the ones earlier in the list may still take to answer.</summary>
    public static TimeSpan PreferenceGrace { get; } = TimeSpan.FromSeconds(4);

    /// <summary>How long a resolver that worked is reused before the candidates race again.</summary>
    public static TimeSpan WinnerLifetime { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Finds the resolver to use: every candidate gets its own client at once, and the first in the profile's order
    /// whose tunnel answers (the far end's SOCKS5 greeting comes back) wins. A single candidate is used as it is.
    /// The winner is remembered for <see cref="WinnerLifetime"/>. Null when none answered in time.
    /// </summary>
    public static async Task<string?> PickResolverAsync(CoreBinaryCatalog catalog, ProxyProfile profile, ILogger logger, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(profile);
        var candidates = Candidates(profile);
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var key = WinnerKey(profile) + string.Join(",", candidates);
        if (Winners.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < WinnerLifetime)
        {
            return known.Resolver;
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        var ports = PortAllocator.GetFreeLoopbackPorts(candidates.Count);
        var tries = candidates.Select((r, i) => TryResolverAsync(catalog, profile, r, ports[i], logger, race.Token)).ToList();
        var pending = tries.ToList();
        var graceStarted = false;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.Result)
            {
                // Earlier entries are preferred, but once one works the others get only a short grace.
                if (!graceStarted)
                {
                    graceStarted = true;
                    race.CancelAfter(PreferenceGrace);
                }

                if (tries.TakeWhile(t => t != done).All(t => t.IsCompleted))
                {
                    break;
                }
            }
        }

        var first = tries.FindIndex(t => t.IsCompletedSuccessfully && t.Result);
        var winner = first >= 0 ? candidates[first] : null;
        await race.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(tries).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (winner is not null)
        {
            Winners[key] = (winner, DateTimeOffset.UtcNow);
            logger.LogInformation("DNS tunnel {Domain}: resolver {Resolver} works ({Count} tried).", profile.Server, winner, candidates.Count);
        }

        return winner;
    }

    /// <summary>Forgets the remembered resolver (the connection through it stopped working).</summary>
    public static void ForgetResolver(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var prefix = WinnerKey(profile);
        foreach (var k in Winners.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Winners.TryRemove(k, out _);
        }
    }

    private static string WinnerKey(ProxyProfile profile) =>
        profile.Server.ToLowerInvariant() + "|" + (profile.Dnstt?.PublicKey.ToLowerInvariant() ?? string.Empty) + "|";

    private static async Task<bool> TryResolverAsync(CoreBinaryCatalog catalog, ProxyProfile profile, string resolver, int port, ILogger logger, CancellationToken ct)
    {
        CoreProcess? client = null;
        try
        {
            client = await StartAsync(catalog, Plan(catalog, profile, port, resolver), catalog.CoresDirectory, logger, ct, background: true).ConfigureAwait(false);
            return await TunnelAnswersAsync(port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or CoreStartException or CoreIntegrityException or IOException or SocketException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The client accepts connections only after its encrypted handshake with the server, and the SOCKS5 method reply
    /// comes from the far end: getting it back proves the whole path works.
    /// </summary>
    private static async Task<bool> TunnelAnswersAsync(int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await stream.WriteAsync(new byte[] { 5, 2, 0, 2 }, ct).ConfigureAwait(false);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
        return reply[0] == 5;
    }

    /// <summary>
    /// The profile as the cores see it: a SOCKS5 server on the dnstt client's port, with the tunnel's user name and
    /// password. TCP only (the tunnel carries one TCP stream per connection; SOCKS UDP cannot cross it).
    /// </summary>
    public static ProxyProfile AsLocalSocks(ProxyProfile profile, int port)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with
        {
            Protocol = ProxyProtocol.Socks,
            Server = "127.0.0.1",
            Port = port,
            Udp = false,
            Dnstt = null,
            Transport = new TransportOptions(),
            Tls = new TlsOptions(),
            Mux = null,
        };
    }

    /// <summary>Starts the client (hash-verified like every core) and waits until its port accepts connections.</summary>
    public static async Task<CoreProcess> StartAsync(
        CoreBinaryCatalog catalog, DnsTunnelEndpoint endpoint, string workDir, ILogger logger, CancellationToken ct, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(endpoint);
        using var binary = catalog.Verify(CoreKind.Dnstt);
        var spec = new CoreLaunchSpec(CoreKind.Dnstt, endpoint.ExecutablePath, endpoint.Arguments, workDir, new Dictionary<string, string>());
        var config = new GeneratedCoreConfig
        {
            Kind = CoreKind.Dnstt,
            Session = SessionKind.ChainBackend,
            Json = string.Empty,
            Report = new CapabilityReport(CoreKind.Dnstt, []),
            ListenPorts = [endpoint.Port],
        };
        var process = await CoreProcess.StartAsync(binary, spec, config, logger, ct, background).ConfigureAwait(false);
        try
        {
            await process.WaitForPortAsync(endpoint.Port, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return process;
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
