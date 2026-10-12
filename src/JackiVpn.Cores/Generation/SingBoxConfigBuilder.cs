using System.Text.Json.Nodes;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using JackiVpn.Routing;

namespace JackiVpn.Cores.Generation;

/// <summary>
/// Builds complete sing-box (≥1.12 schema) configurations for every <see cref="SessionKind"/>.
/// Tags: outbound <c>proxy</c> / <c>direct</c>; DNS servers <c>dns-remote</c> (through the tunnel),
/// <c>dns-direct</c> (direct-routed names), <c>dns-bootstrap</c> (proxy server host names only).
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string TunInboundTag = "tun-in";
    public const string MixedInboundTag = "mixed-in";
    public const string DnsRemote = "dns-remote";
    public const string DnsDirect = "dns-direct";
    public const string DnsBootstrap = "dns-bootstrap";

    /// <summary>Services that sign the user out when they see a request from an unsupported region.</summary>
    public static readonly string[] AlwaysTunneledDomains = ["anthropic.com", "claude.ai", "claude.com", "claudeusercontent.com", "clau.de"];
    public const string DnsFake = "dns-fakeip";

    public static (JsonObject Config, List<string> Secrets, List<string> Notes, List<int> Ports) Build(CoreSessionPlan plan, CoreCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(caps);
        if (plan.Profiles.Count == 0)
        {
            throw new ArgumentException("A session plan needs at least one profile.", nameof(plan));
        }

        var secrets = new List<string>();
        var notes = new List<string>();
        var ports = new List<int>();
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = plan.DebugLogging ? "debug" : "warn", ["timestamp"] = true },
        };

        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var routeRules = new JsonArray();

        if (plan.Kind == SessionKind.ProbeBatch)
        {
            BuildProbeBatch(plan, caps, inbounds, outbounds, endpoints, routeRules, secrets, notes, ports);
            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });
            root["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(DnsServer(plan.Dns.Bootstrap, DnsBootstrap, detour: null)),
                ["final"] = DnsBootstrap,
                ["strategy"] = "prefer_ipv4",
            };
            root["inbounds"] = inbounds;
            root["outbounds"] = outbounds;
            if (endpoints.Count > 0)
            {
                root["endpoints"] = endpoints;
            }

            root["route"] = new JsonObject
            {
                ["rules"] = routeRules,
                ["final"] = DirectTag,
                ["default_domain_resolver"] = new JsonObject { ["server"] = DnsBootstrap },
            };
            AddClashApi(root, plan, caps);
            return (root, secrets, notes, ports);
        }

        // Proxy outbound: either the real protocol, or a SOCKS hop into the Xray backend (chain mode).
        var profile = plan.Profiles[0];
        if (plan.ChainBackend is { } chain)
        {
            var hop = new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = ProxyTag,
                ["server"] = "127.0.0.1",
                ["server_port"] = chain.SocksPort,
                ["version"] = "5",
            };
            if (chain.Auth is { } hopAuth)
            {
                hop["username"] = hopAuth.Username;
                hop["password"] = hopAuth.Password;
                secrets.Add(hopAuth.Password);
            }

            outbounds.Add(hop);
        }
        else
        {
            var built = SingBoxOutboundBuilder.Build(profile, ProxyTag, caps, plan.MuxEnabled, secrets, notes);
            (built.IsEndpoint ? endpoints : outbounds).Add(built.Node);
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });
        var traffic = plan.Traffic ?? new TrafficPolicy();
        var shaperEndpoint = plan.Shaper;
        var shapeClasses = shaperEndpoint is not null && traffic.Shaping ? ShapeClassesOf(traffic) : [];
        if (shapeClasses.Count > 0)
        {
            // One SOCKS hop per class into the relay (the user name tells it the class); it returns the
            // connection through the back inbound, where the normal rules route it.
            foreach (var c in shapeClasses)
            {
                outbounds.Add(new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = ShapeTag(c),
                    ["server"] = "127.0.0.1",
                    ["server_port"] = shaperEndpoint!.Port,
                    ["version"] = "5",
                    ["username"] = c,
                    ["password"] = shaperEndpoint.Password,
                    ["network"] = "tcp",
                });
            }

            secrets.Add(shaperEndpoint!.Password);
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = BackInboundTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = shaperEndpoint.BackPort,
                ["users"] = new JsonArray(new JsonObject { ["username"] = shaperEndpoint.BackAuth.Username, ["password"] = shaperEndpoint.BackAuth.Password }),
            });
            secrets.Add(shaperEndpoint.BackAuth.Password);
            ports.Add(shaperEndpoint.BackPort);
        }

        // Inbound.
        if (plan.Kind == SessionKind.Tun)
        {
            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = TunInboundTag,
                ["interface_name"] = plan.Tun.InterfaceName,
                ["address"] = SingBoxOutboundBuilder.Array([plan.Tun.AddressV4, plan.Tun.AddressV6]),
                ["mtu"] = plan.Tun.Mtu,
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = plan.TunStack switch
                {
                    TunStack.System => "system",
                    TunStack.Gvisor => "gvisor",
                    _ => "mixed",
                },
            };
            var exclude = new List<string>(plan.Tun.RouteExcludeAddresses);
            if (plan.Routing.BypassLan)
            {
                exclude.AddRange(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"]);
            }

            // Workplace subnets never enter the TUN (file shares, RDP and intranet keep their own path).
            exclude.AddRange(traffic.InternalCidrs.Where(NetText.IsCidr));

            if (exclude.Count > 0)
            {
                tun["route_exclude_address"] = SingBoxOutboundBuilder.Array(exclude.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            inbounds.Add(tun);
            if (plan.TunProxyPort > 0)
            {
                // Also a loopback proxy for the system proxy setting: some apps (Claude) check it and behave
                // differently without one, even though the TUN already carries their traffic.
                inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = MixedInboundTag, ["listen"] = "127.0.0.1", ["listen_port"] = plan.TunProxyPort });
                ports.Add(plan.TunProxyPort);
            }
        }
        else
        {
            var mixed = new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = MixedInboundTag,
                ["listen"] = plan.ListenOnLan ? "0.0.0.0" : "127.0.0.1",
                ["listen_port"] = plan.ListenPort,
            };
            if (plan.InboundAuth is { } auth)
            {
                mixed["users"] = new JsonArray(new JsonObject { ["username"] = auth.Username, ["password"] = auth.Password });
                secrets.Add(auth.Password);
            }

            inbounds.Add(mixed);
            ports.Add(plan.ListenPort);
        }

        // Route.
        var ipv6 = plan.Ipv6Allowed;
        if (plan.Routing.Sniff)
        {
            routeRules.Add(new JsonObject { ["action"] = "sniff" });
        }

        // Our own cores (sing-box, the Xray backend) always go direct, and before DNS hijacking: otherwise the
        // backend's DNS lookups would get FakeIP answers and its server connection would loop into the tunnel.
        var directProcesses = plan.DirectProcessPaths.ToList();
        if (plan.ChainBackend is { } backend)
        {
            directProcesses.Add(backend.BackendExecutablePath);
        }

        if (plan.Kind == SessionKind.Tun && directProcesses.Count > 0)
        {
            routeRules.Add(new JsonObject
            {
                ["process_path"] = SingBoxOutboundBuilder.Array(directProcesses.Distinct(StringComparer.OrdinalIgnoreCase)),
                ["outbound"] = DirectTag,
            });
        }

        routeRules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        if (plan.CapturesAllTraffic)
        {
            routeRules.Add(new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" });
        }

        AddBlockRules(routeRules, traffic);
        if (Clean(traffic.InternalDomains) is { Count: > 0 } internalDomains)
        {
            routeRules.Add(new JsonObject { ["domain_suffix"] = SingBoxOutboundBuilder.Array(internalDomains), ["outbound"] = DirectTag });
        }

        if (traffic.InternalCidrs.Select(NetText.NormalizeCidr).OfType<string>().Distinct().ToList() is { Count: > 0 } internalCidrs)
        {
            routeRules.Add(new JsonObject { ["ip_cidr"] = SingBoxOutboundBuilder.Array(internalCidrs), ["outbound"] = DirectTag });
        }

        if (plan.Routing.Final != RuleActionType.Direct)
        {
            // Region-locked services (Claude/Anthropic) must never leave from the local network: one request
            // through a direct rule or a poisoned local DNS answer reports an unsupported region and signs the
            // app out. Ahead of the LAN bypass and the user's rules.
            routeRules.Add(new JsonObject { ["domain_suffix"] = SingBoxOutboundBuilder.Array(AlwaysTunneledDomains), ["outbound"] = ProxyTag });
        }

        if (plan.Routing.BypassLan)
        {
            routeRules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag });
        }

        if (shapeClasses.Count > 0)
        {
            AddShapingRules(routeRules, traffic, plan.Kind == SessionKind.Tun ? (plan.TunProxyPort > 0 ? [TunInboundTag, MixedInboundTag] : [TunInboundTag]) : [MixedInboundTag], plan.Routing);
        }

        if (!ipv6)
        {
            // No IPv6 path through the tunnel: refuse v6 destinations inside the core (apps fall back to v4).
            routeRules.Add(new JsonObject { ["ip_version"] = 6, ["action"] = "reject" });
        }

        var ruleSetsUsed = new HashSet<string>(StringComparer.Ordinal);
        var resolved = false;
        foreach (var rule in plan.Routing.Rules.Where(r => r.Enabled))
        {
            var translated = TranslateRule(rule, plan.RuleSetPaths, ruleSetsUsed, notes);
            if (translated is null)
            {
                continue;
            }

            if (!resolved && MatchesAddresses(translated))
            {
                // IP rules (ip, geoip, private, IP rule-sets) only see addresses, but browsers behind the system
                // proxy, sniffed TUN connections and fake-IP answers all arrive as names: without a lookup here
                // "Iranian IPs direct" never matched them. Looked up with the session's own resolver (through the
                // tunnel unless DNS is set to System/Local), so nothing leaks; rules above keep their names.
                routeRules.Add(new JsonObject
                {
                    ["action"] = "resolve",
                    ["server"] = plan.Dns.Mode is DnsMode.System or DnsMode.Local ? DnsDirect : DnsRemote,
                    ["strategy"] = ipv6 ? "prefer_ipv4" : "ipv4_only",
                });
                resolved = true;
            }

            routeRules.Add(translated);
        }

        if (plan.BlockQuic && plan.Routing.Final != RuleActionType.Direct)
        {
            // After the user's rules (Direct QUIC keeps working): HTTP/3 carried over a TCP tunnel suffers
            // head-of-line blocking; an immediate ICMP reject makes browsers use TCP right away.
            routeRules.Add(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
        }

        var route = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = plan.Routing.Final == RuleActionType.Direct ? DirectTag : ProxyTag,
            ["default_domain_resolver"] = new JsonObject { ["server"] = DnsBootstrap },
        };
        if (plan.Kind == SessionKind.Tun)
        {
            route["auto_detect_interface"] = true;
        }

        if (traffic.TrackProcesses && !plan.ExternalTun)
        {
            // Per-app usage: the core looks up which program owns each connection (Windows/Linux only).
            route["find_process"] = true;
        }

        var dnsSection = BuildDns(plan, ipv6, ruleSetsUsed, directProcesses, traffic);
        if (ruleSetsUsed.Count > 0)
        {
            route["rule_set"] = new JsonArray(ruleSetsUsed.OrderBy(t => t, StringComparer.Ordinal).Select(tag => (JsonNode?)new JsonObject
            {
                ["type"] = "local",
                ["tag"] = tag,
                ["format"] = plan.RuleSetPaths[tag].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "source" : "binary",
                ["path"] = plan.RuleSetPaths[tag],
            }).ToArray());
        }

        root["dns"] = dnsSection;
        root["inbounds"] = inbounds;
        root["outbounds"] = outbounds;
        if (endpoints.Count > 0)
        {
            root["endpoints"] = endpoints;
        }

        root["route"] = route;
        AddClashApi(root, plan, caps);
        if (UsesFakeIp(plan))
        {
            // Keep the fake-IP ↔ domain mapping across core restarts, so apps holding cached fake
            // addresses keep working after a crash recovery.
            var experimental = root["experimental"] as JsonObject ?? new JsonObject();
            experimental["cache_file"] = new JsonObject { ["enabled"] = true, ["store_fakeip"] = true };
            root["experimental"] = experimental;
        }

        return (root, secrets, notes, ports);
    }

    /// <summary>FakeIP only applies to TUN sessions with the tunnel resolver as final DNS.</summary>
    public static bool UsesFakeIp(CoreSessionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.CapturesAllTraffic && plan.Dns.FakeIpEnabled && plan.Dns.Mode is not (DnsMode.System or DnsMode.Local);
    }

    private static void BuildProbeBatch(CoreSessionPlan plan, CoreCapabilities caps, JsonArray inbounds, JsonArray outbounds, JsonArray endpoints, JsonArray rules, List<string> secrets, List<string> notes, List<int> ports)
    {
        if (plan.ProbePorts.Count != plan.Profiles.Count)
        {
            throw new ArgumentException("Probe batch needs exactly one port per profile.", nameof(plan));
        }

        for (var i = 0; i < plan.Profiles.Count; i++)
        {
            var inTag = $"probe-in-{i}";
            var outTag = $"probe-out-{i}";
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = inTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = plan.ProbePorts[i],
            });
            ports.Add(plan.ProbePorts[i]);
            var built = SingBoxOutboundBuilder.Build(plan.Profiles[i], outTag, caps, plan.MuxEnabled, secrets, notes);
            (built.IsEndpoint ? endpoints : outbounds).Add(built.Node);
            rules.Add(new JsonObject { ["inbound"] = inTag, ["outbound"] = outTag });
        }
    }

    private static JsonObject BuildDns(CoreSessionPlan plan, bool ipv6, HashSet<string> ruleSetsUsed, IReadOnlyList<string> directProcesses, TrafficPolicy traffic)
    {
        var dns = plan.Dns;
        var servers = new JsonArray();
        var rules = new JsonArray();
        string final;

        switch (dns.Mode)
        {
            case DnsMode.System:
                servers.Add(DnsServer(DnsServerEndpoint.SystemLocal, DnsDirect, detour: null));
                final = DnsDirect;
                break;
            case DnsMode.Local:
                servers.Add(DnsServer(dns.Direct, DnsDirect, detour: null));
                final = DnsDirect;
                break;
            default:
                // AutoSecure / DoH / DoT / RemoteThroughVpn / Custom: remote resolver reached through the proxy.
                servers.Add(DnsServer(dns.Remote, DnsRemote, detour: ProxyTag));
                servers.Add(DnsServer(dns.Direct, DnsDirect, detour: null));
                final = DnsRemote;
                break;
        }

        servers.Add(DnsServer(dns.Bootstrap, DnsBootstrap, detour: null));

        if (directProcesses.Count > 0 && plan.Kind == SessionKind.Tun)
        {
            // Backend cores resolve their own server names outside the tunnel (no resolution loop).
            rules.Add(new JsonObject
            {
                ["process_path"] = SingBoxOutboundBuilder.Array(directProcesses.Distinct(StringComparer.OrdinalIgnoreCase)),
                ["action"] = "route",
                ["server"] = DnsBootstrap,
            });
        }

        if (Clean(traffic.InternalDomains) is { Count: > 0 } internalNames)
        {
            // Workplace names are answered by the company's own resolver (or the network's), never through the tunnel.
            var internalServer = DnsDirect;
            if (traffic.InternalDns.FirstOrDefault(NetText.IsIpLiteral) is { } companyDns)
            {
                servers.Add(new JsonObject { ["type"] = "udp", ["tag"] = DnsInternal, ["server"] = NetText.TrimBrackets(companyDns) });
                internalServer = DnsInternal;
            }
            else if (dns.Mode is not (DnsMode.System or DnsMode.Local) && dns.Direct.Transport != DnsTransport.Local)
            {
                servers.Add(DnsServer(DnsServerEndpoint.SystemLocal, DnsInternal, detour: null));
                internalServer = DnsInternal;
            }

            rules.Add(new JsonObject { ["domain_suffix"] = SingBoxOutboundBuilder.Array(internalNames), ["action"] = "route", ["server"] = internalServer });
        }

        if (!ipv6)
        {
            // The tunnel carries no IPv6, but Windows sees the TUN's IPv6 address and reports IPv6 as available.
            // Real AAAA answers would make apps without happy-eyeballs fallback (e.g. some Node/Bun clients such
            // as Claude Code) dial IPv6 first and fail; an empty AAAA answer makes every app use IPv4 directly.
            rules.Add(new JsonObject { ["query_type"] = SingBoxOutboundBuilder.Array(["AAAA"]), ["action"] = "predefined", ["rcode"] = "NOERROR" });
        }

        if (final == DnsRemote && plan.Routing.Final != RuleActionType.Direct)
        {
            // Never resolved by the local network's resolver (see AlwaysTunneledDomains), even if a user rule
            // routes a broader name set Direct: fake-IP (resolved on the server) or the tunnel's resolver.
            var tunneled = new JsonObject { ["domain_suffix"] = SingBoxOutboundBuilder.Array(AlwaysTunneledDomains), ["action"] = "route", ["server"] = DnsRemote };
            if (UsesFakeIp(plan))
            {
                tunneled["query_type"] = SingBoxOutboundBuilder.Array(ipv6 ? new[] { "A", "AAAA" } : new[] { "A" });
                tunneled["server"] = DnsFake;
            }

            rules.Add(tunneled);
        }

        if (final == DnsRemote)
        {
            // Names that are routed Direct are also resolved directly (correct geo-answers, no tunnel hairpin).
            foreach (var rule in plan.Routing.Rules.Where(r => r.Enabled && !r.Invert))
            {
                var dnsRule = DomainOnlyDnsRule(rule, plan.RuleSetPaths, ruleSetsUsed);
                if (dnsRule is null)
                {
                    continue;
                }

                switch (rule.Action.Type)
                {
                    case RuleActionType.Direct:
                        dnsRule["action"] = "route";
                        dnsRule["server"] = DnsDirect;
                        rules.Add(dnsRule);
                        break;
                    case RuleActionType.Block:
                        dnsRule["action"] = "reject";
                        rules.Add(dnsRule);
                        break;
                }
            }

            if (UsesFakeIp(plan))
            {
                servers.Add(new JsonObject
                {
                    ["type"] = "fakeip",
                    ["tag"] = DnsFake,
                    ["inet4_range"] = dns.FakeIpV4Range,
                    ["inet6_range"] = dns.FakeIpV6Range,
                });
                rules.Add(new JsonObject
                {
                    ["query_type"] = SingBoxOutboundBuilder.Array(ipv6 ? new[] { "A", "AAAA" } : new[] { "A" }),
                    ["action"] = "route",
                    ["server"] = DnsFake,
                });
            }
        }

        var result = new JsonObject
        {
            ["servers"] = servers,
            ["final"] = final,
            ["strategy"] = ipv6 ? "prefer_ipv4" : "ipv4_only",
        };
        if (rules.Count > 0)
        {
            result["rules"] = rules;
        }

        if (!dns.CacheEnabled)
        {
            result["disable_cache"] = true;
        }

        return result;
    }

    /// <summary>Builds a sing-box ≥1.12 typed DNS server.</summary>
    public static JsonObject DnsServer(DnsServerEndpoint ep, string tag, string? detour)
    {
        ArgumentNullException.ThrowIfNull(ep);
        var s = new JsonObject { ["tag"] = tag };
        switch (ep.Transport)
        {
            case DnsTransport.Local:
                s["type"] = "local";
                return s;
            case DnsTransport.Udp:
                s["type"] = "udp";
                break;
            case DnsTransport.Tcp:
                s["type"] = "tcp";
                break;
            case DnsTransport.Tls:
                s["type"] = "tls";
                break;
            case DnsTransport.Https:
                s["type"] = "https";
                if (!string.IsNullOrEmpty(ep.Path) && ep.Path != "/dns-query")
                {
                    s["path"] = ep.Path;
                }

                break;
            case DnsTransport.Quic:
                s["type"] = "quic";
                break;
            case DnsTransport.H3:
                s["type"] = "h3";
                break;
        }

        s["server"] = ep.Server ?? throw new ArgumentException($"DNS server '{tag}' has no address.", nameof(ep));
        if (ep.Port is { } port)
        {
            s["server_port"] = port;
        }

        if (!string.IsNullOrEmpty(ep.TlsServerName) && ep.Transport is DnsTransport.Tls or DnsTransport.Https or DnsTransport.Quic or DnsTransport.H3)
        {
            s["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = ep.TlsServerName };
        }

        if (!NetText.IsIpLiteral(ep.Server) && tag != DnsBootstrap)
        {
            s["domain_resolver"] = DnsBootstrap;
        }

        if (detour is not null)
        {
            s["detour"] = detour;
        }

        return s;
    }

    /// <summary>Translates one user rule (sing-box semantics are the reference semantics of <see cref="RoutingRule"/>).</summary>
    public static JsonObject? TranslateRule(RoutingRule rule, IReadOnlyDictionary<string, string> ruleSetPaths, HashSet<string> ruleSetsUsed, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(ruleSetPaths);
        ArgumentNullException.ThrowIfNull(ruleSetsUsed);
        ArgumentNullException.ThrowIfNull(notes);
        var o = new JsonObject();
        var ruleSets = new List<string>();
        var hadDestination = false;
        var unavailable = new List<string>();

        foreach (var c in rule.Conditions)
        {
            var values = c.Values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            switch (c.Type)
            {
                case RuleConditionType.Domain:
                    Append(o, "domain", values);
                    hadDestination = true;
                    break;
                case RuleConditionType.DomainSuffix:
                    Append(o, "domain_suffix", values);
                    hadDestination = true;
                    break;
                case RuleConditionType.DomainKeyword:
                    Append(o, "domain_keyword", values);
                    hadDestination = true;
                    break;
                case RuleConditionType.DomainRegex:
                    Append(o, "domain_regex", values);
                    hadDestination = true;
                    break;
                case RuleConditionType.IpCidr:
                    Append(o, "ip_cidr", values);
                    hadDestination = true;
                    break;
                case RuleConditionType.PrivateIp:
                    o["ip_is_private"] = true;
                    hadDestination = true;
                    break;
                case RuleConditionType.GeoIp:
                case RuleConditionType.GeoSite:
                case RuleConditionType.RuleSet:
                    hadDestination = true;
                    foreach (var v in values)
                    {
                        if (v.StartsWith('!'))
                        {
                            notes.Add($"Rule '{rule.Name}': negated geo value '{v}' is not supported and was skipped.");
                            continue;
                        }

                        var tag = c.Type switch
                        {
                            RuleConditionType.GeoIp => RoutingPresets.GeoIpTag(v),
                            RuleConditionType.GeoSite => RoutingPresets.GeoSiteTag(v),
                            _ => v,
                        };
                        if (ruleSetPaths.ContainsKey(tag))
                        {
                            ruleSets.Add(tag);
                        }
                        else
                        {
                            unavailable.Add(tag);
                        }
                    }

                    break;
                case RuleConditionType.Protocol:
                    Append(o, "protocol", values.Select(v => v.ToLowerInvariant()));
                    break;
                case RuleConditionType.Port:
                    o["port"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(int.Parse(v, System.Globalization.CultureInfo.InvariantCulture))).ToArray());
                    break;
                case RuleConditionType.PortRange:
                    Append(o, "port_range", values);
                    break;
                case RuleConditionType.ProcessName:
                case RuleConditionType.ProcessPath:
                    // The core compares process_name/process_path case-sensitively, but Windows names are not
                    // ("telegram.exe" must match …\Telegram.exe): one case-insensitive pattern per app instead.
                    Append(o, "process_path_regex", values.Select(AppPattern).OfType<string>());
                    break;
                case RuleConditionType.Network:
                    Append(o, "network", values.Select(v => v.ToLowerInvariant()));
                    break;
                case RuleConditionType.NetworkType:
                    notes.Add($"Rule '{rule.Name}': network type matching is not supported on Windows; rule skipped.");
                    return null;
            }
        }

        if (unavailable.Count > 0)
        {
            notes.Add($"Rule '{rule.Name}': rule-set(s) {string.Join(", ", unavailable)} unavailable (not downloaded/cached); those conditions were skipped.");
        }

        if (ruleSets.Count > 0)
        {
            o["rule_set"] = SingBoxOutboundBuilder.Array(ruleSets.Distinct(StringComparer.Ordinal));
            foreach (var t in ruleSets)
            {
                ruleSetsUsed.Add(t);
            }
        }

        var destinationKeys = new[] { "domain", "domain_suffix", "domain_keyword", "domain_regex", "ip_cidr", "ip_is_private", "rule_set" };
        if (hadDestination && !destinationKeys.Any(o.ContainsKey))
        {
            // Every destination condition was unavailable: emitting the rule would widen it to "match all".
            notes.Add($"Rule '{rule.Name}' was skipped because none of its destination conditions are available.");
            return null;
        }

        if (o.Count == 0)
        {
            return null;
        }

        if (rule.Invert)
        {
            o["invert"] = true;
        }

        switch (rule.Action.Type)
        {
            case RuleActionType.Direct:
                o["action"] = "route";
                o["outbound"] = DirectTag;
                break;
            case RuleActionType.Block:
                o["action"] = "reject";
                break;
            case RuleActionType.HijackDns:
                o["action"] = "hijack-dns";
                break;
            case RuleActionType.Outbound when rule.Action.OutboundTag is DirectTag or ProxyTag:
                o["action"] = "route";
                o["outbound"] = rule.Action.OutboundTag;
                break;
            case RuleActionType.Outbound:
                notes.Add($"Rule '{rule.Name}': outbound '{rule.Action.OutboundTag}' does not exist; routed through the proxy.");
                o["action"] = "route";
                o["outbound"] = ProxyTag;
                break;
            default:
                o["action"] = "route";
                o["outbound"] = ProxyTag;
                break;
        }

        return o;
    }

    /// <summary>True when a translated rule has a condition matched against destination addresses.</summary>
    private static bool MatchesAddresses(JsonObject rule) =>
        rule.ContainsKey("ip_cidr") || rule.ContainsKey("ip_is_private")
        || (rule["rule_set"] is JsonArray sets && sets.Any(t => t?.GetValue<string>() is { } tag && !tag.StartsWith(RoutingPresets.GeoSitePrefix, StringComparison.Ordinal)));

    /// <summary>Domain-only projection of a rule for DNS routing (IP conditions are not valid in DNS rules).</summary>
    private static JsonObject? DomainOnlyDnsRule(RoutingRule rule, IReadOnlyDictionary<string, string> ruleSetPaths, HashSet<string> ruleSetsUsed)
    {
        if (rule.Conditions.Any(c => c.Type is not (RuleConditionType.Domain or RuleConditionType.DomainSuffix or RuleConditionType.DomainKeyword or RuleConditionType.DomainRegex or RuleConditionType.GeoSite)))
        {
            return null;
        }

        var o = new JsonObject();
        foreach (var c in rule.Conditions)
        {
            var values = c.Values.Select(v => v.Trim()).Where(v => v.Length > 0 && !v.StartsWith('!')).ToList();
            switch (c.Type)
            {
                case RuleConditionType.Domain:
                    Append(o, "domain", values);
                    break;
                case RuleConditionType.DomainSuffix:
                    Append(o, "domain_suffix", values);
                    break;
                case RuleConditionType.DomainKeyword:
                    Append(o, "domain_keyword", values);
                    break;
                case RuleConditionType.DomainRegex:
                    Append(o, "domain_regex", values);
                    break;
                case RuleConditionType.GeoSite:
                    var tags = values.Select(RoutingPresets.GeoSiteTag).Where(ruleSetPaths.ContainsKey).ToList();
                    if (tags.Count > 0)
                    {
                        Append(o, "rule_set", tags);
                        foreach (var t in tags)
                        {
                            ruleSetsUsed.Add(t);
                        }
                    }

                    break;
            }
        }

        return o.Count == 0 ? null : o;
    }

    private static void Append(JsonObject o, string key, IEnumerable<string> values)
    {
        if (o[key] is not JsonArray arr)
        {
            arr = new JsonArray();
            o[key] = arr;
        }

        foreach (var v in values)
        {
            arr.Add(v);
        }
    }

    public const string BackInboundTag = "jv-back";
    public const string DnsInternal = "dns-internal";

    public static string ShapeTag(string shapeClass) => "jv-shape-" + shapeClass;

    /// <summary>Relay classes a policy needs (in rule order).</summary>
    public static IReadOnlyList<string> ShapeClassesOf(TrafficPolicy traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        var classes = new List<string>();
        if (!traffic.Shaping)
        {
            return classes;
        }

        foreach (var dc in traffic.DomainClasses)
        {
            if (Clean(dc.Domains).Count > 0 && IsClassName(dc.Class) && !classes.Contains(dc.Class))
            {
                classes.Add(dc.Class);
            }
        }

        for (var i = 0; i < traffic.LimitedApps.Count; i++)
        {
            if (AppPattern(traffic.LimitedApps[i]) is not null)
            {
                classes.Add(ShapeClasses.App(i));
            }
        }

        if (traffic.ShapeBackground)
        {
            foreach (var (cls, apps) in CategoryClasses(traffic))
            {
                if (apps.Any(a => AppPattern(a) is not null))
                {
                    classes.Add(cls);
                }
            }

            classes.Add(ShapeClasses.Background);
        }

        return classes;
    }

    /// <summary>Built-in categories below browsing, in rule order (media players, then downloaders).</summary>
    private static (string Class, IReadOnlyList<string> Apps)[] CategoryClasses(TrafficPolicy traffic) =>
        [(ShapeClasses.Media, traffic.MediaApps), (ShapeClasses.Downloads, traffic.DownloadApps)];

    private static void AddBlockRules(JsonArray rules, TrafficPolicy traffic)
    {
        var apps = traffic.BlockedApps.Select(AppPattern).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (apps.Count > 0)
        {
            rules.Add(new JsonObject { ["process_path_regex"] = SingBoxOutboundBuilder.Array(apps), ["action"] = "reject" });
        }

        if (Clean(traffic.BlockedDomains) is { Count: > 0 } domains)
        {
            rules.Add(new JsonObject { ["domain_suffix"] = SingBoxOutboundBuilder.Array(domains), ["action"] = "reject" });
        }

        if (traffic.BlockedCidrs.Select(NetText.NormalizeCidr).OfType<string>().Distinct().ToList() is { Count: > 0 } cidrs)
        {
            rules.Add(new JsonObject { ["ip_cidr"] = SingBoxOutboundBuilder.Array(cidrs), ["action"] = "reject" });
        }
    }

    /// <summary>
    /// Only connections that arrive from the device (never those coming back from the relay) are sent to the
    /// relay; QUIC to update hosts is refused so their downloads use TCP, where the relay controls them.
    /// </summary>
    private static void AddShapingRules(JsonArray rules, TrafficPolicy traffic, string[] primaryInbounds, RoutingSettings routing)
    {
        var inbound = SingBoxOutboundBuilder.Array(primaryInbounds);
        var tcp = SingBoxOutboundBuilder.Array(["tcp"]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dc in traffic.DomainClasses)
        {
            if (Clean(dc.Domains) is not { Count: > 0 } names || !IsClassName(dc.Class) || !seen.Add(dc.Class))
            {
                continue;
            }

            rules.Add(new JsonObject { ["domain_suffix"] = SingBoxOutboundBuilder.Array(names), ["network"] = SingBoxOutboundBuilder.Array(["udp"]), ["port"] = 443, ["action"] = "reject" });
            rules.Add(new JsonObject { ["inbound"] = inbound.DeepClone(), ["network"] = tcp.DeepClone(), ["domain_suffix"] = SingBoxOutboundBuilder.Array(names), ["outbound"] = ShapeTag(dc.Class) });
        }

        for (var i = 0; i < traffic.LimitedApps.Count; i++)
        {
            if (AppPattern(traffic.LimitedApps[i]) is { } pattern)
            {
                rules.Add(new JsonObject { ["inbound"] = inbound.DeepClone(), ["network"] = tcp.DeepClone(), ["process_path_regex"] = SingBoxOutboundBuilder.Array([pattern]), ["outbound"] = ShapeTag(ShapeClasses.App(i)) });
            }
        }

        if (!traffic.ShapeBackground)
        {
            return;
        }

        // Apps the user routes by name keep their own rules (the relay hands connections back without the process).
        var priority = traffic.PriorityApps
            .Concat(routing.Rules.Where(r => r.Enabled).SelectMany(r => r.Conditions)
                .Where(c => c.Type is RuleConditionType.ProcessName or RuleConditionType.ProcessPath)
                .SelectMany(c => c.Values))
            .Select(AppPattern).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        // Media players and downloaders get their own (lower) classes; apps the user routes by name are left alone.
        foreach (var (cls, apps) in CategoryClasses(traffic))
        {
            var patterns = apps.Select(AppPattern).OfType<string>().Where(p => !priority.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            if (patterns.Count > 0)
            {
                rules.Add(new JsonObject { ["inbound"] = inbound.DeepClone(), ["network"] = tcp.DeepClone(), ["process_path_regex"] = SingBoxOutboundBuilder.Array(patterns), ["outbound"] = ShapeTag(cls) });
            }
        }

        var conditions = new JsonArray(new JsonObject { ["inbound"] = inbound.DeepClone() }, new JsonObject { ["network"] = tcp.DeepClone() });
        if (priority.Count > 0)
        {
            conditions.Add(new JsonObject { ["process_path_regex"] = SingBoxOutboundBuilder.Array(priority), ["invert"] = true });
        }

        rules.Add(new JsonObject { ["type"] = "logical", ["mode"] = "and", ["rules"] = conditions, ["outbound"] = ShapeTag(ShapeClasses.Background) });
    }

    /// <summary>Case-insensitive pattern for an executable name or full path ("chrome.exe" matches …\Chrome.exe).</summary>
    public static string? AppPattern(string? app)
    {
        if (string.IsNullOrWhiteSpace(app) || NetText.HasControlChars(app) || app.Length > 260)
        {
            return null;
        }

        var a = app.Trim();
        var full = a.Contains('\\', StringComparison.Ordinal) || a.Contains('/', StringComparison.Ordinal);
        var sb = new System.Text.StringBuilder("(?i)");
        sb.Append(full ? "^" : "(^|[\\\\/])");
        foreach (var ch in a)
        {
            if ("\\.+*?()|[]{}^$".Contains(ch, StringComparison.Ordinal))
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.Append('$').ToString();
    }

    /// <summary>Relay class names travel as SOCKS user names: short, ASCII letters, digits and dashes.</summary>
    public static bool IsClassName(string? name) =>
        name is { Length: > 0 and <= 32 } && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static List<string> Clean(IEnumerable<string> domains) =>
        domains.Select(d => d.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(d => d.Length > 0 && NetText.IsValidHostName(d))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static void AddClashApi(JsonObject root, CoreSessionPlan plan, CoreCapabilities caps)
    {
        if (plan.ClashApi is { } api && caps.Supports(CoreFeature.TrafficStatsApi))
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{api.Port}",
                    ["secret"] = api.Secret,
                },
            };
        }
    }
}
