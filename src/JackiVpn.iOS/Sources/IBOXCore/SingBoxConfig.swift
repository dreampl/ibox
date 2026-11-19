import Foundation

/// Profile to sing-box (>= 1.12) JSON for the iOS packet tunnel. A trimmed port of
/// `JackiVpn.Cores.Generation.SingBoxOutboundBuilder` + the TUN parts of `SingBoxConfigBuilder`.
public enum SingBoxConfig {
    public struct Options: Sendable {
        public var remoteDns = "https://1.1.1.1/dns-query"
        public var bootstrapDns = "1.1.1.1"
        public var bypassLan = true
        public var ipv6 = false
        public var mtu = 9000
        public var logLevel = "warn"
        public init() {}
    }

    public static let proxyTag = "proxy"

    public static func build(_ p: Profile, options o: Options = Options()) throws -> [String: Any] {
        let outbound = try self.outbound(p, tag: proxyTag)
        let isEndpoint = p.proto == .wireguard
        let direct: [String: Any] = ["type": "direct", "tag": "direct"]
        let outbounds: [[String: Any]] = isEndpoint ? [direct] : [outbound, direct]

        var routeRules: [[String: Any]] = [
            ["action": "sniff"],
            ["protocol": "dns", "action": "hijack-dns"],
        ]
        if o.bypassLan {
            routeRules.append(["ip_is_private": true, "outbound": "direct"])
        }

        var config: [String: Any] = [
            "log": ["level": o.logLevel, "timestamp": true],
            "dns": [
                "servers": [
                    dnsServer(o.remoteDns, tag: "dns-remote", detour: proxyTag),
                    dnsServer(o.bootstrapDns, tag: "dns-bootstrap", detour: nil),
                ],
                "final": "dns-remote",
                "strategy": o.ipv6 ? "prefer_ipv4" : "ipv4_only",
            ] as [String: Any],
            "inbounds": [[
                "type": "tun",
                "tag": "tun-in",
                "address": o.ipv6 ? ["172.19.0.1/30", "fdfe:dcba:9876::1/126"] : ["172.19.0.1/30"],
                "mtu": o.mtu,
                "auto_route": true,
                "strict_route": true,
                "stack": "mixed",
            ] as [String: Any]],
            "outbounds": outbounds,
            "route": [
                "rules": routeRules,
                "final": proxyTag,
                "auto_detect_interface": true,
                "default_domain_resolver": ["server": "dns-bootstrap"],
            ] as [String: Any],
        ]
        if isEndpoint { config["endpoints"] = [outbound] }
        return config
    }

