using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Cores.Capabilities;

/// <summary>Derives features from the actual binary and negotiates profiles against them.</summary>
public static class CapabilityMatrix
{
    /// <summary>Oldest sing-box whose config schema (typed DNS servers, rule actions, domain_resolver) we generate.</summary>
    public static readonly Version MinSingBox = new(1, 12, 0);

    public static readonly Version MinXray = new(1, 8, 0);

    public static CoreCapabilities ForSingBox(CoreVersionInfo v)
    {
        ArgumentNullException.ThrowIfNull(v);
        var f = CoreFeature.Vless | CoreFeature.Vmess | CoreFeature.Trojan | CoreFeature.Shadowsocks | CoreFeature.Socks | CoreFeature.Http
            | CoreFeature.WebSocket | CoreFeature.Grpc | CoreFeature.HttpUpgrade | CoreFeature.Http2Transport | CoreFeature.Mux
            | CoreFeature.ProcessRouting | CoreFeature.DnsHijack | CoreFeature.RuleSets | CoreFeature.FakeIp | CoreFeature.Tun
            | CoreFeature.VisionFlow | CoreFeature.ShadowsocksPlugins | CoreFeature.TunStrictRoute | CoreFeature.StdinConfig | CoreFeature.Ech;

        if (v.HasTag("with_utls"))
        {
            f |= CoreFeature.Utls | CoreFeature.Reality;
        }

        if (v.HasTag("with_quic"))
        {
            f |= CoreFeature.Hysteria2 | CoreFeature.Tuic | CoreFeature.QuicTransport;
        }

        if (v.HasTag("with_wireguard"))
        {
            f |= CoreFeature.WireGuard;
        }

        if (v.HasTag("with_clash_api"))
        {
            f |= CoreFeature.TrafficStatsApi;
        }

        if (v.Version >= new Version(1, 11, 0))
        {
            f |= CoreFeature.RuleActions | CoreFeature.PortHopping;
        }

        if (v.Version >= new Version(1, 12, 0))
        {
            f |= CoreFeature.TlsFragment | CoreFeature.TlsRecordFragment | CoreFeature.TypedDnsServers | CoreFeature.DomainResolver;
        }

        if (v.Version >= new Version(1, 13, 0))
        {
            f |= CoreFeature.PublicKeyPinning;
        }

        return new CoreCapabilities(CoreKind.SingBox, v, f);
    }

    public static CoreCapabilities ForXray(CoreVersionInfo v)
    {
        ArgumentNullException.ThrowIfNull(v);
        var f = CoreFeature.Vless | CoreFeature.Vmess | CoreFeature.Trojan | CoreFeature.Shadowsocks | CoreFeature.Socks | CoreFeature.Http
            | CoreFeature.WireGuard | CoreFeature.Reality | CoreFeature.Utls | CoreFeature.TlsFragment | CoreFeature.Mux
            | CoreFeature.WebSocket | CoreFeature.Grpc | CoreFeature.HttpUpgrade | CoreFeature.XHttp | CoreFeature.Kcp
            | CoreFeature.VisionFlow | CoreFeature.GeoDataFiles | CoreFeature.StdinConfig;

        // Date-based versions (24.x+) are all newer than 1.8.x.
        if (v.Version.Major >= 24)
        {
            f |= CoreFeature.Ech;
        }
        else
        {
            f |= CoreFeature.Http2Transport;
        }

        return new CoreCapabilities(CoreKind.Xray, v, f);
    }

    public static CapabilityReport Negotiate(ProxyProfile p, CoreCapabilities caps, SessionKind session, bool muxEnabled)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(caps);
        var issues = new List<CapabilityIssue>();
        var core = caps.Kind == CoreKind.SingBox ? "sing-box" : "Xray";

        void Block(CoreFeature f, string msg) => issues.Add(new CapabilityIssue(f, CapabilityImpact.Blocking, msg));
        void Degrade(CoreFeature f, string msg) => issues.Add(new CapabilityIssue(f, CapabilityImpact.Degraded, msg));
        void Info(CoreFeature f, string msg) => issues.Add(new CapabilityIssue(f, CapabilityImpact.Info, msg));

        if (caps.Kind == CoreKind.SingBox && caps.Version.Version < MinSingBox)
        {
            Block(CoreFeature.TypedDnsServers, $"sing-box {caps.Version.DisplayVersion} is too old; {MinSingBox} or newer is required.");
        }

