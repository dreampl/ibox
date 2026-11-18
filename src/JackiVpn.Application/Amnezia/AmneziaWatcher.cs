using System.Collections.Concurrent;
using System.Net;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Amnezia;

public enum AmneziaLinkState
{
    /// <summary>No Amnezia tunnel, or IBOX itself is connected.</summary>
    None,

    /// <summary>An Amnezia tunnel is up, to a server we do not recognise (or the exit IP is unknown).</summary>
    TunnelUp,

    /// <summary>An Amnezia tunnel is up and its exit IP is one of our servers.</summary>
    OnOurServer,
}

/// <summary>A network interface as the platform reports it.</summary>
public sealed record NetAdapterInfo(string Name, string Description, bool IsUp);

/// <summary>Pure rules for "is AmneziaVPN connected, and to us?".</summary>
public static class AmneziaDetector
{
    /// <summary>AmneziaVPN names its Windows tunnel "AmneziaVPN"; the standalone AmneziaWG client uses "AmneziaWG".</summary>
    public static bool IsAmneziaAdapter(NetAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.Name.Contains("amnezia", StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains("amnezia", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AnyTunnelUp(IEnumerable<NetAdapterInfo> adapters) =>
        adapters.Any(a => a.IsUp && IsAmneziaAdapter(a));

    public static AmneziaLinkState Evaluate(bool tunnelUp, bool iboxActive, string? egressIp, IReadOnlySet<IPAddress> ourServers)
    {
        ArgumentNullException.ThrowIfNull(ourServers);
        if (iboxActive || !tunnelUp)
        {
            return AmneziaLinkState.None;
        }

        return IPAddress.TryParse(egressIp, out var ip) && ourServers.Contains(Normalize(ip))
            ? AmneziaLinkState.OnOurServer
            : AmneziaLinkState.TunnelUp;
    }

    internal static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

/// <summary>
/// Polls for an AmneziaVPN tunnel while IBOX is idle. The exit IP is asked only while such a tunnel is up, and then
/// at most once per <see cref="EgressRefresh"/>. Hosts of our servers come from the profiles plus the hosts of
/// Amnezia keys opened from IBOX.
/// </summary>
public sealed class AmneziaWatcher
{
    public static TimeSpan EgressRefresh { get; } = TimeSpan.FromMinutes(1);

    private const int MaxHosts = 128;

    private readonly Func<CancellationToken, Task<bool>> _tunnelUp;
    private readonly Func<bool> _iboxActive;
    private readonly Func<CancellationToken, Task<IEnumerable<string>>> _serverHosts;
    private readonly Func<CancellationToken, Task<string?>> _egressIp;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, byte> _keyHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _egress;
    private DateTimeOffset _egressAt;

    public AmneziaWatcher(
        Func<CancellationToken, Task<bool>> tunnelUp,
        Func<bool> iboxActive,
        Func<CancellationToken, Task<IEnumerable<string>>> serverHosts,
        Func<CancellationToken, Task<string?>> egressIp,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        TimeProvider? time = null)
    {
        _tunnelUp = tunnelUp;
        _iboxActive = iboxActive;
        _serverHosts = serverHosts;
        _egressIp = egressIp;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _time = time ?? TimeProvider.System;
    }

    public AmneziaLinkState State { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Remembers the server of a key the user opened in AmneziaVPN, so its exit IP counts as ours.</summary>
    public void Remember(AmneziaShareKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.HostName is { } host && _keyHosts.Count < MaxHosts)
        {
            _keyHosts.TryAdd(host, 0);
        }
    }

    public async Task<AmneziaLinkState> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = await ComputeAsync(ct).ConfigureAwait(false);
            if (next != State)
            {
                State = next;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks every <paramref name="interval"/> until <paramref name="ct"/> is cancelled. Errors are ignored.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, _time);
        do
        {
            try
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Detection is best effort.
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task<AmneziaLinkState> ComputeAsync(CancellationToken ct)
    {
        if (_iboxActive() || !await _tunnelUp(ct).ConfigureAwait(false))
        {
            _egress = null;
            return AmneziaLinkState.None;
        }

        var now = _time.GetUtcNow();
        if (_egress is null || now - _egressAt >= EgressRefresh)
        {
            _egress = await _egressIp(ct).ConfigureAwait(false);
            _egressAt = now;
        }

        if (_egress is null)
        {
            return AmneziaLinkState.TunnelUp;
        }

        var servers = await ResolveServersAsync(ct).ConfigureAwait(false);
        return AmneziaDetector.Evaluate(true, _iboxActive(), _egress, servers);
    }

    private async Task<IReadOnlySet<IPAddress>> ResolveServersAsync(CancellationToken ct)
    {
        var hosts = (await _serverHosts(ct).ConfigureAwait(false))
            .Concat(_keyHosts.Keys)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().Trim('[', ']'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxHosts);
        var set = new HashSet<IPAddress>();
        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                set.Add(AmneziaDetector.Normalize(literal));
                continue;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                foreach (var ip in await _resolve(host, timeout.Token).ConfigureAwait(false))
                {
                    set.Add(AmneziaDetector.Normalize(ip));
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Unresolvable hosts simply do not match.
            }
        }

        return set;
    }

    /// <summary>Exit IP from the configured trace URL, sent directly (through whatever tunnel is up). Null when off.</summary>
    public static async Task<string?> TraceEgressAsync(ProbeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.CheckPublicIp || !Uri.TryCreate(settings.PublicIpUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            using var http = ConnectionHttp.Create(ConnectionMode.Tun, null, settings.Timeout);
            return HttpLiveProbe.ParseTrace(await http.GetStringAsync(url, ct).ConfigureAwait(false)).Ip;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