    public static func json(_ p: Profile, options: Options = Options()) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: build(p, options: options), options: [.prettyPrinted, .sortedKeys])
        return String(decoding: data, as: UTF8.self)
    }

    static func dnsServer(_ address: String, tag: String, detour: String?) -> [String: Any] {
        var s: [String: Any] = ["tag": tag]
        if let r = address.range(of: "://") {
            let scheme = address[..<r.lowerBound].lowercased()
            var rest = String(address[r.upperBound...])
            var path: String?
            if let slash = rest.firstIndex(of: "/") {
                path = String(rest[slash...])
                rest = String(rest[..<slash])
            }
            s["type"] = scheme == "tls" ? "tls" : scheme == "tcp" ? "tcp" : scheme == "udp" ? "udp" : "https"
            s["server"] = rest
            if let path, path != "/dns-query", s["type"] as? String == "https" { s["path"] = path }
        } else {
            s["type"] = "udp"
            s["server"] = address
        }
        if let detour { s["detour"] = detour }
        return s
    }

    public static func outbound(_ p: Profile, tag: String) throws -> [String: Any] {
        if p.proto == .wireguard { return wireGuardEndpoint(p, tag: tag) }

        var o: [String: Any] = ["tag": tag, "server": p.server, "server_port": p.port]
        switch p.proto {
        case .vless:
            o["type"] = "vless"
            o["uuid"] = p.uuid ?? ""
            if let f = p.flow, !f.isEmpty { o["flow"] = f }
            if let pe = p.packetEncoding, !pe.isEmpty { o["packet_encoding"] = pe }
        case .vmess:
            o["type"] = "vmess"
            o["uuid"] = p.uuid ?? ""
            o["security"] = (p.encryption?.isEmpty ?? true) ? "auto" : p.encryption!
            o["alter_id"] = p.alterId ?? 0
        case .trojan:
            o["type"] = "trojan"
            o["password"] = p.password ?? ""
        case .shadowsocks:
            o["type"] = "shadowsocks"
            o["method"] = p.method ?? ""
            o["password"] = p.password ?? ""
            if let plugin = p.plugin, !plugin.isEmpty {
                o["plugin"] = plugin
                if let opts = p.pluginOptions, !opts.isEmpty { o["plugin_opts"] = opts }
            }
            if p.udpOverTcp { o["udp_over_tcp"] = true }
        case .socks:
            o["type"] = "socks"
            o["version"] = "5"
            addUserPass(&o, p)
        case .http:
            o["type"] = "http"
            addUserPass(&o, p)
        case .hysteria2:
            o["type"] = "hysteria2"
            o["password"] = p.password ?? ""
            if !p.serverPorts.isEmpty { o["server_ports"] = p.serverPorts }
            if let up = p.upMbps { o["up_mbps"] = up }
            if let down = p.downMbps { o["down_mbps"] = down }
            if let obfs = p.obfsType, !obfs.isEmpty { o["obfs"] = ["type": obfs, "password": p.obfsPassword ?? ""] }
        case .tuic:
            o["type"] = "tuic"
            o["uuid"] = p.uuid ?? ""
            if let pw = p.password, !pw.isEmpty { o["password"] = pw }
            if let cc = p.congestionControl, !cc.isEmpty { o["congestion_control"] = cc }
            if let m = p.udpRelayMode, !m.isEmpty { o["udp_relay_mode"] = m }
            if p.zeroRtt { o["zero_rtt_handshake"] = true }
        case .wireguard:
            break
        }

        if let tls = tls(p) { o["tls"] = tls }
        if let transport = try transport(p.stream) { o["transport"] = transport }
        return o
    }

    static func tls(_ p: Profile) -> [String: Any]? {
        let t = p.tls
        guard t.enabled else { return nil }
        var o: [String: Any] = ["enabled": true]
        if let sni = t.serverName, !sni.isEmpty { o["server_name"] = sni }
        if t.disableSni { o["disable_sni"] = true }
        if t.allowInsecure { o["insecure"] = true }
        // gRPC needs HTTP/2: an ALPN list without h2 would make every request fail.
        var alpn = t.alpn
        if p.stream.type == .grpc, !alpn.isEmpty, !alpn.contains(where: { $0.lowercased() == "h2" }) { alpn = ["h2"] }
        if !alpn.isEmpty { o["alpn"] = alpn }
        let fp = t.fingerprint ?? (t.isReality ? "chrome" : nil) // REALITY requires uTLS in sing-box.
        if let fp, !fp.isEmpty { o["utls"] = ["enabled": true, "fingerprint": fp] }
        if let pbk = t.realityPublicKey {
            var r: [String: Any] = ["enabled": true, "public_key": pbk]
            if let sid = t.realityShortId, !sid.isEmpty { r["short_id"] = sid }
            o["reality"] = r
        }
        if t.fragment { o["fragment"] = true }
        return o
    }

    static func transport(_ s: Profile.Stream) throws -> [String: Any]? {
        switch s.type {
        case .tcp:
            return nil
        case .ws:
            var o: [String: Any] = ["type": "ws"]
            if let path = s.path, !path.isEmpty { o["path"] = path }
            if let host = s.host, !host.isEmpty { o["headers"] = ["Host": host] }
            if let ed = s.maxEarlyData, ed > 0 {
                o["max_early_data"] = ed
                o["early_data_header_name"] = s.earlyDataHeaderName ?? "Sec-WebSocket-Protocol"
            }
            return o
        case .httpupgrade:
            var o: [String: Any] = ["type": "httpupgrade"]
            if let host = s.host, !host.isEmpty { o["host"] = host }
            if let path = s.path, !path.isEmpty { o["path"] = path }
            return o
        case .grpc:
            // HTTP/2 health checks so a silently dead connection is replaced within ~25 s.
            var o: [String: Any] = ["type": "grpc", "idle_timeout": "15s", "ping_timeout": "10s"]
            if let name = s.serviceName, !name.isEmpty { o["service_name"] = name }
            return o
        case .http2:
            var o: [String: Any] = ["type": "http"]
            if let host = s.host, !host.isEmpty { o["host"] = ShareLinkParser.splitList(host) }
            if let path = s.path, !path.isEmpty { o["path"] = path }
            return o
        case .quic:
            return ["type": "quic"]
        case .xhttp, .kcp:
            // Desktop routes these to Xray; the iOS tunnel embeds sing-box only.
            throw ProfileParseError("\(s.type.rawValue) transport needs the Xray core, which the iOS app doesn't include yet.")
        }
    }

    private static func addUserPass(_ o: inout [String: Any], _ p: Profile) {
        if let u = p.username, !u.isEmpty { o["username"] = u }
        if let pw = p.password, !pw.isEmpty { o["password"] = pw }
    }

    private static func wireGuardEndpoint(_ p: Profile, tag: String) -> [String: Any] {
        var peer: [String: Any] = [
            "address": p.server,
            "port": p.port,
            "public_key": p.peerPublicKey ?? "",
            "allowed_ips": ["0.0.0.0/0", "::/0"],
        ]
        if let psk = p.preSharedKey, !psk.isEmpty { peer["pre_shared_key"] = psk }
        if p.reserved.count == 3, p.reserved.allSatisfy({ (0...255).contains($0) }) { peer["reserved"] = p.reserved }
        var o: [String: Any] = [
            "type": "wireguard",
            "tag": tag,
            "private_key": p.privateKey ?? "",
            "address": p.localAddresses,
            "peers": [peer],
        ]
        if let mtu = p.mtu { o["mtu"] = mtu }
        return o
    }

    /// True when the iOS tunnel can run this profile with sing-box alone.
    public static func isSupported(_ p: Profile) -> Bool {
        p.stream.type != .xhttp && p.stream.type != .kcp
    }
}