        if (caps.Kind == CoreKind.Xray && caps.Version.Version < MinXray)
        {
            Block(CoreFeature.None, $"Xray {caps.Version.DisplayVersion} is too old; {MinXray} or newer is required.");
        }

        var protocolFeature = p.Protocol switch
        {
            ProxyProtocol.Vless => CoreFeature.Vless,
            ProxyProtocol.Vmess => CoreFeature.Vmess,
            ProxyProtocol.Trojan => CoreFeature.Trojan,
            ProxyProtocol.Shadowsocks => CoreFeature.Shadowsocks,
            ProxyProtocol.Socks => CoreFeature.Socks,
            ProxyProtocol.Http => CoreFeature.Http,
            ProxyProtocol.Hysteria2 => CoreFeature.Hysteria2,
            ProxyProtocol.Tuic => CoreFeature.Tuic,
            ProxyProtocol.WireGuard => CoreFeature.WireGuard,
            _ => CoreFeature.None,
        };
        if (protocolFeature == CoreFeature.None || !caps.Supports(protocolFeature))
        {
            Block(protocolFeature, $"{core} {caps.Version.DisplayVersion} does not support the {p.Protocol} protocol.");
        }

        var t = p.Transport;
        var transportFeature = t.Type switch
        {
            TransportType.WebSocket => CoreFeature.WebSocket,
            TransportType.Grpc => CoreFeature.Grpc,
            TransportType.HttpUpgrade => CoreFeature.HttpUpgrade,
            TransportType.Http2 => CoreFeature.Http2Transport,
            TransportType.Quic => CoreFeature.QuicTransport,
            TransportType.XHttp => CoreFeature.XHttp,
            TransportType.Kcp => CoreFeature.Kcp,
            _ => CoreFeature.None,
        };
        if (transportFeature != CoreFeature.None && !caps.Supports(transportFeature))
        {
            Block(transportFeature, $"{core} does not support the {t.Type} transport.");
        }

        if (t.Type == TransportType.Tcp && string.Equals(t.HeaderType, "http", StringComparison.OrdinalIgnoreCase) && caps.Kind == CoreKind.SingBox)
        {
            Block(CoreFeature.Http2Transport, "sing-box does not support TCP HTTP header obfuscation (headerType=http).");
        }

