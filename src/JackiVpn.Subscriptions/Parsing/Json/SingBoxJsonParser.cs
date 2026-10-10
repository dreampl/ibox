using System.Text.Json.Nodes;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Subscriptions.Parsing.Json;

/// <summary>
/// sing-box JSON → <see cref="ProxyProfile"/>. Accepts a full config (<c>outbounds</c> + <c>endpoints</c>),
/// a single outbound object, or an array of outbounds. Unknown fields at every level are preserved in the
/// profile's extension objects so a newer sing-box option survives a round-trip.
/// </summary>
public static class SingBoxJsonParser
{
    private static readonly HashSet<string> NonProxyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "block", "dns", "selector", "urltest", "tor", "ssh", "shadowtls", "hysteria", "naive", "anytls",
    };

    private static readonly HashSet<string> CommonKnown = new(StringComparer.Ordinal)
    {
        "type", "tag", "server", "server_port", "uuid", "flow", "network", "tls", "packet_encoding", "multiplex", "transport",
        "security", "alter_id", "password", "method", "plugin", "plugin_opts", "udp_over_tcp", "version", "username",
        "server_ports", "hop_interval", "up_mbps", "down_mbps", "obfs", "congestion_control", "udp_relay_mode",
        "zero_rtt_handshake", "heartbeat", "local_address", "private_key", "peer_public_key", "pre_shared_key",
        "reserved", "mtu", "path", "headers",
    };

    private static readonly HashSet<string> TlsKnown = new(StringComparer.Ordinal)
    {
        "enabled", "disable_sni", "server_name", "insecure", "alpn", "min_version", "max_version", "utls", "reality", "ech",
        "fragment", "fragment_fallback_delay", "record_fragment", "certificate_public_key_sha256",
    };

    private static readonly HashSet<string> TransportKnown = new(StringComparer.Ordinal)
    {
        "type", "path", "headers", "host", "service_name", "max_early_data", "early_data_header_name", "method",
    };

    private static readonly HashSet<string> MuxKnown = new(StringComparer.Ordinal)
    {
        "enabled", "protocol", "max_connections", "min_streams", "max_streams", "padding",
    };

    public static void ParseDocument(JsonNode root, ParseContext ctx, ParseResult result, ConfigFormat format)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(result);
        var items = new List<(JsonObject Obj, bool IsEndpoint)>();
        switch (root)
        {
            case JsonObject o when o.Arr("outbounds") is not null || o.Arr("endpoints") is not null:
                items.AddRange((o.Arr("outbounds") ?? []).OfType<JsonObject>().Select(x => (x, false)));
                items.AddRange((o.Arr("endpoints") ?? []).OfType<JsonObject>().Select(x => (x, true)));
                break;
            case JsonObject single:
                items.Add((single, false));
                break;
            case JsonArray arr:
                items.AddRange(arr.OfType<JsonObject>().Select(x => (x, false)));
                break;
        }

        var index = 0;
        foreach (var (obj, isEndpoint) in items)
        {
            index++;
            if (result.TotalEntries >= ctx.Limits.MaxEntries)
            {
                result.Warn(null, $"Entry limit ({ctx.Limits.MaxEntries}) reached; remaining entries ignored.");
                break;
            }

            var type = obj.Str("type") ?? string.Empty;
            if (NonProxyTypes.Contains(type))
            {
                if (type is "hysteria" or "naive" or "anytls" or "ssh" or "tor" or "shadowtls")
                {
                    result.Warn(index, $"Outbound type '{type}' is not supported by {Domain.ProductInfo.DisplayName} yet; skipped.");
                }

                continue;
            }

            result.TotalEntries++;
            try
            {
                var profile = isEndpoint ? ParseEndpoint(obj) : ParseOutbound(obj);
                result.Profiles.Add(ProfileFactory.Finish(profile, ctx, format, obj.ToJsonString()));
            }
            catch (ProfileParseException ex)
            {
                result.Error(index, ex.Message, obj.ToJsonString());
            }
        }
    }

    public static ProxyProfile ParseOutbound(JsonObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var type = (o.Str("type") ?? string.Empty).ToLowerInvariant();
        var protocol = type switch
        {
            "vless" => ProxyProtocol.Vless,
            "vmess" => ProxyProtocol.Vmess,
            "trojan" => ProxyProtocol.Trojan,
            "shadowsocks" => ProxyProtocol.Shadowsocks,
            "socks" => ProxyProtocol.Socks,
            "http" => ProxyProtocol.Http,
            "hysteria2" => ProxyProtocol.Hysteria2,
            "tuic" => ProxyProtocol.Tuic,
            "wireguard" => ProxyProtocol.WireGuard,
            _ => throw new ProfileParseException($"Unsupported sing-box outbound type '{ProfileFactory.Truncate(type)}'."),
        };

        if (protocol == ProxyProtocol.WireGuard)
        {
            return ParseLegacyWireGuard(o);
        }

        var server = o.Str("server") ?? throw new ProfileParseException("Outbound has no server.");
        var port = o.Int("server_port") ?? throw new ProfileParseException("Outbound has no server_port.");

        var profile = new ProxyProfile
        {
            Id = string.Empty,
            Name = o.Str("tag") ?? string.Empty,
            Protocol = protocol,
            Server = server,
            Port = port,
            Credentials = new ProxyCredentials
            {
                Uuid = o.Str("uuid"),
                Password = o.Str("password"),
                Username = o.Str("username"),
                Flow = o.Str("flow"),
                Encryption = protocol == ProxyProtocol.Vmess ? o.Str("security") ?? "auto" : protocol == ProxyProtocol.Vless ? "none" : null,
                AlterId = o.Int("alter_id"),
                PacketEncoding = o.Str("packet_encoding"),
            },
            Transport = ParseTransport(o.Obj("transport")),
            Tls = ParseTls(o.Obj("tls")),
            Mux = ParseMux(o.Obj("multiplex")),
            Udp = o.Str("network") switch
            {
                "tcp" => false,
                _ => null,
            },
            Extensions = o.Unknown(CommonKnown),
        };

        if (protocol == ProxyProtocol.Http)
        {
            profile = profile with
            {
                Transport = profile.Transport with
                {
                    Path = o.Str("path"),
                    Headers = o.StringMap("headers"),
                },
            };
        }

        return protocol switch
        {
            ProxyProtocol.Shadowsocks => profile with
            {
                Shadowsocks = new ShadowsocksOptions
                {
                    Method = o.Str("method") ?? throw new ProfileParseException("Shadowsocks outbound has no method."),
                    Plugin = o.Str("plugin"),
                    PluginOptions = o.Str("plugin_opts"),
                    UdpOverTcp = o.Bool("udp_over_tcp") == true || o.Obj("udp_over_tcp")?.Bool("enabled") == true,
                },
            },
            ProxyProtocol.Hysteria2 => profile with
            {
                Hysteria2 = new Hysteria2Options
                {
                    UpMbps = o.Int("up_mbps"),
                    DownMbps = o.Int("down_mbps"),
                    ObfsType = o.Obj("obfs")?.Str("type"),
                    ObfsPassword = o.Obj("obfs")?.Str("password"),
                    ServerPorts = o.StrList("server_ports"),
                    HopInterval = ProfileFactory.ParseDuration(o.Str("hop_interval")),
                },
            },
            ProxyProtocol.Tuic => profile with
            {
                Tuic = new TuicOptions
                {
                    CongestionControl = o.Str("congestion_control"),
                    UdpRelayMode = o.Str("udp_relay_mode"),
                    ZeroRttHandshake = o.Bool("zero_rtt_handshake") == true,
                    Heartbeat = ProfileFactory.ParseDuration(o.Str("heartbeat")),
                },
            },
            _ => profile,
        };
    }

    /// <summary>sing-box ≥1.11 WireGuard endpoint (single peer supported).</summary>
    public static ProxyProfile ParseEndpoint(JsonObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!string.Equals(o.Str("type"), "wireguard", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileParseException($"Unsupported endpoint type '{ProfileFactory.Truncate(o.Str("type"))}'.");
        }

        var peers = o.Arr("peers")?.OfType<JsonObject>().ToList() ?? [];
        if (peers.Count == 0)
        {
            throw new ProfileParseException("WireGuard endpoint has no peers.");
        }

        var peer = peers[0];
        var known = new HashSet<string>(StringComparer.Ordinal) { "type", "tag", "address", "private_key", "peers", "mtu" };
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = o.Str("tag") ?? string.Empty,
            Protocol = ProxyProtocol.WireGuard,
            Server = peer.Str("address") ?? throw new ProfileParseException("WireGuard peer has no address."),
            Port = peer.Int("port") ?? throw new ProfileParseException("WireGuard peer has no port."),
            WireGuard = new WireGuardOptions
            {
                PrivateKey = o.Str("private_key") ?? string.Empty,
                PeerPublicKey = peer.Str("public_key") ?? string.Empty,
                PreSharedKey = peer.Str("pre_shared_key"),
                LocalAddresses = o.StrList("address"),
                AllowedIps = peer.StrList("allowed_ips") is { Count: > 0 } a ? a : ["0.0.0.0/0", "::/0"],
                Reserved = peer.IntList("reserved"),
                Mtu = o.Int("mtu"),
                PersistentKeepaliveSeconds = peer.Int("persistent_keepalive_interval"),
            },
            Extensions = o.Unknown(known),
        };
    }

    private static ProxyProfile ParseLegacyWireGuard(JsonObject o) => new()
    {
        Id = string.Empty,
        Name = o.Str("tag") ?? string.Empty,
        Protocol = ProxyProtocol.WireGuard,
        Server = o.Str("server") ?? throw new ProfileParseException("WireGuard outbound has no server."),
        Port = o.Int("server_port") ?? throw new ProfileParseException("WireGuard outbound has no server_port."),
        WireGuard = new WireGuardOptions
        {
            PrivateKey = o.Str("private_key") ?? string.Empty,
            PeerPublicKey = o.Str("peer_public_key") ?? string.Empty,
            PreSharedKey = o.Str("pre_shared_key"),
            LocalAddresses = o.StrList("local_address"),
            Reserved = o.IntList("reserved"),
            Mtu = o.Int("mtu"),
        },
        Extensions = o.Unknown(CommonKnown),
    };

    public static TransportOptions ParseTransport(JsonObject? t)
    {
        if (t is null)
        {
            return new TransportOptions();
        }

        var type = ProfileFactory.ParseTransport(t.Str("type"));
        return type switch
        {
            TransportType.Grpc => new TransportOptions
            {
                Type = type,
                ServiceName = t.Str("service_name"),
                Extensions = t.Unknown(TransportKnown),
            },
            TransportType.Http2 => new TransportOptions
            {
                Type = type,
                Host = string.Join(",", t.StrList("host")) is { Length: > 0 } h ? h : null,
                Path = t.Str("path"),
                Method = t.Str("method"),
                Headers = t.StringMap("headers", "Host"),
                Extensions = t.Unknown(TransportKnown),
            },
            _ => new TransportOptions
            {
                Type = type,
                Path = t.Str("path"),
                Host = t.Str("host") ?? t.HeaderValue("headers", "Host"),
                Headers = t.StringMap("headers", "Host"),
                MaxEarlyData = t.Int("max_early_data"),
                EarlyDataHeaderName = t.Str("early_data_header_name"),
                Extensions = t.Unknown(TransportKnown),
            },
        };
    }

    public static TlsOptions ParseTls(JsonObject? t)
    {
        if (t is null || t.Bool("enabled") != true)
        {
            return new TlsOptions();
        }

        var utls = t.Obj("utls");
        var reality = t.Obj("reality");
        var ech = t.Obj("ech");
        var fragmentEnabled = t.Bool("fragment") == true;
        var recordFragment = t.Bool("record_fragment") == true;
        return new TlsOptions
        {
            Enabled = true,
            ServerName = t.Str("server_name"),
            DisableSni = t.Bool("disable_sni") == true,
            AllowInsecure = t.Bool("insecure") == true,
            Alpn = t.StrList("alpn"),
            MinVersion = t.Str("min_version"),
            MaxVersion = t.Str("max_version"),
            Fingerprint = utls?.Bool("enabled") == true ? utls.Str("fingerprint") ?? "chrome" : null,
            Fragment = fragmentEnabled || recordFragment
                ? new TlsFragmentOptions
                {
                    Enabled = fragmentEnabled,
                    RecordFragment = recordFragment,
                    FallbackDelay = ProfileFactory.ParseDuration(t.Str("fragment_fallback_delay")),
                }
                : null,
            Reality = reality?.Bool("enabled") == true
                ? new RealityOptions { Enabled = true, PublicKey = reality.Str("public_key"), ShortId = reality.Str("short_id") }
                : null,
            Ech = ech?.Bool("enabled") == true
                ? new EchOptions { Enabled = true, Config = string.Join("\n", ech.StrList("config")), QueryServerName = ech.Str("query_server_name") }
                : null,
            PublicKeyPins = t.StrList("certificate_public_key_sha256"),
            Extensions = t.Unknown(TlsKnown),
        };
    }

    private static MuxOptions? ParseMux(JsonObject? m)
    {
        if (m is null)
        {
            return null;
        }

        return new MuxOptions
        {
            Mode = m.Bool("enabled") == true ? MuxMode.On : MuxMode.Off,
            Protocol = m.Str("protocol"),
            MaxConnections = m.Int("max_connections"),
            MinStreams = m.Int("min_streams"),
            MaxStreams = m.Int("max_streams"),
            Padding = m.Bool("padding") == true,
            Extensions = m.Unknown(MuxKnown),
        };
    }
}
