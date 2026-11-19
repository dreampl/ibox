import Foundation

/// Parses single share links: vless, vmess (v2rayN base64 JSON and URI form), trojan, ss (SIP002 + legacy),
/// socks, http/https, hysteria2/hy2, tuic, wireguard/wg.
/// Port of `JackiVpn.Subscriptions.Parsing.Uris.ShareLinkParser`. dnstt is not supported on iOS yet
/// (the desktop app runs it as a separate process, which an iOS packet tunnel can't do).
public enum ShareLinkParser {
    public static let supportedSchemes: Set<String> = [
        "vless", "vmess", "trojan", "ss", "socks", "socks5", "http", "https", "hysteria2", "hy2", "tuic", "wireguard", "wg",
    ]

    public static let maxLineLength = 16 * 1024

    public static func looksLikeShareLink(_ line: String) -> Bool {
        guard let r = line.range(of: "://") else { return false }
        let scheme = line[..<r.lowerBound]
        return !scheme.isEmpty && scheme.count <= 12 && supportedSchemes.contains(scheme.lowercased())
    }

    public static func parse(_ input: String) throws -> Profile {
        let raw = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard raw.count <= maxLineLength else { throw ProfileParseError("Link is too long.") }
        guard !raw.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) else {
            throw ProfileParseError("Link contains control characters.")
        }
        guard let r = raw.range(of: "://") else { throw ProfileParseError("Not a share link (missing scheme).") }
        let scheme = raw[..<r.lowerBound].lowercased()

        var p: Profile
        switch scheme {
        case "vless": p = try parseVless(ShareLink.parse(raw))
        case "vmess": p = try parseVmess(raw)
        case "trojan": p = try parseTrojan(ShareLink.parse(raw))
        case "ss": p = try parseShadowsocks(raw)
        case "socks", "socks5": p = try parseUserPass(ShareLink.parse(raw), proto: .socks, defaultPort: 1080)
        case "http", "https": p = try parseHttp(ShareLink.parse(raw))
        case "hysteria2", "hy2": p = try parseHysteria2(ShareLink.parse(raw))
        case "tuic": p = try parseTuic(ShareLink.parse(raw))
        case "wireguard", "wg": p = try parseWireGuard(ShareLink.parse(raw))
        default: throw ProfileParseError("Unsupported scheme '\(scheme.prefix(16))'.")
        }

