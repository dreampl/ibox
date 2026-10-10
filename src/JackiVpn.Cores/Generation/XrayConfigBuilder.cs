using System.Globalization;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Cores.Generation;

/// <summary>
/// Builds Xray configurations. Xray is used as a protocol backend (sing-box owns TUN, DNS, routing and
/// statistics), so a config is a set of loopback SOCKS inbounds routed 1:1 to proxy outbounds.
/// TLS fragmentation is expressed as Xray's <c>freedom</c> fragment outbound chained via
/// <c>sockopt.dialerProxy</c>.
/// </summary>
public static class XrayConfigBuilder
{
    public static (JsonObject Config, List<string> Secrets, List<string> Notes, List<int> Ports) Build(CoreSessionPlan plan, CoreCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(caps);
        if (plan.Kind is not (SessionKind.ChainBackend or SessionKind.ProbeBatch))
        {
            throw new ArgumentException($"Xray is only used as a backend or for probe batches (got {plan.Kind}).", nameof(plan));
        }

        var secrets = new List<string>();
        var notes = new List<string>();
        var ports = new List<int>();
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();

        var portsFor = plan.Kind == SessionKind.ProbeBatch ? plan.ProbePorts : [plan.ListenPort];
        if (portsFor.Count != plan.Profiles.Count)
        {
            throw new ArgumentException("Exactly one port per profile is required.", nameof(plan));
        }

        for (var i = 0; i < plan.Profiles.Count; i++)
        {
            var inTag = plan.Kind == SessionKind.ProbeBatch ? $"probe-in-{i}" : "socks-in";
            var outTag = plan.Kind == SessionKind.ProbeBatch ? $"probe-out-{i}" : "proxy";
            var socks = new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" };
            if (plan.Kind == SessionKind.ChainBackend && plan.InboundAuth is { } auth)
            {
                socks["auth"] = "password";
                socks["accounts"] = new JsonArray(new JsonObject { ["user"] = auth.Username, ["pass"] = auth.Password });
                secrets.Add(auth.Password);
            }

            inbounds.Add(new JsonObject
            {
                ["tag"] = inTag,
                ["listen"] = "127.0.0.1",
                ["port"] = portsFor[i],
                ["protocol"] = "socks",
                ["settings"] = socks,
            });
            ports.Add(portsFor[i]);

            var (outbound, fragment) = BuildOutbound(plan.Profiles[i], outTag, caps, plan.MuxEnabled, secrets, notes);
            outbounds.Add(outbound);
            if (fragment is not null)
            {
                outbounds.Add(fragment);
            }

            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(inTag),
                ["outboundTag"] = outTag,
            });
        }

        outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = plan.DebugLogging ? "debug" : "warning" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules },
        };
        return (root, secrets, notes, ports);
    }

    public static (JsonObject Outbound, JsonObject? FragmentOutbound) BuildOutbound(
        ProxyProfile p, string tag, CoreCapabilities caps, bool muxEnabled, ICollection<string> secrets, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(caps);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(notes);
        var sameDialect = p.Origin.SourceFormat is ConfigFormat.XrayFull or ConfigFormat.XrayOutbound;
        var dropped = new List<string>();
        var c = p.Credentials;
        JsonObject settings;
        string protocol;

        switch (p.Protocol)
        {
            case ProxyProtocol.Vless:
            case ProxyProtocol.Vmess:
            {
                protocol = p.Protocol == ProxyProtocol.Vless ? "vless" : "vmess";
                var user = new JsonObject { ["id"] = Secret(c.Uuid, secrets) };
                if (p.Protocol == ProxyProtocol.Vless)
                {
                    user["encryption"] = string.IsNullOrEmpty(c.Encryption) ? "none" : c.Encryption;
                    if (!string.IsNullOrEmpty(c.Flow))
                    {
                        user["flow"] = c.Flow;
                    }
                }
                else
                {
                    user["alterId"] = c.AlterId ?? 0;
                    user["security"] = string.IsNullOrEmpty(c.Encryption) ? "auto" : c.Encryption;
                }

                settings = new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject { ["address"] = p.Server, ["port"] = p.Port, ["users"] = new JsonArray(user) }),
                };
                break;
            }

            case ProxyProtocol.Trojan:
                protocol = "trojan";
                settings = new JsonObject
                {
                    ["servers"] = new JsonArray(new JsonObject { ["address"] = p.Server, ["port"] = p.Port, ["password"] = Secret(c.Password, secrets) }),
                };
                break;
            case ProxyProtocol.Shadowsocks:
            {
                protocol = "shadowsocks";
                var server = new JsonObject
                {
                    ["address"] = p.Server,
                    ["port"] = p.Port,
                    ["method"] = p.Shadowsocks!.Method,
                    ["password"] = Secret(c.Password, secrets),
                };
                if (p.Shadowsocks.UdpOverTcp)
                {
                    server["uot"] = true;
                }

                settings = new JsonObject { ["servers"] = new JsonArray(server) };
                break;
            }

            case ProxyProtocol.Socks:
            case ProxyProtocol.Http:
            {
                protocol = p.Protocol == ProxyProtocol.Socks ? "socks" : "http";
                var server = new JsonObject { ["address"] = p.Server, ["port"] = p.Port };
                if (!string.IsNullOrEmpty(c.Username))
                {
                    server["users"] = new JsonArray(new JsonObject { ["user"] = c.Username, ["pass"] = Secret(c.Password, secrets) });
                }

                settings = new JsonObject { ["servers"] = new JsonArray(server) };
                break;
            }

            case ProxyProtocol.WireGuard:
            {
                protocol = "wireguard";
                var wg = p.WireGuard!;
                var peer = new JsonObject
                {
                    ["publicKey"] = wg.PeerPublicKey,
                    ["endpoint"] = FormatEndpoint(p.Server, p.Port),
                    ["allowedIPs"] = SingBoxOutboundBuilder.Array(wg.AllowedIps),
                };
                if (!string.IsNullOrEmpty(wg.PreSharedKey))
                {
                    peer["preSharedKey"] = Secret(wg.PreSharedKey, secrets);
                }

                if (wg.PersistentKeepaliveSeconds is { } ka)
                {
                    peer["keepAlive"] = ka;
                }

                settings = new JsonObject
                {
                    ["secretKey"] = Secret(wg.PrivateKey, secrets),
                    ["address"] = SingBoxOutboundBuilder.Array(wg.LocalAddresses),
                    ["peers"] = new JsonArray(peer),
                };
                if (wg.Mtu is { } mtu)
                {
                    settings["mtu"] = mtu;
                }

                if (wg.Reserved.Count == 3)
                {
                    settings["reserved"] = new JsonArray(wg.Reserved.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
                }

                break;
            }

            default:
                throw new NotSupportedException($"Protocol {p.Protocol} cannot be expressed for Xray.");
        }

        var outbound = new JsonObject { ["tag"] = tag, ["protocol"] = protocol, ["settings"] = settings };
        JsonObject? fragmentOutbound = null;

        if (p.Protocol != ProxyProtocol.WireGuard)
        {
            var stream = BuildStream(p, caps, sameDialect, dropped);
            if (p.Tls.Fragment is { Enabled: true } frag && caps.Supports(CoreFeature.TlsFragment) && p.Tls.Enabled)
            {
                var fragTag = tag + "-fragment";
                var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
                sockopt["dialerProxy"] = fragTag;
                stream["sockopt"] = sockopt;
                fragmentOutbound = new JsonObject
                {
                    ["tag"] = fragTag,
                    ["protocol"] = "freedom",
                    ["settings"] = new JsonObject
                    {
                        ["fragment"] = new JsonObject
                        {
                            ["packets"] = frag.Packets ?? "tlshello",
                            ["length"] = frag.Length ?? "100-200",
                            ["interval"] = frag.Interval ?? IntervalFromDelay(frag.FallbackDelay),
                        },
                    },
                };
            }

            outbound["streamSettings"] = stream;
        }

        if (muxEnabled && Capabilities.CapabilityMatrix.MuxApplicable(p, caps))
        {
            outbound["mux"] = new JsonObject
            {
                ["enabled"] = true,
                ["concurrency"] = p.Mux?.XrayConcurrency ?? 8,
                ["xudpConcurrency"] = p.Mux?.XudpConcurrency ?? 16,
                ["xudpProxyUDP443"] = p.Mux?.XudpProxyUdp443 ?? "reject",
            };
        }

        if (sameDialect)
        {
            var ext = p.Extensions?.DeepClone() as JsonObject;
            ext?.Remove("sockopt");
            ExtensionSanitizer.MergeInto(outbound, ext, dropped);
        }

        if (dropped.Count > 0)
        {
            notes.Add($"Ignored unsafe source options for '{p.DisplayName}': {string.Join(", ", dropped.Distinct())}.");
        }

        return (outbound, fragmentOutbound);
    }

    private static JsonObject BuildStream(ProxyProfile p, CoreCapabilities caps, bool sameDialect, List<string> dropped)
    {
        var t = p.Transport;
        var stream = new JsonObject();
        switch (t.Type)
        {
            case TransportType.WebSocket:
            {
                stream["network"] = "ws";
                var ws = new JsonObject { ["path"] = WithEarlyData(t.Path ?? "/", t.MaxEarlyData) };
                if (!string.IsNullOrEmpty(t.Host))
                {
                    ws["host"] = t.Host;
                }

                if (t.Headers.Count > 0)
                {
                    ws["headers"] = Headers(t.Headers);
                }

                MergeExt(ws, t.Extensions, sameDialect, dropped);
                stream["wsSettings"] = ws;
                break;
            }

            case TransportType.Grpc:
            {
                stream["network"] = "grpc";
                // Health checks (seconds): dead HTTP/2 connections are replaced instead of hanging every stream.
                var g = new JsonObject { ["serviceName"] = t.ServiceName ?? string.Empty, ["idle_timeout"] = 15, ["health_check_timeout"] = 10 };
                if (t.GrpcMultiMode)
                {
                    g["multiMode"] = true;
                }

                if (!string.IsNullOrEmpty(t.Host))
                {
                    g["authority"] = t.Host;
                }

                MergeExt(g, t.Extensions, sameDialect, dropped);
                stream["grpcSettings"] = g;
                break;
            }

            case TransportType.HttpUpgrade:
            {
                stream["network"] = "httpupgrade";
                var h = new JsonObject { ["path"] = t.Path ?? "/" };
                if (!string.IsNullOrEmpty(t.Host))
                {
                    h["host"] = t.Host;
                }

                MergeExt(h, t.Extensions, sameDialect, dropped);
                stream["httpupgradeSettings"] = h;
                break;
            }

            case TransportType.XHttp:
            {
                stream["network"] = "xhttp";
                var x = new JsonObject { ["path"] = t.Path ?? "/" };
                if (!string.IsNullOrEmpty(t.Host))
                {
                    x["host"] = t.Host;
                }

                if (!string.IsNullOrEmpty(t.XHttpMode))
                {
                    x["mode"] = t.XHttpMode;
                }

                if (t.Extensions?["xhttp_extra"] is JsonObject extra && ExtensionSanitizer.Sanitize(extra, dropped) is { } cleanExtra)
                {
                    x["extra"] = cleanExtra;
                }

                var rest = t.Extensions?.DeepClone() as JsonObject;
                rest?.Remove("xhttp_extra");
                MergeExt(x, rest, sameDialect, dropped);
                stream["xhttpSettings"] = x;
                break;
            }

            case TransportType.Http2:
            {
                stream["network"] = "http";
                var h = new JsonObject { ["path"] = t.Path ?? "/" };
                if (!string.IsNullOrEmpty(t.Host))
                {
                    h["host"] = SingBoxOutboundBuilder.Array(t.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }

                stream["httpSettings"] = h;
                break;
            }

            case TransportType.Kcp:
            {
                stream["network"] = "kcp";
                var k = new JsonObject();
                if (!string.IsNullOrEmpty(t.HeaderType))
                {
                    k["header"] = new JsonObject { ["type"] = t.HeaderType };
                }

                if (!string.IsNullOrEmpty(t.KcpSeed))
                {
                    k["seed"] = t.KcpSeed;
                }

                MergeExt(k, t.Extensions, sameDialect, dropped);
                stream["kcpSettings"] = k;
                break;
            }

            case TransportType.Quic:
                stream["network"] = "quic";
                break;
            default:
            {
                stream["network"] = "tcp";
                if (string.Equals(t.HeaderType, "http", StringComparison.OrdinalIgnoreCase))
                {
                    var request = new JsonObject { ["path"] = new JsonArray(t.Path ?? "/") };
                    if (!string.IsNullOrEmpty(t.Host))
                    {
                        request["headers"] = new JsonObject { ["Host"] = SingBoxOutboundBuilder.Array(t.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) };
                    }

                    stream["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = request } };
                }

                break;
            }
        }

        var tls = p.Tls;
        if (!tls.Enabled)
        {
            stream["security"] = "none";
        }
        else if (tls.Reality is { Enabled: true } r)
        {
            stream["security"] = "reality";
            var rs = new JsonObject
            {
                ["serverName"] = tls.ServerName ?? string.Empty,
                ["fingerprint"] = string.IsNullOrEmpty(tls.Fingerprint) ? "chrome" : tls.Fingerprint,
                ["publicKey"] = r.PublicKey,
            };
            if (!string.IsNullOrEmpty(r.ShortId))
            {
                rs["shortId"] = r.ShortId;
            }

            if (!string.IsNullOrEmpty(r.SpiderX))
            {
                rs["spiderX"] = r.SpiderX;
            }

            MergeExt(rs, tls.Extensions, sameDialect, dropped);
            stream["realitySettings"] = rs;
        }
        else
        {
            stream["security"] = "tls";
            var ts = new JsonObject();
            if (!string.IsNullOrEmpty(tls.ServerName))
            {
                ts["serverName"] = tls.ServerName;
            }

            if (SingBoxOutboundBuilder.EffectiveAlpn(p) is { Count: > 0 } alpn)
            {
                ts["alpn"] = SingBoxOutboundBuilder.Array(alpn);
            }

            if (!string.IsNullOrEmpty(tls.Fingerprint) && caps.Supports(CoreFeature.Utls))
            {
                ts["fingerprint"] = tls.Fingerprint;
            }

            if (tls.CertificateSha256Pins.Count > 0)
            {
                ts["pinnedPeerCertSha256"] = string.Join(",", tls.CertificateSha256Pins);
            }

            // Xray 26+ removed allowInsecure (negotiation blocks such profiles on Xray); older builds still accept it.
            if (tls.AllowInsecure && caps.Version.Version.Major < 26)
            {
                ts["allowInsecure"] = true;
            }

            if (!string.IsNullOrEmpty(tls.MinVersion))
            {
                ts["minVersion"] = tls.MinVersion;
            }

            if (tls.Ech is { Enabled: true, Config: { Length: > 0 } ech } && caps.Supports(CoreFeature.Ech))
            {
                ts["echConfigList"] = ech;
            }

            MergeExt(ts, tls.Extensions, sameDialect, dropped);
            stream["tlsSettings"] = ts;
        }

        if (sameDialect && p.Extensions?["sockopt"] is JsonObject sockoptExt && ExtensionSanitizer.Sanitize(sockoptExt, dropped) is { } cleanSockopt)
        {
            stream["sockopt"] = cleanSockopt;
        }

        return stream;
    }

    /// <summary>Approximates a sing-box fallback delay (e.g. 20ms) as an Xray interval range ("10-20").</summary>
    public static string IntervalFromDelay(TimeSpan? delay)
    {
        if (delay is not { } d || d <= TimeSpan.Zero)
        {
            return "10-20";
        }

        var hi = Math.Max(1, (int)Math.Round(d.TotalMilliseconds));
        var lo = Math.Max(1, hi / 2);
        return string.Create(CultureInfo.InvariantCulture, $"{lo}-{hi}");
    }

    private static string WithEarlyData(string path, int? maxEarlyData) =>
        maxEarlyData is > 0 && !path.Contains("ed=", StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}ed={maxEarlyData}")
            : path;

    private static string FormatEndpoint(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}" : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";

    private static void MergeExt(JsonObject target, JsonObject? ext, bool sameDialect, List<string> dropped)
    {
        if (sameDialect)
        {
            ExtensionSanitizer.MergeInto(target, ext, dropped);
        }
    }

    private static JsonObject Headers(IReadOnlyDictionary<string, string> headers)
    {
        var h = new JsonObject();
        foreach (var (k, v) in headers.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            h[k] = v;
        }

        return h;
    }

    private static string? Secret(string? value, ICollection<string> secrets)
    {
        if (!string.IsNullOrEmpty(value))
        {
            secrets.Add(value);
        }

        return value;
    }
}
