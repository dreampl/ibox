using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Subscriptions.Parsing.Uris;

/// <summary>
/// Parses single share links: vless, vmess (v2rayN base64 JSON and standard URI forms), trojan,
/// ss (SIP002 + legacy), socks/socks5, http/https, hysteria2/hy2, tuic, wireguard/wg.
/// </summary>
public static class ShareLinkParser
{
    public static readonly IReadOnlySet<string> SupportedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "vmess", "trojan", "ss", "socks", "socks5", "socks4", "http", "https", "hysteria2", "hy2", "tuic", "wireguard", "wg",
    };

    public static bool LooksLikeShareLink(string line)
    {
        var idx = line.IndexOf("://", StringComparison.Ordinal);
        return idx > 0 && idx <= 12 && SupportedSchemes.Contains(line[..idx]);
    }

    /// <summary>Parses one link. Throws <see cref="ProfileParseException"/> on malformed input.</summary>
    public static ProxyProfile Parse(string raw, ParseContext ctx, ConfigFormat format = ConfigFormat.UriList)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(ctx);
        raw = raw.Trim();
        if (raw.Length > ctx.Limits.MaxLineLength)
        {
            throw new ProfileParseException("Link is too long.");
        }

        if (NetText.HasControlChars(raw))
        {
            throw new ProfileParseException("Link contains control characters.");
        }

        var scheme = raw[..Math.Max(0, raw.IndexOf("://", StringComparison.Ordinal))].ToLowerInvariant();
        var profile = scheme switch
        {
            "vless" => ParseVless(ShareLink.Parse(raw)),
            "vmess" => ParseVmess(raw),
            "trojan" => ParseTrojan(ShareLink.Parse(raw)),
            "ss" => ParseShadowsocks(raw),
            "socks" or "socks5" or "socks4" => ParseSocks(ShareLink.Parse(raw)),
            "http" or "https" => ParseHttp(ShareLink.Parse(raw)),
            "hysteria2" or "hy2" => ParseHysteria2(ShareLink.Parse(raw)),
            "tuic" => ParseTuic(ShareLink.Parse(raw)),
            "wireguard" or "wg" => ParseWireGuard(ShareLink.Parse(raw)),
            _ => throw new ProfileParseException($"Unsupported scheme '{ProfileFactory.Truncate(scheme)}'."),
        };

        return ProfileFactory.Finish(profile, ctx, format, raw);
    }

    private static ProxyProfile ParseVless(ShareLink link)
    {
        var uuid = LinkQuery.Decode(link.UserInfo ?? string.Empty).Trim();
        if (uuid.Length == 0)
        {
            throw new ProfileParseException("VLESS link has no user id.");
        }

        var q = link.Query;
        var (transport, tls) = ParseStream(q, defaultSecurity: "none");
        var profile = new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Vless,
            Server = link.RequireHost(),
            Port = link.RequirePort(443),
            Credentials = new ProxyCredentials
            {
                Uuid = uuid,
                Flow = q.Get("flow"),
                Encryption = q.Get("encryption") ?? "none",
                PacketEncoding = q.Get("packetEncoding", "packet_encoding"),
            },
            Transport = transport,
            Tls = tls,
            Mux = ParseMux(q),
        };

        return profile with { Extensions = q.UnconsumedAsExtensions() };
    }

    private static ProxyProfile ParseTrojan(ShareLink link)
    {
        var password = LinkQuery.Decode(link.UserInfo ?? string.Empty);
        if (password.Length == 0)
        {
            throw new ProfileParseException("Trojan link has no password.");
        }

        var q = link.Query;
        var (transport, tls) = ParseStream(q, defaultSecurity: "tls");
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Trojan,
            Server = link.RequireHost(),
            Port = link.RequirePort(443),
            Credentials = new ProxyCredentials { Password = password, Flow = q.Get("flow") },
            Transport = transport,
            Tls = tls,
            Mux = ParseMux(q),
            Extensions = q.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseVmess(string raw)
    {
        var body = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hash = body.IndexOf('#', StringComparison.Ordinal);
        var bodyNoFragment = hash >= 0 ? body[..hash] : body;

        if (!bodyNoFragment.Contains('@', StringComparison.Ordinal)
            && Base64Text.TryDecodeToString(bodyNoFragment, 64 * 1024, out var json)
            && json.TrimStart().StartsWith('{'))
        {
            return ParseVmessJson(json, hash >= 0 ? LinkQuery.Decode(body[(hash + 1)..]) : null);
        }

        // Standard URI form: vmess://uuid@host:port?type=ws&security=tls...
        var link = ShareLink.Parse(raw);
        var q = link.Query;
        var (transport, tls) = ParseStream(q, defaultSecurity: "none");
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Vmess,
            Server = link.RequireHost(),
            Port = link.RequirePort(),
            Credentials = new ProxyCredentials
            {
                Uuid = LinkQuery.Decode(link.UserInfo ?? string.Empty),
                Encryption = q.Get("encryption", "scy", "security_method") ?? "auto",
                AlterId = q.GetInt("aid", "alterId") ?? 0,
            },
            Transport = transport,
            Tls = tls,
            Mux = ParseMux(q),
            Extensions = q.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseVmessJson(string json, string? fragmentName)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonDefaults.UntrustedDocument);
        }
        catch (JsonException)
        {
            throw new ProfileParseException("VMess payload is not valid JSON.");
        }

        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object)
            {
                throw new ProfileParseException("VMess payload is not a JSON object.");
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "v", "ps", "add", "port", "id", "aid", "scy", "net", "type", "host", "path", "tls", "sni", "alpn", "fp", "allowInsecure", "insecure", "skip-cert-verify",
            };

            var net = Str(o, "net");
            var type = ProfileFactory.ParseTransport(net);
            var headerType = Str(o, "type");
            var host = Str(o, "host");
            var path = Str(o, "path");
            var transport = type switch
            {
                TransportType.Grpc => new TransportOptions { Type = type, ServiceName = path?.Trim('/'), GrpcMultiMode = headerType == "multi" },
                TransportType.Kcp => new TransportOptions { Type = type, HeaderType = headerType, KcpSeed = path },
                TransportType.Quic => new TransportOptions { Type = type, HeaderType = headerType, Host = host, Path = path },
                _ => new TransportOptions { Type = type, Host = host, Path = path, HeaderType = headerType is "http" ? "http" : null },
            };

            var security = Str(o, "tls")?.ToLowerInvariant();
            var tls = security is "tls" or "xtls"
                ? new TlsOptions
                {
                    Enabled = true,
                    ServerName = Str(o, "sni"),
                    Alpn = ProfileFactory.SplitList(Str(o, "alpn")),
                    Fingerprint = Str(o, "fp"),
                    AllowInsecure = Bool(o, "allowInsecure") || Bool(o, "insecure") || Bool(o, "skip-cert-verify"),
                }
                : new TlsOptions();

            var ext = new JsonObject();
            foreach (var prop in o.EnumerateObject())
            {
                if (!known.Contains(prop.Name))
                {
                    ext[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                }
            }

            var name = Str(o, "ps");
            return new ProxyProfile
            {
                Id = string.Empty,
                Name = string.IsNullOrWhiteSpace(name) ? fragmentName ?? string.Empty : name,
                Protocol = ProxyProtocol.Vmess,
                Server = Str(o, "add") ?? throw new ProfileParseException("VMess payload has no address."),
                Port = Int(o, "port") ?? throw new ProfileParseException("VMess payload has no port."),
                Credentials = new ProxyCredentials
                {
                    Uuid = Str(o, "id") ?? throw new ProfileParseException("VMess payload has no id."),
                    AlterId = Int(o, "aid") ?? 0,
                    Encryption = Str(o, "scy") ?? "auto",
                },
                Transport = transport,
                Tls = tls,
                Extensions = ext.Count == 0 ? null : new JsonObject { ["vmess_json"] = ext },
            };
        }
    }

    private static ProxyProfile ParseShadowsocks(string raw)
    {
        var body = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hash = body.IndexOf('#', StringComparison.Ordinal);
        var name = hash >= 0 ? LinkQuery.Decode(body[(hash + 1)..]) : null;
        var main = hash >= 0 ? body[..hash] : body;

        if (!main.Contains('@', StringComparison.Ordinal))
        {
            // Legacy: ss://BASE64(method:password@host:port)
            var qIdx = main.IndexOf('?', StringComparison.Ordinal);
            var b64 = qIdx >= 0 ? main[..qIdx] : main;
            if (!Base64Text.TryDecodeToString(b64.TrimEnd('/'), 16 * 1024, out var decoded))
            {
                throw new ProfileParseException("Shadowsocks legacy link is not valid Base64.");
            }

            var rebuilt = "ss://" + decoded + (qIdx >= 0 ? main[qIdx..] : string.Empty) + (hash >= 0 ? "#" + body[(hash + 1)..] : string.Empty);
            var legacy = ShareLink.Parse(rebuilt);
            return BuildShadowsocks(legacy, ParseUserInfoPlain(legacy.UserInfo), name);
        }

        var link = ShareLink.Parse(raw);
        var userInfo = link.UserInfo ?? string.Empty;
        (string Method, string Password) creds;
        var decodedUser = LinkQuery.Decode(userInfo);
        if (decodedUser.Contains(':', StringComparison.Ordinal) && !Base64Text.LooksLikeBase64(decodedUser))
        {
            creds = ParseUserInfoPlain(decodedUser);
        }
        else if (Base64Text.TryDecodeToString(decodedUser, 4096, out var dec) && dec.Contains(':', StringComparison.Ordinal))
        {
            creds = ParseUserInfoPlain(dec);
        }
        else
        {
            creds = ParseUserInfoPlain(decodedUser);
        }

        return BuildShadowsocks(link, creds, name);
    }

    private static (string Method, string Password) ParseUserInfoPlain(string? userInfo)
    {
        var s = LinkQuery.Decode(userInfo ?? string.Empty);
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new ProfileParseException("Shadowsocks credentials must be 'method:password'.");
        }

        return (s[..colon].Trim().ToLowerInvariant(), s[(colon + 1)..]);
    }

    private static ProxyProfile BuildShadowsocks(ShareLink link, (string Method, string Password) creds, string? name)
    {
        var q = link.Query;
        string? plugin = null;
        string? pluginOpts = null;
        var pluginRaw = q.Get("plugin");
        if (!string.IsNullOrEmpty(pluginRaw))
        {
            var semi = pluginRaw.IndexOf(';', StringComparison.Ordinal);
            plugin = semi >= 0 ? pluginRaw[..semi] : pluginRaw;
            pluginOpts = semi >= 0 ? pluginRaw[(semi + 1)..] : null;
        }

        return new ProxyProfile
        {
            Id = string.Empty,
            Name = name ?? link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Shadowsocks,
            Server = link.RequireHost(),
            Port = link.RequirePort(),
            Credentials = new ProxyCredentials { Password = creds.Password },
            Shadowsocks = new ShadowsocksOptions
            {
                Method = creds.Method,
                Plugin = plugin,
                PluginOptions = pluginOpts,
                UdpOverTcp = q.GetBool("uot", "udp-over-tcp", "udp_over_tcp"),
            },
            Extensions = q.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseSocks(ShareLink link)
    {
        var (user, pass) = SplitUserPass(link.UserInfo, allowBase64: true);
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Socks,
            Server = link.RequireHost(),
            Port = link.RequirePort(1080),
            Credentials = new ProxyCredentials { Username = user, Password = pass },
            Extensions = link.Query.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseHttp(ShareLink link)
    {
        var (user, pass) = SplitUserPass(link.UserInfo, allowBase64: true);
        var tls = link.Scheme == "https";
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Http,
            Server = link.RequireHost(),
            Port = link.RequirePort(tls ? 443 : 80),
            Credentials = new ProxyCredentials { Username = user, Password = pass },
            Tls = tls ? new TlsOptions { Enabled = true, ServerName = link.Query.Get("sni"), AllowInsecure = link.Query.GetBool("allowInsecure", "insecure") } : new TlsOptions(),
            Extensions = link.Query.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseHysteria2(ShareLink link)
    {
        var q = link.Query;
        var auth = LinkQuery.Decode(link.UserInfo ?? string.Empty);
        var ports = new List<string>();
        if (link.PortText is { } pt && (pt.Contains(',', StringComparison.Ordinal) || pt.Contains('-', StringComparison.Ordinal)))
        {
            ports.AddRange(pt.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Replace('-', ':')));
        }

        if (q.Get("mport", "ports") is { } mport)
        {
            ports.AddRange(mport.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Replace('-', ':')));
        }

        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Hysteria2,
            Server = link.RequireHost(),
            Port = link.RequirePort(443),
            Credentials = new ProxyCredentials { Password = auth },
            Tls = new TlsOptions
            {
                Enabled = true,
                ServerName = q.Get("sni", "peer"),
                AllowInsecure = q.GetBool("insecure", "allowInsecure", "allow_insecure"),
                Alpn = q.GetList("alpn"),
                PublicKeyPins = q.Get("pinSHA256", "pinsha256") is { } pin ? [pin] : [],
            },
            Hysteria2 = new Hysteria2Options
            {
                ObfsType = q.Get("obfs"),
                ObfsPassword = q.Get("obfs-password", "obfs_password", "obfsParam"),
                UpMbps = ParseMbps(q.Get("upmbps", "up")),
                DownMbps = ParseMbps(q.Get("downmbps", "down")),
                ServerPorts = ports.Where(p => p.Contains(':', StringComparison.Ordinal)).Distinct().ToArray(),
                HopInterval = ProfileFactory.ParseDuration(q.Get("hopInterval", "hop_interval")),
            },
            Extensions = q.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseTuic(ShareLink link)
    {
        var q = link.Query;
        var (uuid, password) = SplitUserPass(link.UserInfo, allowBase64: false);
        if (string.IsNullOrEmpty(uuid))
        {
            throw new ProfileParseException("TUIC link has no UUID.");
        }

        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.Tuic,
            Server = link.RequireHost(),
            Port = link.RequirePort(443),
            Credentials = new ProxyCredentials { Uuid = uuid, Password = password },
            Tls = new TlsOptions
            {
                Enabled = true,
                ServerName = q.Get("sni", "peer"),
                Alpn = q.GetList("alpn"),
                AllowInsecure = q.GetBool("allow_insecure", "allowInsecure", "insecure"),
                DisableSni = q.GetBool("disable_sni"),
            },
            Tuic = new TuicOptions
            {
                CongestionControl = q.Get("congestion_control", "congestion-control", "cc"),
                UdpRelayMode = q.Get("udp_relay_mode", "udp-relay-mode"),
                ZeroRttHandshake = q.GetBool("zero_rtt_handshake", "reduce_rtt", "zero-rtt"),
                Heartbeat = ProfileFactory.ParseDuration(q.Get("heartbeat")),
            },
            Extensions = q.UnconsumedAsExtensions(),
        };
    }

    private static ProxyProfile ParseWireGuard(ShareLink link)
    {
        var q = link.Query;
        var privateKey = LinkQuery.Decode(link.UserInfo ?? string.Empty).Trim();
        var reserved = q.GetList("reserved")
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : -1)
            .ToArray();
        return new ProxyProfile
        {
            Id = string.Empty,
            Name = link.Fragment ?? string.Empty,
            Protocol = ProxyProtocol.WireGuard,
            Server = link.RequireHost(),
            Port = link.RequirePort(51820),
            WireGuard = new WireGuardOptions
            {
                PrivateKey = privateKey,
                PeerPublicKey = q.Get("publickey", "public_key", "peer_public_key", "publicKey") ?? string.Empty,
                PreSharedKey = q.Get("presharedkey", "pre_shared_key", "psk", "preSharedKey"),
                LocalAddresses = q.GetList("address", "ip", "addresses").Select(NormalizeCidr).ToArray(),
                Reserved = reserved,
                Mtu = q.GetInt("mtu"),
                PersistentKeepaliveSeconds = q.GetInt("keepalive", "persistent_keepalive"),
            },
            Extensions = q.UnconsumedAsExtensions(),
        };
    }

    private static string NormalizeCidr(string address) =>
        address.Contains('/', StringComparison.Ordinal) ? address : address + (address.Contains(':', StringComparison.Ordinal) ? "/128" : "/32");

    /// <summary>Stream settings shared by VLESS/Trojan/VMess(URI): transport + TLS/Reality.</summary>
    internal static (TransportOptions Transport, TlsOptions Tls) ParseStream(LinkQuery q, string defaultSecurity)
    {
        var type = ProfileFactory.ParseTransport(q.Get("type", "net", "network"));
        var headerType = q.Get("headerType", "header");
        var mode = q.Get("mode");
        var path = q.Get("path");
        var transportExt = new JsonObject();
        if (type == TransportType.XHttp && q.Get("extra") is { } extra)
        {
            try
            {
                transportExt["xhttp_extra"] = JsonNode.Parse(extra, JsonDefaults.NodeOptions, JsonDefaults.UntrustedDocument);
            }
            catch (JsonException)
            {
                transportExt["xhttp_extra_raw"] = extra;
            }
        }

        var transport = new TransportOptions
        {
            Type = type,
            Host = q.Get("host"),
            Path = type == TransportType.Grpc ? null : path,
            ServiceName = type == TransportType.Grpc ? q.Get("serviceName", "service_name") ?? path?.Trim('/') : null,
            GrpcMultiMode = type == TransportType.Grpc && string.Equals(mode, "multi", StringComparison.OrdinalIgnoreCase),
            XHttpMode = type == TransportType.XHttp ? mode : null,
            HeaderType = type is TransportType.Tcp or TransportType.Kcp or TransportType.Quic ? headerType : null,
            KcpSeed = type == TransportType.Kcp ? q.Get("seed") : null,
            MaxEarlyData = q.GetInt("ed", "maxEarlyData"),
            EarlyDataHeaderName = q.Get("eh", "earlyDataHeaderName"),
            Extensions = transportExt.Count == 0 ? null : transportExt,
        };

        var security = (q.Get("security") ?? defaultSecurity).ToLowerInvariant();
        var fragment = ParseFragment(q.Get("fragment"));
        var tls = security switch
        {
            "none" or "" => new TlsOptions(),
            "tls" or "xtls" => new TlsOptions
            {
                Enabled = true,
                ServerName = q.Get("sni", "peer", "serverName"),
                Alpn = q.GetList("alpn"),
                Fingerprint = q.Get("fp", "fingerprint"),
                AllowInsecure = q.GetBool("allowInsecure", "insecure", "allow_insecure", "skip-cert-verify"),
                Fragment = fragment,
                Ech = q.Get("ech") is { } ech ? new EchOptions { Enabled = true, Config = ech } : null,
            },
            "reality" => new TlsOptions
            {
                Enabled = true,
                ServerName = q.Get("sni", "peer", "serverName"),
                Alpn = q.GetList("alpn"),
                Fingerprint = q.Get("fp", "fingerprint") ?? "chrome",
                Reality = new RealityOptions
                {
                    Enabled = true,
                    PublicKey = q.Get("pbk", "publicKey", "public_key"),
                    ShortId = q.Get("sid", "shortId", "short_id"),
                    SpiderX = q.Get("spx", "spiderX"),
                },
                Fragment = fragment,
            },
            _ => throw new ProfileParseException($"Unsupported security '{ProfileFactory.Truncate(security)}'."),
        };

        return (transport, tls);
    }

    /// <summary>
    /// Accepts <c>fragment=1</c>/<c>true</c> (enable with core defaults) or a comma list containing
    /// <c>tlshello</c> / packet range, length range and interval range (common client formats).
    /// </summary>
    internal static TlsFragmentOptions? ParseFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new TlsFragmentOptions { Enabled = true };
        }

        var tokens = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string? packets = null;
        var tlsHello = tokens.FindIndex(t => t.Equals("tlshello", StringComparison.OrdinalIgnoreCase));
        if (tlsHello >= 0)
        {
            packets = "tlshello";
            tokens.RemoveAt(tlsHello);
        }

        return new TlsFragmentOptions
        {
            Enabled = true,
            Packets = packets,
            Length = tokens.Count > 0 ? tokens[0] : null,
            Interval = tokens.Count > 1 ? tokens[1] : null,
        };
    }

    private static MuxOptions? ParseMux(LinkQuery q)
    {
        var mux = q.Get("mux");
        if (mux is null)
        {
            return null;
        }

        var enabled = mux == "1" || mux.Equals("true", StringComparison.OrdinalIgnoreCase);
        return new MuxOptions
        {
            Mode = enabled ? MuxMode.On : MuxMode.Off,
            XrayConcurrency = q.GetInt("concurrency", "muxConcurrency"),
        };
    }

    private static (string? User, string? Pass) SplitUserPass(string? userInfo, bool allowBase64)
    {
        if (string.IsNullOrEmpty(userInfo))
        {
            return (null, null);
        }

        var decoded = LinkQuery.Decode(userInfo);
        if (allowBase64 && !decoded.Contains(':', StringComparison.Ordinal) && Base64Text.TryDecodeToString(decoded, 4096, out var b64) && b64.Contains(':', StringComparison.Ordinal))
        {
            decoded = b64;
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? (decoded[..colon], decoded[(colon + 1)..]) : (decoded, null);
    }

    private static int? ParseMbps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && (v.GetString() is "1" or "true"))
            || (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i != 0));
}
