using System.Net;
using JackiVpn.Application.Health;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Connectivity;

/// <summary>
/// When a server fails on the current network, looks for another way to reach it and tests every way in one probe
/// core (real request + 64 KB of data, so a path that freezes after the handshake does not count):
/// <list type="number">
/// <item>Poisoned or blocked DNS → the address from encrypted DNS (SNI/Host keep the name).</item>
/// <item>CDN configs (WebSocket/gRPC/HTTP over Cloudflare) → clean edge addresses found by scanning.</item>
/// <item>TLS / REALITY → other browser fingerprints (DPI that blocks one hello often passes another).</item>
/// </list>
/// The fastest working way is remembered for this server on this network (<see cref="EndpointFix"/>) and used by
/// every later connection and test there.
/// </summary>
public sealed class EndpointRescue
{
    public static IReadOnlyList<string> Fingerprints { get; } = ["chrome", "firefox", "safari", "edge", "ios", "android", "randomized"];

    public const int MaxVariants = 12;

    private readonly ConnectionTuner _tuner;
    private readonly InsightStore _insights;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _network;
    private readonly ILogger _logger;
    private readonly SecureDns _dns;
    private readonly CleanIpScanner _scanner;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _tried = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Network, string Name, int Port), (IReadOnlyList<CleanIp> Ips, DateTimeOffset At)> _scans = [];

    public EndpointRescue(ConnectionTuner tuner, InsightStore insights, Func<AppSettings> settings, Func<string> network, ILogger logger, SecureDns? dns = null, CleanIpScanner? scanner = null, TimeProvider? time = null)
    {
        _tuner = tuner;
        _insights = insights;
        _settings = settings;
        _network = network;
        _logger = logger;
        _dns = dns ?? new SecureDns();
        _scanner = scanner ?? new CleanIpScanner();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Retry interval for the same server on the same network.</summary>
    public static TimeSpan RetryAfter { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// False while the app's own direct connections would go through a tunnel (e.g. Windows TUN connected), where
    /// a scan would measure the tunnel instead of the network.
    /// </summary>
    public Func<bool> CanProbeDirectly { get; init; } = static () => true;

    /// <summary>Progress text for the UI ("Searching a clean address…").</summary>
    public event EventHandler<string>? Progress;

    /// <summary>Tries to find a working way to each profile; returns the ones that work now.</summary>
    public async Task<IReadOnlyList<ProxyProfile>> RescueAsync(IReadOnlyList<ProxyProfile> profiles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var rescued = new List<ProxyProfile>();
        foreach (var p in profiles)
        {
            if (await RescueAsync(p, ct).ConfigureAwait(false))
            {
                rescued.Add(p);
            }
        }

        return rescued;
    }

    public async Task<bool> RescueAsync(ProxyProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var options = _settings().Connectivity;
        if (!CanProbeDirectly() || !(options.SecureDnsRescue || options.CleanIpScan || options.FingerprintRotation) || profile.IsUdpBasedProtocol
            || profile.Protocol == ProxyProtocol.WireGuard)
        {
            return false;
        }

        var network = _network();
        var key = network + "|" + profile.Fingerprint;
        var now = _time.GetUtcNow();
        lock (_tried)
        {
            if (_tried.TryGetValue(key, out var at) && now - at < RetryAfter)
            {
                return false;
            }

            _tried[key] = now;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var variants = await BuildVariantsAsync(profile, options, network, ct).ConfigureAwait(false);
            if (variants.Count == 0)
            {
                return false;
            }

            Progress?.Invoke(this, $"Testing {variants.Count} ways to reach {profile.DisplayName}");
            var results = await _tuner.ProbeVariantsAsync(variants.Select(v => v.Profile).ToList(), ct).ConfigureAwait(false);
            var best = Enumerable.Range(0, variants.Count).Where(i => results[i] is not null).OrderBy(i => results[i]).FirstOrDefault(-1);
            if (best < 0)
            {
                _logger.LogInformation("No way around the block found for {Server} ({Count} variants tested).", profile.DisplayName, variants.Count);
                return false;
            }

            var fix = variants[best].Fix with { AtUtc = _time.GetUtcNow() };
            _insights.Update(profile.Fingerprint, i =>
            {
                var fixes = new Dictionary<string, EndpointFix>(i.Fixes ?? new Dictionary<string, EndpointFix>(), StringComparer.Ordinal) { [network] = fix };
                return i with { Fixes = fixes };
            });
            _logger.LogInformation("Found a way to {Server}: {Reason} ({Ms:0} ms).", profile.DisplayName, fix.Reason, results[best]);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<(ProxyProfile Profile, EndpointFix Fix)>> BuildVariantsAsync(ProxyProfile profile, ConnectivitySettings options, string network, CancellationToken ct)
    {
        var settings = _settings();
        var baseProfile = ProfileTuner.ApplyStatic(profile, settings.Performance);
        var variants = new List<(ProxyProfile, EndpointFix)>();
        var stamp = _time.GetUtcNow();
        var serverIsName = !NetText.IsIpLiteral(profile.Server);
        IReadOnlyList<IPAddress> secureAddresses = [];

        // 1) The network's resolver lies or fails: use the encrypted-DNS answer.
        if (options.SecureDnsRescue && serverIsName && await SecureDns.LocalAnswerIsBadAsync(profile.Server, ct).ConfigureAwait(false))
        {
            Progress?.Invoke(this, "Resolving with encrypted DNS");
            secureAddresses = await _dns.ResolveAsync(profile.Server, ct).ConfigureAwait(false);
            if (secureAddresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) is { } ip)
            {
                variants.Add((Sessions.SessionPlanFactory.PinServerAddress(baseProfile, [ip]), new EndpointFix(ip.ToString(), null, "encrypted DNS", stamp)));
            }
        }

        // 2) A CDN config whose usual edge is filtered: other edges of the same CDN.
        if (options.CleanIpScan && CleanIpScanner.IsCdnTransport(profile) && CleanIpScanner.FrontName(profile) is { } front && await BehindCloudflareAsync(profile, secureAddresses, ct).ConfigureAwait(false))
        {
            Progress?.Invoke(this, "Searching for a clean CDN address");
            var scanKey = (network, front, profile.Port);
            IReadOnlyList<CleanIp> clean;
            lock (_scans)
            {
                clean = _scans.TryGetValue(scanKey, out var cached) && stamp - cached.At < TimeSpan.FromMinutes(30) ? cached.Ips : [];
            }

            if (clean.Count == 0)
            {
                clean = await _scanner.ScanAsync(front, profile.Port, profile.Tls.Enabled, keep: 4, ct).ConfigureAwait(false);
                lock (_scans)
                {
                    _scans[scanKey] = (clean, stamp);
                }
            }

            foreach (var c in clean)
            {
                variants.Add((Sessions.SessionPlanFactory.PinServerAddress(baseProfile, [IPAddress.Parse(c.Address)]), new EndpointFix(c.Address, null, $"clean CDN address ({c.Ms:0} ms)", stamp)));
            }
        }

        // 3) Handshake fingerprinting: other browser hellos (REALITY always uses uTLS).
        if (options.FingerprintRotation && profile.Tls.Enabled && (profile.Tls.IsReality || !CleanIpScanner.IsCdnTransport(profile)))
        {
            var current = baseProfile.Tls.Fingerprint ?? "chrome";
            var address = variants.Select(v => v.Item2.Address).FirstOrDefault(a => a is not null);
            foreach (var fp in Fingerprints.Where(f => !string.Equals(f, current, StringComparison.OrdinalIgnoreCase)))
            {
                var p = baseProfile with { Tls = baseProfile.Tls with { Fingerprint = fp } };
                if (address is not null)
                {
                    p = Sessions.SessionPlanFactory.PinServerAddress(p, [IPAddress.Parse(address)]);
                }

                variants.Add((p, new EndpointFix(address, fp, $"{fp} TLS fingerprint", stamp)));
            }
        }

        return variants.Take(MaxVariants).ToList();
    }

    private static async Task<bool> BehindCloudflareAsync(ProxyProfile p, IReadOnlyList<IPAddress> secure, CancellationToken ct)
    {
        if (IPAddress.TryParse(NetText.TrimBrackets(p.Server), out var literal))
        {
            return CleanIpScanner.IsCloudflare(literal);
        }

        if (secure.Any(CleanIpScanner.IsCloudflare))
        {
            return true;
        }

        try
        {
            var local = await System.Net.Dns.GetHostAddressesAsync(p.Server, System.Net.Sockets.AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            // A name that does not resolve, or resolves to a block page, may still be a CDN name: scanning is cheap.
            return local.Length == 0 || local.Any(CleanIpScanner.IsCloudflare) || local.All(SecureDns.IsPoisoned);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }
}
