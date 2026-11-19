using System.Net;
using System.Net.NetworkInformation;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Privacy;

/// <summary>How this device reaches one of our servers without IBOX.</summary>
public enum OurServerPath
{
    /// <summary>No VPN adapter or system proxy here: the router (or another device upstream) uses our configs.</summary>
    Router = 0,

    /// <summary>A local VPN adapter or proxy is active: another app (v2rayNG, Hiddify, Amnezia…) uses our configs.</summary>
    OtherApp,
}

public sealed record OurServerFinding(string Ip, OurServerPath Via);

/// <summary>
/// While IBOX is off: does this device's public address belong to one of the user's servers? Then another app or the
/// router already uses our configs, and the dashboard says there is no need to turn IBOX on. Runs only when asked
/// (app start, network change), at most once per <see cref="MinInterval"/>, one lookup at a time; never in a loop.
/// </summary>
public sealed class OurServerDetector(
    Func<CancellationToken, Task<string?>> publicIp,
    Func<CancellationToken, Task<IReadOnlySet<string>>> ourIps,
    Func<bool> localTunnelOrProxy,
    Func<AppSettings> settings,
    ILogger logger,
    TimeProvider? time = null)
{
    public static TimeSpan MinInterval { get; } = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public OurServerFinding? Current { get; private set; }

    public event EventHandler<OurServerFinding?>? Changed;

    /// <summary>One check, unless IBOX is connected, the setting is off, or a check ran moments ago.</summary>
    public async Task<OurServerFinding?> CheckAsync(bool iboxConnected, CancellationToken ct)
    {
        var s = settings();
        if (iboxConnected || !s.Privacy.DetectOtherApps || !s.Probe.CheckPublicIp)
        {
            Set(null);
            return null;
        }

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return Current;
        }

        try
        {
            var now = _time.GetUtcNow();
            if (now - _last < MinInterval)
            {
                return Current;
            }

            _last = now;
            var ip = await publicIp(ct).ConfigureAwait(false);
            var finding = ip is not null && (await ourIps(ct).ConfigureAwait(false)).Contains(Normalize(ip))
                ? new OurServerFinding(ip, localTunnelOrProxy() ? OurServerPath.OtherApp : OurServerPath.Router)
                : null;
            Set(finding);
            if (finding is not null)
            {
                logger.LogInformation("Already going out through one of our servers ({Via}).", finding.Via);
            }

            return finding;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogDebug("Our-server check failed: {Error}", ex.Message);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>IBOX connected or the user dismissed the card.</summary>
    public void Clear() => Set(null);

    private void Set(OurServerFinding? finding)
    {
        if (finding == Current)
        {
            return;
        }

        Current = finding;
        Changed?.Invoke(this, finding);
    }

    /// <summary>
    /// Our servers' addresses: exit IPs learned by earlier sessions (cached in the insight store) plus server
    /// addresses given as IP literals. No DNS lookups: resolving every server name on start would itself tell the
    /// network which VPN servers this device knows.
    /// </summary>
    public static IReadOnlySet<string> KnownIps(InsightStore insights, IEnumerable<ProxyProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(insights);
        ArgumentNullException.ThrowIfNull(profiles);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in insights.ExitIps())
        {
            set.Add(Normalize(ip));
        }

        foreach (var p in profiles)
        {
            if (IPAddress.TryParse(p.Server.Trim('[', ']'), out _))
            {
                set.Add(Normalize(p.Server.Trim('[', ']')));
            }
        }

        return set;
    }

    /// <summary>This device's public address on the default path (the system proxy too, so a proxy app is seen).</summary>
    public static async Task<string?> LookupPublicIpAsync(ProbeSettings probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!Uri.TryCreate(probe.PublicIpUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            return HttpLiveProbe.ParseTrace(await http.GetStringAsync(url, ct).ConfigureAwait(false)).Ip;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A VPN adapter of another app is up (tun/tap/wg/ppp, not ours). Platforms add their own proxy check on top
    /// (Windows: the WinINET system proxy).
    /// </summary>
    public static bool ForeignTunnelUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && IsForeignTunnel(n.Name, n.Description, n.NetworkInterfaceType));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    public static bool IsForeignTunnel(string name, string? description, NetworkInterfaceType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Equals(Domain.ProductInfo.TunInterfaceName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Domain.ProductInfo.PreviousTunInterfaceName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Domain.ProductInfo.LegacyTunInterfaceName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (type is NetworkInterfaceType.Ppp)
        {
            return true;
        }

        // Windows lists its own Teredo/6to4/IP-HTTPS pseudo adapters as "Tunnel": only real VPN hints count.
        var text = (name + " " + description).ToLowerInvariant();
        return TunnelHints.Any(h => text.Contains(h, StringComparison.Ordinal))
            || name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) || name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("utun", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tap", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] TunnelHints = ["wintun", "wireguard", "tap-windows", "openvpn", "sing-box", "singbox", "amnezia", "hiddify", "v2ray", "clash", "vpn"];

    private static string Normalize(string ip) => IPAddress.TryParse(ip, out var a) ? a.ToString() : ip;
}