        p.raw = raw
        p.id = Profile.stableId(for: raw)
        normalizePath(&p.stream)
        return p
    }

    // MARK: - Protocols

    private static func parseVless(_ link: ShareLink) throws -> Profile {
        let uuid = LinkQuery.decode(link.userInfo ?? "").trimmingCharacters(in: .whitespaces)
        guard !uuid.isEmpty else { throw ProfileParseError("VLESS link has no user id.") }
        let q = link.query
        var p = try Profile(proto: .vless, server: link.requireHost(), port: link.requirePort(443), name: link.fragment ?? "")
        p.uuid = uuid
        p.flow = q.get("flow")
        p.encryption = q.get("encryption") ?? "none"
        p.packetEncoding = q.get("packetEncoding", "packet_encoding")
        (p.stream, p.tls) = try parseStream(q, defaultSecurity: "none")
        return p
    }

    private static func parseTrojan(_ link: ShareLink) throws -> Profile {
        let password = LinkQuery.decode(link.userInfo ?? "")
        guard !password.isEmpty else { throw ProfileParseError("Trojan link has no password.") }
        let q = link.query
        var p = try Profile(proto: .trojan, server: link.requireHost(), port: link.requirePort(443), name: link.fragment ?? "")
        p.password = password
        p.flow = q.get("flow")
        (p.stream, p.tls) = try parseStream(q, defaultSecurity: "tls")
        return p
    }

    private static func parseVmess(_ raw: String) throws -> Profile {
        let body = raw[raw.range(of: "://")!.upperBound...]
        let hash = body.firstIndex(of: "#")
        let bodyNoFragment = String(hash.map { body[..<$0] } ?? body)
        let fragmentName = hash.map { LinkQuery.decode(String(body[body.index(after: $0)...])) }

        if !bodyNoFragment.contains("@"),
           let json = Base64Text.decodeToString(bodyNoFragment, maxChars: 64 * 1024),
           json.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("{") {
            return try parseVmessJson(json, fragmentName: fragmentName)
        }

        let link = try ShareLink.parse(raw)
        let q = link.query
        var p = try Profile(proto: .vmess, server: link.requireHost(), port: link.requirePort(), name: link.fragment ?? "")
        p.uuid = LinkQuery.decode(link.userInfo ?? "")
        p.encryption = q.get("encryption", "scy", "security_method") ?? "auto"
        p.alterId = q.int("aid", "alterId") ?? 0
        (p.stream, p.tls) = try parseStream(q, defaultSecurity: "none")
        return p
    }

    private static func parseVmessJson(_ json: String, fragmentName: String?) throws -> Profile {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw ProfileParseError("VMess payload is not a JSON object.")
        }
        func str(_ k: String) -> String? {
            switch obj[k] {
            case let s as String: return s.isEmpty ? nil : s
            case let n as NSNumber: return n.stringValue
            default: return nil
            }
        }
        func int(_ k: String) -> Int? { str(k).flatMap { Int($0) } }
        func bool(_ k: String) -> Bool {
            switch obj[k] {
            case let b as Bool: return b
            case let s as String: return s == "1" || s == "true"
            case let n as NSNumber: return n.intValue != 0
            default: return false
            }
        }

        guard let server = str("add") else { throw ProfileParseError("VMess payload has no address.") }
        guard let port = int("port") else { throw ProfileParseError("VMess payload has no port.") }
        guard let uuid = str("id") else { throw ProfileParseError("VMess payload has no id.") }

        let name = str("ps")?.trimmingCharacters(in: .whitespaces)
        var p = Profile(proto: .vmess, server: server, port: port, name: (name?.isEmpty == false ? name : fragmentName) ?? "")
        p.uuid = uuid
        p.alterId = int("aid") ?? 0
        p.encryption = str("scy") ?? "auto"

        let headerType = str("type")
        var s = Profile.Stream()
        s.type = try parseTransport(str("net"))
        switch s.type {
        case .grpc:
            s.serviceName = str("path")?.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        case .kcp, .quic:
            s.headerType = headerType
            s.host = str("host")
            s.path = str("path")
        default:
            s.host = str("host")
            s.path = str("path")
            s.headerType = headerType == "http" ? "http" : nil
        }
        p.stream = s

        if let security = str("tls")?.lowercased(), security == "tls" || security == "xtls" {
            p.tls.enabled = true
            p.tls.serverName = str("sni")
            p.tls.alpn = splitList(str("alpn"))
            p.tls.fingerprint = str("fp")
            p.tls.allowInsecure = bool("allowInsecure") || bool("insecure") || bool("skip-cert-verify")
        }
        return p
    }

    private static func parseShadowsocks(_ raw: String) throws -> Profile {
        let body = raw[raw.range(of: "://")!.upperBound...]
        let hash = body.firstIndex(of: "#")
        let name = hash.map { LinkQuery.decode(String(body[body.index(after: $0)...])) }
        let main = hash.map { body[..<$0] } ?? body

        if !main.contains("@") {
            // Legacy: ss://BASE64(method:password@host:port)
            let qIdx = main.firstIndex(of: "?")
            var b64 = String(qIdx.map { main[..<$0] } ?? main)
            while b64.hasSuffix("/") { b64.removeLast() }
            guard let decoded = Base64Text.decodeToString(b64, maxChars: 16 * 1024) else {
                throw ProfileParseError("Shadowsocks legacy link is not valid Base64.")
            }
            let rebuilt = "ss://" + decoded + (qIdx.map { String(main[$0...]) } ?? "")
            let legacy = try ShareLink.parse(rebuilt)
            return try buildShadowsocks(legacy, creds: splitMethodPassword(legacy.userInfo), name: name)
        }

        let link = try ShareLink.parse(raw)
        let decodedUser = LinkQuery.decode(link.userInfo ?? "")
        let creds: (String, String)
        if decodedUser.contains(":") && !Base64Text.looksLikeBase64(decodedUser) {
            creds = try splitMethodPassword(decodedUser)
        } else if let dec = Base64Text.decodeToString(decodedUser, maxChars: 4096), dec.contains(":") {
            creds = try splitMethodPassword(dec)
        } else {
            creds = try splitMethodPassword(decodedUser)
        }
        return try buildShadowsocks(link, creds: creds, name: name)
    }

    private static func splitMethodPassword(_ userInfo: String?) throws -> (String, String) {
        let s = LinkQuery.decode(userInfo ?? "")
        guard let colon = s.firstIndex(of: ":"), colon > s.startIndex else {
            throw ProfileParseError("Shadowsocks credentials must be 'method:password'.")
        }
        return (s[..<colon].trimmingCharacters(in: .whitespaces).lowercased(), String(s[s.index(after: colon)...]))
    }

    private static func buildShadowsocks(_ link: ShareLink, creds: (String, String), name: String?) throws -> Profile {
        let q = link.query
        var p = try Profile(proto: .shadowsocks, server: link.requireHost(), port: link.requirePort(), name: name ?? link.fragment ?? "")
        p.method = creds.0
        p.password = creds.1
        if let pluginRaw = q.get("plugin") {
            if let semi = pluginRaw.firstIndex(of: ";") {
                p.plugin = String(pluginRaw[..<semi])
                p.pluginOptions = String(pluginRaw[pluginRaw.index(after: semi)...])
            } else {
                p.plugin = pluginRaw
            }
        }
        p.udpOverTcp = q.bool("uot", "udp-over-tcp", "udp_over_tcp")
        return p
    }

    private static func parseUserPass(_ link: ShareLink, proto: Profile.Proto, defaultPort: Int) throws -> Profile {
        var p = try Profile(proto: proto, server: link.requireHost(), port: link.requirePort(defaultPort), name: link.fragment ?? "")
        (p.username, p.password) = splitUserPass(link.userInfo, allowBase64: true)
        return p
    }

    private static func parseHttp(_ link: ShareLink) throws -> Profile {
        let tls = link.scheme == "https"
        var p = try parseUserPass(link, proto: .http, defaultPort: tls ? 443 : 80)
        if tls {
            p.tls.enabled = true
            p.tls.serverName = link.query.get("sni")
            p.tls.allowInsecure = link.query.bool("allowInsecure", "insecure")
        }
        return p
    }

    private static func parseHysteria2(_ link: ShareLink) throws -> Profile {
        let q = link.query
        var p = try Profile(proto: .hysteria2, server: link.requireHost(), port: link.requirePort(443), name: link.fragment ?? "")
        p.password = LinkQuery.decode(link.userInfo ?? "")
        var ports: [String] = []
        if let pt = link.portText, pt.contains(",") || pt.contains("-") {
            ports += pt.split(separator: ",").map { $0.replacingOccurrences(of: "-", with: ":") }
        }
        if let mport = q.get("mport", "ports") {
            ports += mport.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "-", with: ":") }
        }
        var seen = Set<String>()
        p.serverPorts = ports.filter { $0.contains(":") && seen.insert($0).inserted }
        p.tls.enabled = true
        p.tls.serverName = q.get("sni", "peer")
        p.tls.allowInsecure = q.bool("insecure", "allowInsecure", "allow_insecure")
        p.tls.alpn = q.list("alpn")
        p.obfsType = q.get("obfs")
        p.obfsPassword = q.get("obfs-password", "obfs_password", "obfsParam")
        p.upMbps = parseMbps(q.get("upmbps", "up"))
        p.downMbps = parseMbps(q.get("downmbps", "down"))
        return p
    }

    private static func parseTuic(_ link: ShareLink) throws -> Profile {
        let q = link.query
        let (uuid, password) = splitUserPass(link.userInfo, allowBase64: false)
        guard let uuid, !uuid.isEmpty else { throw ProfileParseError("TUIC link has no UUID.") }
        var p = try Profile(proto: .tuic, server: link.requireHost(), port: link.requirePort(443), name: link.fragment ?? "")
        p.uuid = uuid
        p.password = password
        p.tls.enabled = true
        p.tls.serverName = q.get("sni", "peer")
        p.tls.alpn = q.list("alpn")
        p.tls.allowInsecure = q.bool("allow_insecure", "allowInsecure", "insecure")
        p.tls.disableSni = q.bool("disable_sni")
        p.congestionControl = q.get("congestion_control", "congestion-control", "cc")
        p.udpRelayMode = q.get("udp_relay_mode", "udp-relay-mode")
        p.zeroRtt = q.bool("zero_rtt_handshake", "reduce_rtt", "zero-rtt")
        return p
    }

    private static func parseWireGuard(_ link: ShareLink) throws -> Profile {
        let q = link.query
        var p = try Profile(proto: .wireguard, server: link.requireHost(), port: link.requirePort(51820), name: link.fragment ?? "")
        p.privateKey = LinkQuery.decode(link.userInfo ?? "").trimmingCharacters(in: .whitespaces)
        p.peerPublicKey = q.get("publickey", "public_key", "peer_public_key", "publicKey") ?? ""
        p.preSharedKey = q.get("presharedkey", "pre_shared_key", "psk", "preSharedKey")
        p.localAddresses = q.list("address", "ip", "addresses").map { a in
            a.contains("/") ? a : a + (a.contains(":") ? "/128" : "/32")
        }
        p.reserved = q.list("reserved").map { Int($0) ?? -1 }
        p.mtu = q.int("mtu")
        return p
    }

    // MARK: - Stream settings

    static func parseStream(_ q: LinkQuery, defaultSecurity: String) throws -> (Profile.Stream, Profile.TLS) {
        var s = Profile.Stream()
        s.type = try parseTransport(q.get("type", "net", "network"))
        let path = q.get("path")
        s.host = q.get("host")
        if s.type == .grpc {
            s.serviceName = q.get("serviceName", "service_name") ?? path?.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        } else {
            s.path = path
        }
        if [.tcp, .kcp, .quic].contains(s.type) { s.headerType = q.get("headerType", "header") }
        s.maxEarlyData = q.int("ed", "maxEarlyData")
        s.earlyDataHeaderName = q.get("eh", "earlyDataHeaderName")

        var t = Profile.TLS()
        let security = (q.get("security") ?? defaultSecurity).lowercased()
        switch security {
        case "none", "":
            break
        case "tls", "xtls":
            t.enabled = true
            t.serverName = q.get("sni", "peer", "serverName")
            t.alpn = q.list("alpn")
            t.fingerprint = q.get("fp", "fingerprint")
            t.allowInsecure = q.bool("allowInsecure", "insecure", "allow_insecure", "skip-cert-verify")
            t.fragment = isFragmentOn(q.get("fragment"))
        case "reality":
            t.enabled = true
            t.serverName = q.get("sni", "peer", "serverName")
            t.alpn = q.list("alpn")
            t.fingerprint = q.get("fp", "fingerprint") ?? "chrome"
            guard let pbk = q.get("pbk", "publicKey", "public_key") else {
                throw ProfileParseError("REALITY link has no public key (pbk=).")
            }
            t.realityPublicKey = pbk
            t.realityShortId = q.get("sid", "shortId", "short_id")
            t.fragment = isFragmentOn(q.get("fragment"))
        default:
            throw ProfileParseError("Unsupported security '\(security.prefix(16))'.")
        }
        return (s, t)
    }

    static func parseTransport(_ value: String?) throws -> Profile.Transport {
        switch value?.trimmingCharacters(in: .whitespaces).lowercased() {
        case nil, "", "tcp", "raw", "none": return .tcp
        case "ws", "websocket": return .ws
        case "grpc", "gun": return .grpc
        case "httpupgrade", "http-upgrade": return .httpupgrade
        case "h2", "http", "http2": return .http2
        case "quic": return .quic
        case "xhttp", "splithttp": return .xhttp
        case "kcp", "mkcp": return .kcp
        case let other?: throw ProfileParseError("Unsupported transport '\(other.prefix(16))'.")
        }
    }

    /// Moves Xray-style early data in the path ("/ws?ed=2048") into structured fields, like
    /// `ProfileFactory.NormalizeTransport` on desktop.
    static func normalizePath(_ s: inout Profile.Stream) {
        guard var path = s.path, !path.isEmpty else { return }
        if !path.hasPrefix("/") { path = "/" + path }
        if let q = path.firstIndex(of: "?") {
            let query = LinkQuery(String(path[path.index(after: q)...]))
            if let ed = query.int("ed"), s.maxEarlyData == nil {
                s.maxEarlyData = ed
                path = String(path[..<q])
            }
        }
        s.path = path
    }

    private static func isFragmentOn(_ v: String?) -> Bool {
        guard let v = v?.trimmingCharacters(in: .whitespaces).lowercased(), !v.isEmpty else { return false }
        return v != "0" && v != "false"
    }

    private static func splitUserPass(_ userInfo: String?, allowBase64: Bool) -> (String?, String?) {
        guard let userInfo, !userInfo.isEmpty else { return (nil, nil) }
        var decoded = LinkQuery.decode(userInfo)
        if allowBase64, !decoded.contains(":"), let b64 = Base64Text.decodeToString(decoded, maxChars: 4096), b64.contains(":") {
            decoded = b64
        }
        guard let colon = decoded.firstIndex(of: ":") else { return (decoded, nil) }
        return (String(decoded[..<colon]), String(decoded[decoded.index(after: colon)...]))
    }

    private static func parseMbps(_ v: String?) -> Int? {
        guard let v else { return nil }
        return Int(String(v.trimmingCharacters(in: .whitespaces).prefix(while: { $0.isASCII && $0.isNumber })))
    }

    static func splitList(_ v: String?) -> [String] {
        (v ?? "").split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
}
