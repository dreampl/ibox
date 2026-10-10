using System.Globalization;
using System.Text.Json.Nodes;
using JackiVpn.Cores.Capabilities;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Common;

namespace JackiVpn.Cores.Generation;

/// <summary>Normalized profile → sing-box outbound / endpoint JSON (only negotiated features are emitted).</summary>
public static class SingBoxOutboundBuilder
{
    public sealed record Result(JsonObject Node, bool IsEndpoint);

    public static Result Build(ProxyProfile p, string tag, CoreCapabilities caps, bool muxEnabled, ICollection<string> secrets, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(caps);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(notes);
        var sameDialect = p.Origin.SourceFormat is ConfigFormat.SingBoxFull or ConfigFormat.SingBoxOutbound;
        var dropped = new List<string>();

        if (p.Protocol == ProxyProtocol.WireGuard)
        {
            return new Result(BuildWireGuardEndpoint(p, tag, secrets), true);
        }

        var o = new JsonObject
        {
            ["type"] = p.Protocol switch
            {
                ProxyProtocol.Vless => "vless",
                ProxyProtocol.Vmess => "vmess",
                ProxyProtocol.Trojan => "trojan",
                ProxyProtocol.Shadowsocks => "shadowsocks",
                ProxyProtocol.Socks => "socks",
                ProxyProtocol.Http => "http",
                ProxyProtocol.Hysteria2 => "hysteria2",
                ProxyProtocol.Tuic => "tuic",
                _ => throw new NotSupportedException($"Protocol {p.Protocol} cannot be expressed for sing-box."),
            },
            ["tag"] = tag,
            ["server"] = p.Server,
            ["server_port"] = p.Port,
        };

        var c = p.Credentials;
        switch (p.Protocol)
        {
            case ProxyProtocol.Vless:
                o["uuid"] = Secret(c.Uuid, secrets);
                if (!string.IsNullOrEmpty(c.Flow))
                {
                    o["flow"] = c.Flow;
                }

                if (!string.IsNullOrEmpty(c.PacketEncoding))
                {
                    o["packet_encoding"] = c.PacketEncoding;
                }

                break;
            case ProxyProtocol.Vmess:
                o["uuid"] = Secret(c.Uuid, secrets);
                o["security"] = string.IsNullOrEmpty(c.Encryption) ? "auto" : c.Encryption;
                o["alter_id"] = c.AlterId ?? 0;
                if (!string.IsNullOrEmpty(c.PacketEncoding))
                {
                    o["packet_encoding"] = c.PacketEncoding;
                }

                break;
            case ProxyProtocol.Trojan:
                o["password"] = Secret(c.Password, secrets);
                break;
            case ProxyProtocol.Shadowsocks:
                o["method"] = p.Shadowsocks!.Method;
                o["password"] = Secret(c.Password, secrets);
                if (!string.IsNullOrEmpty(p.Shadowsocks.Plugin))
                {
                    o["plugin"] = p.Shadowsocks.Plugin;
                    if (!string.IsNullOrEmpty(p.Shadowsocks.PluginOptions))
                    {
                        o["plugin_opts"] = p.Shadowsocks.PluginOptions;
                    }
                }

                if (p.Shadowsocks.UdpOverTcp)
                {
                    o["udp_over_tcp"] = true;
                }

                break;
            case ProxyProtocol.Socks:
                o["version"] = "5";
                AddUserPass(o, c, secrets);
                if (p.Udp == false)
                {
                    o["network"] = "tcp";
                }

                break;
            case ProxyProtocol.Http:
                AddUserPass(o, c, secrets);
                if (!string.IsNullOrEmpty(p.Transport.Path))
                {
                    o["path"] = p.Transport.Path;
                }

                if (p.Transport.Headers.Count > 0)
                {
                    o["headers"] = Headers(p.Transport.Headers);
                }

                break;
            case ProxyProtocol.Hysteria2:
                o["password"] = Secret(c.Password, secrets);
                var hy = p.Hysteria2 ?? new Hysteria2Options();
                if (hy.ServerPorts.Count > 0 && caps.Supports(CoreFeature.PortHopping))
                {
                    o["server_ports"] = new JsonArray(hy.ServerPorts.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                    if (hy.HopInterval is { } hop)
                    {
                        o["hop_interval"] = DurationText.Format(hop);
                    }
                }

                if (hy.UpMbps is { } up)
                {
                    o["up_mbps"] = up;
                }

                if (hy.DownMbps is { } down)
                {
                    o["down_mbps"] = down;
                }

                if (!string.IsNullOrEmpty(hy.ObfsType))
                {
                    o["obfs"] = new JsonObject { ["type"] = hy.ObfsType, ["password"] = Secret(hy.ObfsPassword, secrets) };
                }

                break;
            case ProxyProtocol.Tuic:
                o["uuid"] = Secret(c.Uuid, secrets);
                if (!string.IsNullOrEmpty(c.Password))
                {
                    o["password"] = Secret(c.Password, secrets);
                }

                var tu = p.Tuic ?? new TuicOptions();
                if (!string.IsNullOrEmpty(tu.CongestionControl))
                {
                    o["congestion_control"] = tu.CongestionControl;
                }

                if (!string.IsNullOrEmpty(tu.UdpRelayMode))
                {
                    o["udp_relay_mode"] = tu.UdpRelayMode;
                }

                if (tu.ZeroRttHandshake)
                {
                    o["zero_rtt_handshake"] = true;
                }

                if (tu.Heartbeat is { } hb)
                {
                    o["heartbeat"] = DurationText.Format(hb);
                }

                break;
        }

        if (BuildTls(p, caps, sameDialect, dropped) is { } tls)
        {
            o["tls"] = tls;
        }

        if (BuildTransport(p.Transport, sameDialect, dropped) is { } transport)
        {
            o["transport"] = transport;
        }

        if (muxEnabled && CapabilityMatrix.MuxApplicable(p, caps))
        {
            var mux = p.Mux ?? new MuxOptions();
            var m = new JsonObject { ["enabled"] = true, ["protocol"] = mux.Protocol ?? "h2mux" };
            if (mux.MaxConnections is { } mc)
            {
                m["max_connections"] = mc;
            }

            if (mux.MinStreams is { } mins)
            {
                m["min_streams"] = mins;
            }

            if (mux.MaxStreams is { } maxs)
            {
                m["max_streams"] = maxs;
            }

            if (mux.Padding)
            {
                m["padding"] = true;
            }

            if (sameDialect)
            {
                ExtensionSanitizer.MergeInto(m, mux.Extensions, dropped);
            }

            o["multiplex"] = m;
        }

        if (sameDialect)
        {
            ExtensionSanitizer.MergeInto(o, p.Extensions, dropped);
        }

        if (dropped.Count > 0)
        {
            notes.Add($"Ignored unsafe source options for '{p.DisplayName}': {string.Join(", ", dropped.Distinct())}.");
        }

        return new Result(o, false);
    }

    internal const string GrpcIdleTimeout = "15s";
    internal const string GrpcPingTimeout = "10s";

    /// <summary>
    /// gRPC only works over HTTP/2: a link that advertises ALPN without "h2" (e.g. only http/1.1) would make the
    /// CDN/server answer HTTP/1.1 and every request fail, so gRPC always offers h2.
    /// </summary>
    internal static IReadOnlyList<string> EffectiveAlpn(ProxyProfile p) =>
        p.Transport.Type == TransportType.Grpc && p.Tls.Alpn.Count > 0 && !p.Tls.Alpn.Contains("h2", StringComparer.OrdinalIgnoreCase)
            ? ["h2"]
            : p.Tls.Alpn;

    public static JsonObject? BuildTls(ProxyProfile p, CoreCapabilities caps, bool sameDialect, ICollection<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(caps);
        var tls = p.Tls;
        if (!tls.Enabled)
        {
            return null;
        }

        var t = new JsonObject { ["enabled"] = true };
        if (!string.IsNullOrEmpty(tls.ServerName))
        {
            t["server_name"] = tls.ServerName;
        }

        if (tls.DisableSni)
        {
            t["disable_sni"] = true;
        }

        if (tls.AllowInsecure)
        {
            t["insecure"] = true;
        }

        if (EffectiveAlpn(p) is { Count: > 0 } alpn)
        {
            t["alpn"] = Array(alpn);
        }

        if (!string.IsNullOrEmpty(tls.MinVersion))
        {
            t["min_version"] = tls.MinVersion;
        }

        if (!string.IsNullOrEmpty(tls.MaxVersion))
        {
            t["max_version"] = tls.MaxVersion;
        }

        var fingerprint = tls.Fingerprint;
        if (tls.IsReality && string.IsNullOrEmpty(fingerprint))
        {
            fingerprint = "chrome"; // REALITY requires uTLS in sing-box.
        }

        if (!string.IsNullOrEmpty(fingerprint) && caps.Supports(CoreFeature.Utls))
        {
            t["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint };
        }

        if (tls.Reality is { Enabled: true } r)
        {
            var ro = new JsonObject { ["enabled"] = true, ["public_key"] = r.PublicKey };
            if (!string.IsNullOrEmpty(r.ShortId))
            {
                ro["short_id"] = r.ShortId;
            }

            t["reality"] = ro;
        }

        if (tls.Ech is { Enabled: true } ech && caps.Supports(CoreFeature.Ech))
        {
            var eo = new JsonObject { ["enabled"] = true };
            if (!string.IsNullOrWhiteSpace(ech.Config))
            {
                eo["config"] = Array(ech.Config.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            if (!string.IsNullOrEmpty(ech.QueryServerName) && caps.Version.Version >= new Version(1, 13, 0))
            {
                eo["query_server_name"] = ech.QueryServerName;
            }

            t["ech"] = eo;
        }

        if (tls.Fragment is { } frag && caps.Supports(CoreFeature.TlsFragment))
        {
            if (frag.Enabled)
            {
                t["fragment"] = true;
                if (frag.FallbackDelay is { } delay)
                {
                    t["fragment_fallback_delay"] = DurationText.Format(delay);
                }
            }

            if (frag.RecordFragment && caps.Supports(CoreFeature.TlsRecordFragment))
            {
                t["record_fragment"] = true;
            }
        }

        if (tls.PublicKeyPins.Count > 0 && caps.Supports(CoreFeature.PublicKeyPinning))
        {
            t["certificate_public_key_sha256"] = Array(tls.PublicKeyPins);
        }

        if (sameDialect)
        {
            ExtensionSanitizer.MergeInto(t, tls.Extensions, dropped);
        }

        return t;
    }

    public static JsonObject? BuildTransport(TransportOptions t, bool sameDialect, ICollection<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(t);
        JsonObject? o;
        switch (t.Type)
        {
            case TransportType.WebSocket:
                o = new JsonObject { ["type"] = "ws" };
                if (!string.IsNullOrEmpty(t.Path))
                {
                    o["path"] = t.Path;
                }

                var wsHeaders = new Dictionary<string, string>(t.Headers, StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(t.Host))
                {
                    wsHeaders["Host"] = t.Host;
                }

                if (wsHeaders.Count > 0)
                {
                    o["headers"] = Headers(wsHeaders);
                }

                if (t.MaxEarlyData is > 0)
                {
                    o["max_early_data"] = t.MaxEarlyData;
                    o["early_data_header_name"] = t.EarlyDataHeaderName ?? "Sec-WebSocket-Protocol";
                }

                break;
            case TransportType.HttpUpgrade:
                o = new JsonObject { ["type"] = "httpupgrade" };
                if (!string.IsNullOrEmpty(t.Host))
                {
                    o["host"] = t.Host;
                }

                if (!string.IsNullOrEmpty(t.Path))
                {
                    o["path"] = t.Path;
                }

                if (t.Headers.Count > 0)
                {
                    o["headers"] = Headers(t.Headers);
                }

                break;
            case TransportType.Grpc:
                // HTTP/2 health checks: a silently dead connection (NAT/CDN idle cut, network change) is detected
                // within ~25 s and replaced, instead of every stream on it hanging until the watchdog reconnects.
                o = new JsonObject { ["type"] = "grpc", ["idle_timeout"] = GrpcIdleTimeout, ["ping_timeout"] = GrpcPingTimeout };
                if (!string.IsNullOrEmpty(t.ServiceName))
                {
                    o["service_name"] = t.ServiceName;
                }

                break;
            case TransportType.Http2:
                o = new JsonObject { ["type"] = "http" };
                if (!string.IsNullOrEmpty(t.Host))
                {
                    o["host"] = Array(t.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }

                if (!string.IsNullOrEmpty(t.Path))
                {
                    o["path"] = t.Path;
                }

                if (!string.IsNullOrEmpty(t.Method))
                {
                    o["method"] = t.Method;
                }

                if (t.Headers.Count > 0)
                {
                    o["headers"] = Headers(t.Headers);
                }

                break;
            case TransportType.Quic:
                o = new JsonObject { ["type"] = "quic" };
                break;
            default:
                return null;
        }

        if (sameDialect)
        {
            ExtensionSanitizer.MergeInto(o, t.Extensions, dropped);
        }

        return o;
    }

    private static JsonObject BuildWireGuardEndpoint(ProxyProfile p, string tag, ICollection<string> secrets)
    {
        var wg = p.WireGuard!;
        var peer = new JsonObject
        {
            ["address"] = p.Server,
            ["port"] = p.Port,
            ["public_key"] = wg.PeerPublicKey,
            ["allowed_ips"] = Array(wg.AllowedIps),
        };
        if (!string.IsNullOrEmpty(wg.PreSharedKey))
        {
            peer["pre_shared_key"] = Secret(wg.PreSharedKey, secrets);
        }

        if (wg.Reserved.Count == 3)
        {
            peer["reserved"] = new JsonArray(wg.Reserved.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
        }

        if (wg.PersistentKeepaliveSeconds is { } ka)
        {
            peer["persistent_keepalive_interval"] = ka;
        }

        var e = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = tag,
            ["address"] = Array(wg.LocalAddresses),
            ["private_key"] = Secret(wg.PrivateKey, secrets),
            ["peers"] = new JsonArray(peer),
        };
        if (wg.Mtu is { } mtu)
        {
            e["mtu"] = mtu;
        }

        return e;
    }

    private static void AddUserPass(JsonObject o, ProxyCredentials c, ICollection<string> secrets)
    {
        if (!string.IsNullOrEmpty(c.Username))
        {
            o["username"] = c.Username;
        }

        if (!string.IsNullOrEmpty(c.Password))
        {
            o["password"] = Secret(c.Password, secrets);
        }
    }

    private static string? Secret(string? value, ICollection<string> secrets)
    {
        if (!string.IsNullOrEmpty(value))
        {
            secrets.Add(value);
        }

        return value;
    }

    internal static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject Headers(IReadOnlyDictionary<string, string> headers)
    {
        var h = new JsonObject();
        foreach (var (k, v) in headers.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            h[string.Equals(k, "host", StringComparison.OrdinalIgnoreCase) ? "Host" : k] = v;
        }

        return h;
    }

    internal static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
