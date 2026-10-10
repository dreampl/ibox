using System.Globalization;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Subscriptions.Parsing.Json;

/// <summary>
/// Xray JSON → <see cref="ProxyProfile"/>. Accepts a full config, an array of full configs (PasarGuard
/// <c>/xray</c> format, names from <c>remarks</c>), a single outbound or an array of outbounds.
/// The Xray "fragment chain" (<c>sockopt.dialerProxy</c> → <c>freedom</c> outbound with <c>fragment</c>)
/// is recognized and normalized into <see cref="TlsFragmentOptions"/>.
/// </summary>
public static class XrayJsonParser
{
    private static readonly HashSet<string> ProxyProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "vmess", "trojan", "shadowsocks", "socks", "http", "wireguard",
    };

    private static readonly HashSet<string> OutboundKnown = new(StringComparer.Ordinal)
    {
        "protocol", "tag", "settings", "streamSettings", "mux",
    };

    private static readonly HashSet<string> TlsKnown = new(StringComparer.Ordinal)
    {
        "serverName", "alpn", "fingerprint", "allowInsecure", "minVersion", "maxVersion", "echConfigList", "pinnedPeerCertSha256",
    };

    private static readonly HashSet<string> RealityKnown = new(StringComparer.Ordinal)
    {
        "serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "show",
    };

    public static void ParseDocument(JsonNode root, ParseContext ctx, ParseResult result, ConfigFormat format)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(result);
        switch (root)
        {
            case JsonArray arr when arr.OfType<JsonObject>().Any(x => x.Arr("outbounds") is not null):
                foreach (var cfg in arr.OfType<JsonObject>())
                {
                    ParseFullConfig(cfg, ctx, result, format);
                }

                break;
            case JsonObject o when o.Arr("outbounds") is not null:
                ParseFullConfig(o, ctx, result, format);
                break;
            case JsonObject single:
                ParseOutbounds([single], null, ctx, result, format);
                break;
            case JsonArray outbounds:
                ParseOutbounds(outbounds.OfType<JsonObject>().ToList(), null, ctx, result, format);
                break;
        }
    }

    private static void ParseFullConfig(JsonObject cfg, ParseContext ctx, ParseResult result, ConfigFormat format)
    {
        var outbounds = cfg.Arr("outbounds")?.OfType<JsonObject>().ToList() ?? [];
        ParseOutbounds(outbounds, cfg.Str("remarks"), ctx, result, format);
    }

    private static void ParseOutbounds(List<JsonObject> outbounds, string? remarks, ParseContext ctx, ParseResult result, ConfigFormat format)
    {
        var byTag = outbounds.Where(o => o.Str("tag") is not null)
            .GroupBy(o => o.Str("tag")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var proxies = outbounds.Where(o => ProxyProtocols.Contains(o.Str("protocol") ?? string.Empty)).ToList();

        foreach (var o in outbounds)
        {
            var proto = o.Str("protocol") ?? string.Empty;
            if (!ProxyProtocols.Contains(proto) && proto is not ("freedom" or "blackhole" or "dns" or "loopback" or ""))
            {
                result.Warn(null, $"Xray outbound protocol '{ProfileFactory.Truncate(proto)}' is not supported yet; skipped.");
            }
        }

        foreach (var o in proxies)
        {
            if (result.TotalEntries >= ctx.Limits.MaxEntries)
            {
                result.Warn(null, $"Entry limit ({ctx.Limits.MaxEntries}) reached; remaining entries ignored.");
                return;
            }

            result.TotalEntries++;
            try
            {
                var profile = ParseOutbound(o, byTag);
                if (proxies.Count == 1 && !string.IsNullOrWhiteSpace(remarks))
                {
                    profile = profile with { Name = remarks };
                }
                else if (!string.IsNullOrWhiteSpace(remarks) && string.IsNullOrWhiteSpace(profile.Name))
                {
                    profile = profile with { Name = remarks };
                }

                result.Profiles.Add(ProfileFactory.Finish(profile, ctx, format, o.ToJsonString()));
            }
            catch (ProfileParseException ex)
            {
                result.Error(null, ex.Message, o.ToJsonString());
            }
        }
    }

    public static ProxyProfile ParseOutbound(JsonObject o, IReadOnlyDictionary<string, JsonObject>? byTag = null)
    {
        ArgumentNullException.ThrowIfNull(o);
        var proto = (o.Str("protocol") ?? string.Empty).ToLowerInvariant();
        var settings = o.Obj("settings") ?? new JsonObject();
        var stream = o.Obj("streamSettings");
        var tag = o.Str("tag");
        var name = tag is null or "proxy" ? string.Empty : tag;

        ProxyProfile profile = proto switch
        {
            "vless" or "vmess" => ParseVnext(proto, settings, name),
            "trojan" or "shadowsocks" or "socks" or "http" => ParseServers(proto, settings, name),
            "wireguard" => ParseWireGuard(settings, name),
            _ => throw new ProfileParseException($"Unsupported Xray protocol '{ProfileFactory.Truncate(proto)}'."),
        };

        if (stream is not null && profile.Protocol != ProxyProtocol.WireGuard)
        {
            var (transport, tls) = ParseStream(stream);
            profile = profile with { Transport = transport, Tls = tls };

            // Fragment chain: sockopt.dialerProxy → freedom outbound with "fragment".
            var sockopt = stream.Obj("sockopt");
            var dialer = sockopt?.Str("dialerProxy");
            if (dialer is not null && byTag is not null && byTag.TryGetValue(dialer, out var chained)
                && string.Equals(chained.Str("protocol"), "freedom", StringComparison.OrdinalIgnoreCase)
                && chained.Obj("settings")?.Obj("fragment") is { } frag)
            {
                profile = profile with
                {
                    Tls = profile.Tls with
                    {
                        Fragment = new TlsFragmentOptions
                        {
                            Enabled = true,
                            Packets = frag.Str("packets"),
                            Length = frag.Str("length"),
                            Interval = frag.Str("interval"),
                        },
                    },
                };
            }

            var sockoptRest = sockopt?.Unknown(new HashSet<string>(StringComparer.Ordinal) { "dialerProxy" });
            if (sockoptRest is not null)
            {
                var ext = profile.Extensions ?? new JsonObject();
                ext["sockopt"] = sockoptRest;
                profile = profile with { Extensions = ext };
            }
        }

        if (o.Obj("mux") is { } mux)
        {
            profile = profile with
            {
                Mux = new MuxOptions
                {
                    Mode = mux.Bool("enabled") == true ? MuxMode.On : MuxMode.Off,
                    XrayConcurrency = mux.Int("concurrency"),
                    XudpConcurrency = mux.Int("xudpConcurrency"),
                    XudpProxyUdp443 = mux.Str("xudpProxyUDP443"),
                },
            };
        }

        var unknown = o.Unknown(OutboundKnown);
        if (unknown is not null)
        {
            var ext = profile.Extensions ?? new JsonObject();
            foreach (var (k, v) in unknown)
            {
                ext[k] = v?.DeepClone();
            }

            profile = profile with { Extensions = ext };
        }

        return profile;
    }

    private static ProxyProfile ParseVnext(string proto, JsonObject settings, string name)
    {
        JsonObject server;
        JsonObject user;
        if (settings.Arr("vnext")?.OfType<JsonObject>().FirstOrDefault() is { } vnext)
        {
            server = vnext;
            user = vnext.Arr("users")?.OfType<JsonObject>().FirstOrDefault() ?? throw new ProfileParseException("vnext has no users.");
        }
        else
        {
            // Newer flat form: settings.address / settings.port / settings.id.
            server = settings;
            user = settings;
        }

        return new ProxyProfile
        {
            Id = string.Empty,
            Name = name,
            Protocol = proto == "vless" ? ProxyProtocol.Vless : ProxyProtocol.Vmess,
            Server = server.Str("address") ?? throw new ProfileParseException("Outbound has no address."),
            Port = server.Int("port") ?? throw new ProfileParseException("Outbound has no port."),
            Credentials = new ProxyCredentials
            {
                Uuid = user.Str("id") ?? throw new ProfileParseException("Outbound user has no id."),
                Flow = user.Str("flow"),
                Encryption = proto == "vless" ? user.Str("encryption") ?? "none" : user.Str("security") ?? "auto",
                AlterId = user.Int("alterId"),
            },
        };
    }

    private static ProxyProfile ParseServers(string proto, JsonObject settings, string name)
    {
        var server = settings.Arr("servers")?.OfType<JsonObject>().FirstOrDefault() ?? settings;
        var user = server.Arr("users")?.OfType<JsonObject>().FirstOrDefault();
        var protocol = proto switch
        {
            "trojan" => ProxyProtocol.Trojan,
            "shadowsocks" => ProxyProtocol.Shadowsocks,
            "socks" => ProxyProtocol.Socks,
            _ => ProxyProtocol.Http,
        };

        var profile = new ProxyProfile
        {
            Id = string.Empty,
            Name = name,
            Protocol = protocol,
            Server = server.Str("address") ?? throw new ProfileParseException("Outbound has no address."),
            Port = server.Int("port") ?? throw new ProfileParseException("Outbound has no port."),
            Credentials = new ProxyCredentials
            {
                Password = server.Str("password") ?? user?.Str("pass"),
                Username = user?.Str("user"),
                Flow = server.Str("flow"),
            },
        };

        return protocol == ProxyProtocol.Shadowsocks
            ? profile with
            {
                Shadowsocks = new ShadowsocksOptions
                {
                    Method = server.Str("method") ?? throw new ProfileParseException("Shadowsocks server has no method."),
                    UdpOverTcp = server.Bool("uot") == true,
                },
            }
            : profile;
    }

    private static ProxyProfile ParseWireGuard(JsonObject settings, string name)
    {
        var peer = settings.Arr("peers")?.OfType<JsonObject>().FirstOrDefault() ?? throw new ProfileParseException("WireGuard has no peers.");
        var endpoint = peer.Str("endpoint") ?? throw new ProfileParseException("WireGuard peer has no endpoint.");
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new ProfileParseException("WireGuard endpoint must be host:port.");
        }

        var host = endpoint[..colon].Trim('[', ']');
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = name,
            Protocol = ProxyProtocol.WireGuard,
            Server = host,
            Port = port,
            WireGuard = new WireGuardOptions
            {
                PrivateKey = settings.Str("secretKey") ?? string.Empty,
                PeerPublicKey = peer.Str("publicKey") ?? string.Empty,
                PreSharedKey = peer.Str("preSharedKey"),
                LocalAddresses = settings.StrList("address"),
                AllowedIps = peer.StrList("allowedIPs") is { Count: > 0 } a ? a : ["0.0.0.0/0", "::/0"],
                Reserved = settings.IntList("reserved"),
                Mtu = settings.Int("mtu"),
                PersistentKeepaliveSeconds = peer.Int("keepAlive"),
            },
        };
    }

    public static (TransportOptions Transport, TlsOptions Tls) ParseStream(JsonObject stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var network = stream.Str("network");
        var type = ProfileFactory.ParseTransport(network);
        TransportOptions transport;
        switch (type)
        {
            case TransportType.WebSocket:
            {
                var ws = stream.Obj("wsSettings");
                transport = new TransportOptions
                {
                    Type = type,
                    Path = ws.Str("path"),
                    Host = ws.Str("host") ?? ws.HeaderValue("headers", "Host"),
                    Headers = ws.StringMap("headers", "Host"),
                    Extensions = ws.Unknown(new HashSet<string>(StringComparer.Ordinal) { "path", "host", "headers" }),
                };
                break;
            }

            case TransportType.Grpc:
            {
                var g = stream.Obj("grpcSettings");
                transport = new TransportOptions
                {
                    Type = type,
                    ServiceName = g.Str("serviceName"),
                    GrpcMultiMode = g.Bool("multiMode") == true,
                    Extensions = g.Unknown(new HashSet<string>(StringComparer.Ordinal) { "serviceName", "multiMode" }),
                };
                break;
            }

            case TransportType.HttpUpgrade:
            {
                var h = stream.Obj("httpupgradeSettings");
                transport = new TransportOptions
                {
                    Type = type,
                    Path = h.Str("path"),
                    Host = h.Str("host"),
                    Headers = h.StringMap("headers", "Host"),
                    Extensions = h.Unknown(new HashSet<string>(StringComparer.Ordinal) { "path", "host", "headers" }),
                };
                break;
            }

            case TransportType.XHttp:
            {
                var x = stream.Obj("xhttpSettings") ?? stream.Obj("splithttpSettings");
                transport = new TransportOptions
                {
                    Type = type,
                    Path = x.Str("path"),
                    Host = x.Str("host"),
                    XHttpMode = x.Str("mode"),
                    Extensions = x.Unknown(new HashSet<string>(StringComparer.Ordinal) { "path", "host", "mode" }),
                };
                break;
            }

            case TransportType.Http2:
            {
                var h = stream.Obj("httpSettings");
                transport = new TransportOptions
                {
                    Type = type,
                    Path = h.Str("path"),
                    Host = string.Join(",", h.StrList("host")) is { Length: > 0 } hosts ? hosts : null,
                    Method = h.Str("method"),
                };
                break;
            }

            case TransportType.Kcp:
            {
                var k = stream.Obj("kcpSettings");
                transport = new TransportOptions
                {
                    Type = type,
                    HeaderType = k.Obj("header").Str("type"),
                    KcpSeed = k.Str("seed"),
                    Extensions = k.Unknown(new HashSet<string>(StringComparer.Ordinal) { "header", "seed" }),
                };
                break;
            }

            default:
            {
                var tcp = stream.Obj("tcpSettings") ?? stream.Obj("rawSettings");
                var header = tcp.Obj("header");
                var headerType = header.Str("type");
                var request = header.Obj("request");
                transport = new TransportOptions
                {
                    Type = type,
                    HeaderType = headerType,
                    Path = headerType == "http" ? request.StrList("path").FirstOrDefault() : null,
                    Host = headerType == "http" ? request.Obj("headers").StrList("Host").FirstOrDefault() : null,
                    Extensions = tcp.Unknown(new HashSet<string>(StringComparer.Ordinal) { "header" }),
                };
                break;
            }
        }

        var security = (stream.Str("security") ?? "none").ToLowerInvariant();
        TlsOptions tls;
        if (security is "tls" or "xtls")
        {
            var t = stream.Obj("tlsSettings") ?? stream.Obj("xtlsSettings");
            tls = new TlsOptions
            {
                Enabled = true,
                ServerName = t.Str("serverName"),
                Alpn = t.StrList("alpn"),
                Fingerprint = t.Str("fingerprint"),
                AllowInsecure = t.Bool("allowInsecure") == true,
                MinVersion = t.Str("minVersion"),
                MaxVersion = t.Str("maxVersion"),
                Ech = t.Str("echConfigList") is { } ech ? new EchOptions { Enabled = true, Config = ech } : null,
                CertificateSha256Pins = t.StrList("pinnedPeerCertSha256"),
                Extensions = t.Unknown(TlsKnown),
            };
        }
        else if (security == "reality")
        {
            var r = stream.Obj("realitySettings");
            tls = new TlsOptions
            {
                Enabled = true,
                ServerName = r.Str("serverName"),
                Fingerprint = r.Str("fingerprint") ?? "chrome",
                Reality = new RealityOptions
                {
                    Enabled = true,
                    PublicKey = r.Str("publicKey") ?? r.Str("password"),
                    ShortId = r.Str("shortId"),
                    SpiderX = r.Str("spiderX"),
                },
                Extensions = r.Unknown(RealityKnown),
            };
        }
        else
        {
            tls = new TlsOptions();
        }

        return (transport, tls);
    }
}
