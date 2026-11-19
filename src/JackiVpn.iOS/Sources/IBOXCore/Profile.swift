import Foundation

/// One proxy server, normalized from a share link. Mirrors the subset of
/// `JackiVpn.Domain.Profiles.ProxyProfile` that the iOS sing-box tunnel needs.
public struct Profile: Codable, Hashable, Identifiable, Sendable {
    public enum Proto: String, Codable, Sendable {
        case vless, vmess, trojan, shadowsocks, socks, http, hysteria2, tuic, wireguard
    }

    public enum Transport: String, Codable, Sendable {
        case tcp, ws, grpc, httpupgrade, http2, quic, xhttp, kcp
    }

    public struct TLS: Codable, Hashable, Sendable {
        public var enabled = false
        public var serverName: String?
        public var alpn: [String] = []
        public var fingerprint: String?
        public var allowInsecure = false
        public var disableSni = false
        public var realityPublicKey: String?
        public var realityShortId: String?
        public var fragment = false

        public var isReality: Bool { realityPublicKey != nil }
        public init() {}
    }

    public struct Stream: Codable, Hashable, Sendable {
        public var type: Transport = .tcp
        public var host: String?
        public var path: String?
        public var serviceName: String?
        public var headerType: String?
        public var maxEarlyData: Int?
        public var earlyDataHeaderName: String?
        public init() {}
    }

    public var id: String
    public var name: String
    public var proto: Proto
    public var server: String
    public var port: Int

    public var uuid: String?
    public var password: String?
    public var username: String?
    public var flow: String?
    public var encryption: String?
    public var alterId: Int?
    public var packetEncoding: String?

    public var stream = Stream()
    public var tls = TLS()

    // Shadowsocks
    public var method: String?
    public var plugin: String?
    public var pluginOptions: String?
    public var udpOverTcp = false

    // Hysteria2
    public var obfsType: String?
    public var obfsPassword: String?
    public var upMbps: Int?
    public var downMbps: Int?
    public var serverPorts: [String] = []

    // TUIC
    public var congestionControl: String?
    public var udpRelayMode: String?
    public var zeroRtt = false

    // WireGuard
    public var privateKey: String?
    public var peerPublicKey: String?
    public var preSharedKey: String?
    public var localAddresses: [String] = []
    public var reserved: [Int] = []
    public var mtu: Int?

    /// Original link, kept so the profile can be shared/re-exported unchanged.
    public var raw: String

    public init(proto: Proto, server: String, port: Int, name: String = "", raw: String = "") {
        self.id = ""
        self.name = name
        self.proto = proto
        self.server = server
        self.port = port
        self.raw = raw
    }

    public var displayName: String {
        name.trimmingCharacters(in: .whitespaces).isEmpty ? "\(server):\(port)" : name
    }

    /// Stable id derived from the link, so re-importing a subscription keeps selection.
    public static func stableId(for raw: String) -> String {
        // FNV-1a 64-bit: deterministic across launches and platforms (Hasher is seeded per process).
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in raw.utf8 {
            hash ^= UInt64(byte)
            hash = hash &* 0x100_0000_01b3
        }
        return String(hash, radix: 16)
    }
}

public struct ProfileParseError: Error, Equatable, CustomStringConvertible {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var description: String { message }
}