        if (t.Type == TransportType.Kcp && caps.Kind == CoreKind.Xray && caps.Version.Version.Major >= 26
            && (!string.IsNullOrEmpty(t.KcpSeed) || t.HeaderType is { Length: > 0 } ht && !ht.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            Block(CoreFeature.Kcp, $"Xray {caps.Version.DisplayVersion} removed mKCP header/seed (replaced by FinalMask); this legacy mKCP profile cannot be used.");
        }

        if (t.Type == TransportType.Quic && caps.Kind == CoreKind.SingBox)
        {
            Degrade(CoreFeature.QuicTransport, "sing-box's QUIC transport is not wire-compatible with Xray QUIC servers.");
        }

        if (t.Type == TransportType.Grpc && caps.Kind == CoreKind.SingBox && t.ServiceName is { } sn && (sn.StartsWith('/') || sn.Contains('|', StringComparison.Ordinal)))
        {
            Block(CoreFeature.Grpc, "This gRPC profile uses a custom path (Xray format), which sing-box cannot express.");
        }
        else if (t.Type == TransportType.Grpc && t.GrpcMultiMode && caps.Kind == CoreKind.SingBox)
        {
            Degrade(CoreFeature.Grpc, "sing-box does not support gRPC multi mode; gun mode will be used.");
        }

        if (t.Extensions is not null && p.Origin.SourceFormat is not (ConfigFormat.SingBoxFull or ConfigFormat.SingBoxOutbound) && caps.Kind == CoreKind.SingBox)
        {
            Info(CoreFeature.None, "Source-specific transport options are not applicable to sing-box and were not applied.");
        }

        var tls = p.Tls;
        if (tls.Enabled)
        {
            if (tls.IsReality && !caps.Supports(CoreFeature.Reality))
            {
                Block(CoreFeature.Reality, $"{core} build has no REALITY support.");
            }

            if (tls.Fingerprint is { Length: > 0 } && !caps.Supports(CoreFeature.Utls))
            {
                Degrade(CoreFeature.Utls, $"uTLS fingerprint '{tls.Fingerprint}' is not supported by this {core} build; the default TLS fingerprint will be used.");
            }

            if (tls.Fragment is { Enabled: true } frag)
            {
                if (!caps.Supports(CoreFeature.TlsFragment))
                {
                    Degrade(CoreFeature.TlsFragment, $"TLS fragmentation is not supported by {core} {caps.Version.DisplayVersion}.");
                }
                else if (caps.Kind == CoreKind.SingBox && (frag.Length is not null || frag.Interval is not null || (frag.Packets is not null && frag.Packets != "tlshello")))
                {
                    Degrade(CoreFeature.TlsFragment, "sing-box fragments the TLS handshake with its own timing; the configured Xray length/interval values cannot be applied.");
                }
                else if (caps.Kind == CoreKind.Xray && frag.Length is null && frag.Interval is null && frag.FallbackDelay is not null)
                {
                    Degrade(CoreFeature.TlsFragment, "sing-box style fragment (fallback delay) is approximated with Xray fragment length/interval.");
                }
            }

            if (tls.Fragment is { RecordFragment: true })
            {
                if (!caps.Supports(CoreFeature.TlsRecordFragment))
                {
                    Degrade(CoreFeature.TlsRecordFragment, $"TLS record fragmentation is not supported by {core}.");
                }
            }

            if (tls.AllowInsecure && caps.Kind == CoreKind.Xray && caps.Version.Version.Major >= 26)
            {
                Block(CoreFeature.None, $"Xray {caps.Version.DisplayVersion} removed allowInsecure; pin the server certificate (SHA-256) or use sing-box.");
            }

            if (tls.CertificateSha256Pins.Count > 0 && caps.Kind == CoreKind.SingBox)
            {
                Degrade(CoreFeature.PublicKeyPinning, "Certificate SHA-256 pins (Xray format) are not supported by sing-box; normal certificate verification applies.");
            }

            if (tls.PublicKeyPins.Count > 0 && !caps.Supports(CoreFeature.PublicKeyPinning))
            {
                Degrade(CoreFeature.PublicKeyPinning, $"Certificate public-key pinning is not supported by {core} {caps.Version.DisplayVersion}.");
            }

            if (tls.Ech is { Enabled: true } && !caps.Supports(CoreFeature.Ech))
            {
                Degrade(CoreFeature.Ech, $"ECH is not supported by {core} {caps.Version.DisplayVersion}.");
            }
        }

        if (p.Credentials.Flow is { Length: > 0 } && !caps.Supports(CoreFeature.VisionFlow))
        {
            Block(CoreFeature.VisionFlow, $"{core} does not support flow '{p.Credentials.Flow}'.");
        }

        if (p.Shadowsocks?.Plugin is { Length: > 0 } plugin)
        {
            var supported = caps.Kind == CoreKind.SingBox && plugin is "obfs-local" or "v2ray-plugin";
            if (!supported)
            {
                Block(CoreFeature.ShadowsocksPlugins, $"{core} does not support the Shadowsocks plugin '{plugin}'.");
            }
        }

        if (p.Hysteria2 is { ServerPorts.Count: > 0 } && !caps.Supports(CoreFeature.PortHopping))
        {
            Degrade(CoreFeature.PortHopping, "Port hopping is not supported by this core; only the primary port will be used.");
        }

        if (muxEnabled)
        {
            if (p.IsUdpBasedProtocol)
            {
                Info(CoreFeature.Mux, "MUX is not applicable to UDP-based protocols and stays off.");
            }
            else if (p.Credentials.Flow is { Length: > 0 })
            {
                Info(CoreFeature.Mux, "MUX is incompatible with XTLS Vision flow and stays off.");
            }
            else if (!caps.Supports(CoreFeature.Mux))
            {
                Degrade(CoreFeature.Mux, $"{core} does not support MUX.");
            }
        }

        if (session == SessionKind.Tun && !caps.Supports(CoreFeature.Tun))
        {
            Info(CoreFeature.Tun, $"{core} runs as the protocol backend behind the sing-box TUN frontend.");
        }

        return new CapabilityReport(caps.Kind, issues);
    }

    /// <summary>
    /// True when MUX can actually be applied to this profile on this core. Never on gRPC: it already multiplexes
    /// streams over one HTTP/2 connection, and a second multiplexer on top only adds head-of-line blocking.
    /// </summary>
    public static bool MuxApplicable(ProxyProfile p, CoreCapabilities caps) =>
        caps.Supports(CoreFeature.Mux) && !p.IsUdpBasedProtocol && string.IsNullOrEmpty(p.Credentials.Flow) && p.Protocol is not (ProxyProtocol.Socks or ProxyProtocol.Http)
        && p.Transport.Type != TransportType.Grpc;
}
