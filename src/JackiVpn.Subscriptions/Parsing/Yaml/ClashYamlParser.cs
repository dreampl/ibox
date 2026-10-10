using System.Text;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing.Json;

namespace JackiVpn.Subscriptions.Parsing.Yaml;

/// <summary>Clash / Clash Meta (mihomo) YAML <c>proxies:</c> → <see cref="ProxyProfile"/>.</summary>
public static class ClashYamlParser
{
    private static readonly HashSet<string> MetaOnlyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "hysteria2", "hysteria", "tuic", "wireguard",
    };

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "name", "type", "server", "port", "uuid", "password", "cipher", "alterId", "flow", "tls", "servername", "sni", "alpn",
        "skip-cert-verify", "client-fingerprint", "network", "ws-opts", "grpc-opts", "h2-opts", "http-opts", "reality-opts",
        "udp", "packet-encoding", "smux", "plugin", "plugin-opts", "udp-over-tcp", "username", "headers", "ports", "obfs",
        "obfs-password", "up", "down", "congestion-controller", "udp-relay-mode", "reduce-rtt", "heartbeat-interval",
        "disable-sni", "private-key", "public-key", "pre-shared-key", "ip", "ipv6", "reserved", "mtu", "allowed-ips",
        "fingerprint", "ech-opts",
    };

    public static bool LooksLikeClash(string text)
    {
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var t = line.TrimEnd();
            if (t.StartsWith("proxies:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static void Parse(string text, ParseContext ctx, ParseResult result)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(result);
        var root = SafeYaml.Load(text, ctx.Limits.MaxYamlNodes, ctx.Limits.MaxYamlDepth) as JsonObject
            ?? throw new ProfileParseException("YAML root is not a mapping.");
        var proxies = root.Arr("proxies")?.OfType<JsonObject>().ToList() ?? [];
        var isMeta = proxies.Any(p => MetaOnlyTypes.Contains(p.Str("type") ?? string.Empty)) || root.Obj("sniffer") is not null;
        result.Format = isMeta ? ConfigFormat.ClashMetaYaml : ConfigFormat.ClashYaml;

        var index = 0;
        foreach (var p in proxies)
        {
            index++;
            if (result.TotalEntries >= ctx.Limits.MaxEntries)
            {
                result.Warn(null, $"Entry limit ({ctx.Limits.MaxEntries}) reached; remaining entries ignored.");
                break;
            }

            result.TotalEntries++;
            try
            {
                var profile = ParseProxy(p);
                result.Profiles.Add(ProfileFactory.Finish(profile, ctx, result.Format, p.ToJsonString()));
            }
            catch (ProfileParseException ex)
            {
                result.Error(index, ex.Message, p.ToJsonString());
            }
        }
    }

    public static ProxyProfile ParseProxy(JsonObject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var type = (p.Str("type") ?? string.Empty).ToLowerInvariant();
        var protocol = type switch
        {
            "vless" => ProxyProtocol.Vless,
            "vmess" => ProxyProtocol.Vmess,
            "trojan" => ProxyProtocol.Trojan,
            "ss" or "shadowsocks" => ProxyProtocol.Shadowsocks,
            "socks5" or "socks" => ProxyProtocol.Socks,
            "http" => ProxyProtocol.Http,
            "hysteria2" or "hy2" => ProxyProtocol.Hysteria2,
            "tuic" => ProxyProtocol.Tuic,
            "wireguard" => ProxyProtocol.WireGuard,
            _ => throw new ProfileParseException($"Unsupported Clash proxy type '{ProfileFactory.Truncate(type)}'."),
        };

        var server = p.Str("server") ?? throw new ProfileParseException("Proxy has no server.");
        var port = p.Int("port") ?? throw new ProfileParseException("Proxy has no port.");
        var profile = new ProxyProfile
        {
            Id = string.Empty,
            Name = p.Str("name") ?? string.Empty,
            Protocol = protocol,
            Server = server,
            Port = port,
            Credentials = new ProxyCredentials
            {
                Uuid = p.Str("uuid"),
                Password = p.Str("password"),
                Username = p.Str("username"),
                Flow = p.Str("flow"),
                AlterId = p.Int("alterId"),
                Encryption = protocol == ProxyProtocol.Vmess ? p.Str("cipher") ?? "auto" : protocol == ProxyProtocol.Vless ? "none" : null,
                PacketEncoding = p.Str("packet-encoding"),
            },
            Transport = ParseTransport(p),
            Tls = ParseTls(p, protocol),
            Udp = p.Bool("udp"),
            Mux = p.Obj("smux") is { } smux
                ? new MuxOptions
                {
                    Mode = smux.Bool("enabled") == true ? MuxMode.On : MuxMode.Off,
                    Protocol = smux.Str("protocol"),
                    MaxConnections = smux.Int("max-connections"),
                    MinStreams = smux.Int("min-streams"),
                    MaxStreams = smux.Int("max-streams"),
                    Padding = smux.Bool("padding") == true,
                }
                : null,
            Extensions = p.Unknown(Known),
        };

        return protocol switch
        {
            ProxyProtocol.Shadowsocks => profile with
            {
                Shadowsocks = new ShadowsocksOptions
                {
                    Method = p.Str("cipher") ?? throw new ProfileParseException("Shadowsocks proxy has no cipher."),
                    Plugin = MapPlugin(p.Str("plugin")),
                    PluginOptions = MapPluginOptions(p.Str("plugin"), p.Obj("plugin-opts")),
                    UdpOverTcp = p.Bool("udp-over-tcp") == true,
                },
                Credentials = profile.Credentials with { Encryption = null },
            },
            ProxyProtocol.Hysteria2 => profile with
            {
                Hysteria2 = new Hysteria2Options
                {
                    ObfsType = p.Str("obfs"),
                    ObfsPassword = p.Str("obfs-password"),
                    UpMbps = ParseMbps(p.Str("up")),
                    DownMbps = ParseMbps(p.Str("down")),
                    ServerPorts = p.Str("ports") is { } ports
                        ? ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Replace('-', ':')).ToArray()
                        : [],
                },
            },
            ProxyProtocol.Tuic => profile with
            {
                Tuic = new TuicOptions
                {
                    CongestionControl = p.Str("congestion-controller"),
                    UdpRelayMode = p.Str("udp-relay-mode"),
                    ZeroRttHandshake = p.Bool("reduce-rtt") == true,
                    Heartbeat = ProfileFactory.ParseDuration(p.Str("heartbeat-interval")),
                },
            },
            ProxyProtocol.WireGuard => profile with
            {
                WireGuard = new WireGuardOptions
                {
                    PrivateKey = p.Str("private-key") ?? string.Empty,
                    PeerPublicKey = p.Str("public-key") ?? string.Empty,
                    PreSharedKey = p.Str("pre-shared-key"),
                    LocalAddresses = new[] { p.Str("ip"), p.Str("ipv6") }
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a!.Contains('/', StringComparison.Ordinal) ? a : a + (a.Contains(':', StringComparison.Ordinal) ? "/128" : "/32"))
                        .ToArray(),
                    Reserved = p.IntList("reserved"),
                    Mtu = p.Int("mtu"),
                    AllowedIps = p.StrList("allowed-ips") is { Count: > 0 } a2 ? a2 : ["0.0.0.0/0", "::/0"],
                },
            },
            _ => profile,
        };
    }

    private static TransportOptions ParseTransport(JsonObject p)
    {
        var network = p.Str("network");
        var type = ProfileFactory.ParseTransport(network);
        switch (type)
        {
            case TransportType.WebSocket:
            {
                var ws = p.Obj("ws-opts");
                return new TransportOptions
                {
                    Type = type,
                    Path = ws.Str("path"),
                    Host = ws.HeaderValue("headers", "Host"),
                    Headers = ws.StringMap("headers", "Host"),
                    MaxEarlyData = ws.Int("max-early-data"),
                    EarlyDataHeaderName = ws.Str("early-data-header-name"),
                    Extensions = ws.Obj("v2ray-http-upgrade") is not null || ws.Bool("v2ray-http-upgrade") == true
                        ? new JsonObject { ["v2ray-http-upgrade"] = true }
                        : null,
                };
            }

            case TransportType.Grpc:
                return new TransportOptions { Type = type, ServiceName = p.Obj("grpc-opts").Str("grpc-service-name") };
            case TransportType.Http2:
            {
                var h2 = p.Obj("h2-opts");
                if (h2 is not null)
                {
                    return new TransportOptions { Type = type, Host = string.Join(",", h2.StrList("host")) is { Length: > 0 } h ? h : null, Path = h2.Str("path") };
                }

                // Clash "http" network is TCP with an HTTP obfuscation header.
                var http = p.Obj("http-opts");
                return new TransportOptions
                {
                    Type = TransportType.Tcp,
                    HeaderType = "http",
                    Path = http.StrList("path").FirstOrDefault(),
                    Host = http.Obj("headers").StrList("Host").FirstOrDefault(),
                    Method = http.Str("method"),
                };
            }

            default:
                return new TransportOptions { Type = type };
        }
    }

    private static TlsOptions ParseTls(JsonObject p, ProxyProtocol protocol)
    {
        var implicitTls = protocol is ProxyProtocol.Trojan or ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic;
        var reality = p.Obj("reality-opts");
        var tls = p.Bool("tls") == true || implicitTls || reality is not null;
        if (!tls)
        {
            return new TlsOptions();
        }

        return new TlsOptions
        {
            Enabled = true,
            ServerName = p.Str("servername") ?? p.Str("sni"),
            Alpn = p.StrList("alpn"),
            AllowInsecure = p.Bool("skip-cert-verify") == true,
            Fingerprint = p.Str("client-fingerprint"),
            DisableSni = p.Bool("disable-sni") == true,
            Reality = reality is not null
                ? new RealityOptions { Enabled = true, PublicKey = reality.Str("public-key"), ShortId = reality.Str("short-id") }
                : null,
            PublicKeyPins = protocol == ProxyProtocol.Hysteria2 && p.Str("fingerprint") is { } pin ? [pin] : [],
        };
    }

    private static string? MapPlugin(string? clashPlugin) => clashPlugin?.ToLowerInvariant() switch
    {
        null or "" => null,
        "obfs" => "obfs-local",
        "v2ray-plugin" => "v2ray-plugin",
        var other => other,
    };

    private static string? MapPluginOptions(string? plugin, JsonObject? opts)
    {
        if (opts is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        void Add(string kv)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(kv);
        }

        switch (plugin?.ToLowerInvariant())
        {
            case "obfs":
                if (opts.Str("mode") is { } mode)
                {
                    Add("obfs=" + mode);
                }

                if (opts.Str("host") is { } host)
                {
                    Add("obfs-host=" + host);
                }

                break;
            case "v2ray-plugin":
                if (opts.Str("mode") is { } m)
                {
                    Add("mode=" + m);
                }

                if (opts.Bool("tls") == true)
                {
                    Add("tls");
                }

                if (opts.Str("host") is { } h)
                {
                    Add("host=" + h);
                }

                if (opts.Str("path") is { } path)
                {
                    Add("path=" + path);
                }

                if (opts.Bool("mux") == false)
                {
                    Add("mux=0");
                }

                break;
            default:
                foreach (var (k, v) in opts)
                {
                    Add(k + "=" + v);
                }

                break;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static int? ParseMbps(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
}
