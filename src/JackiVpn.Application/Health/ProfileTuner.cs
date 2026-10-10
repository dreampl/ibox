using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Health;

/// <summary>Chosen TLS fragmentation variant for one server (measured by <see cref="ConnectionTuner"/>).</summary>
public enum FragmentVariant
{
    /// <summary>Use the server's configuration as provided.</summary>
    AsConfigured = 0,

    /// <summary>TLS record fragmentation only: no per-segment waiting during the handshake.</summary>
    RecordOnly,
}

/// <summary>
/// Safe, deterministic adjustments applied to a profile right before it is used (connect and probes):
/// <list type="bullet">
/// <item>A uTLS fingerprint for TLS servers that specify none — Go's built-in ClientHello is trivially
/// identifiable, while a mainstream browser hello blends in. REALITY is untouched (always uTLS).</item>
/// <item>WebSocket / HTTPUpgrade cannot run over HTTP/2: if the config offers <c>h2</c> in ALPN a CDN may pick
/// it and every handshake fails, so <c>h2</c> is removed for those transports.</item>
/// <item>A measured fragmentation variant (record-level instead of TCP-level) when it proved at least as
/// reliable and not slower.</item>
/// </list>
/// The stored profile is never modified; the identity fingerprint is kept so caches stay keyed correctly.
/// </summary>
public static class ProfileTuner
{
    public static ProxyProfile ApplyStatic(ProxyProfile profile, PerformanceSettings performance)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(performance);
        var tls = profile.Tls;
        if (!tls.Enabled || tls.IsReality)
        {
            return profile;
        }

        var changed = tls;
        if (string.IsNullOrEmpty(tls.Fingerprint)
            && !string.IsNullOrWhiteSpace(performance.DefaultFingerprint)
            && ProfileValidator.KnownUtlsFingerprints.Contains(performance.DefaultFingerprint))
        {
            changed = changed with { Fingerprint = performance.DefaultFingerprint };
        }

        if (profile.Transport.Type is TransportType.WebSocket or TransportType.HttpUpgrade
            && tls.Alpn.Any(a => string.Equals(a, "h2", StringComparison.OrdinalIgnoreCase)))
        {
            var alpn = tls.Alpn.Where(a => !string.Equals(a, "h2", StringComparison.OrdinalIgnoreCase)).ToArray();
            changed = changed with { Alpn = alpn.Length == 0 ? ["http/1.1"] : alpn };
        }

        return ReferenceEquals(changed, tls) ? profile : profile with { Tls = changed };
    }

    public const int EarlyDataBytes = 2048;

    /// <summary>
    /// WebSocket early data (0-RTT): the first request rides in the upgrade request's Sec-WebSocket-Protocol
    /// header, saving one round trip per new connection. Only for WS configs that do not set it already.
    /// </summary>
    public static bool CanUseEarlyData(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Transport.Type == TransportType.WebSocket
            && profile.Transport.MaxEarlyData is null or 0
            && profile.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Trojan;
    }

    public static ProxyProfile ApplyEarlyData(ProxyProfile profile, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return enabled && CanUseEarlyData(profile)
            ? profile with { Transport = profile.Transport with { MaxEarlyData = EarlyDataBytes, EarlyDataHeaderName = "Sec-WebSocket-Protocol" } }
            : profile;
    }

    public static bool CanUseRecordFragment(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Tls is { Enabled: true, Fragment: { Enabled: true } } && !profile.Tls.IsReality;
    }

    public static ProxyProfile ApplyFragment(ProxyProfile profile, FragmentVariant variant)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (variant != FragmentVariant.RecordOnly || !CanUseRecordFragment(profile))
        {
            return profile;
        }

        var frag = profile.Tls.Fragment!;
        return profile with { Tls = profile.Tls with { Fragment = frag with { Enabled = false, RecordFragment = true } } };
    }
}
