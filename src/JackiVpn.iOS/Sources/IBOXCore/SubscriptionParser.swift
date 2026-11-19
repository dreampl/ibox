import Foundation

/// Turns a subscription body (plain or Base64 link list) or pasted text into profiles.
/// Sing-box/Xray JSON and Clash YAML subscriptions are not ported yet (desktop: ConfigParserPipeline).
public enum SubscriptionParser {
    public struct Result: Sendable {
        public var profiles: [Profile] = []
        public var errors: [String] = []
    }

    public static let maxBodyBytes = 4 * 1024 * 1024
    public static let maxProfiles = 2000

    public static func parse(_ body: String) -> Result {
        var text = body.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("\u{FEFF}") { text.removeFirst() }
        if Base64Text.looksLikeBase64(text), let decoded = Base64Text.decodeToString(text, maxChars: maxBodyBytes) {
            text = decoded
        }

        var result = Result()
        var seen = Set<String>()
        for line in text.split(whereSeparator: \.isNewline) {
            let l = line.trimmingCharacters(in: .whitespaces)
            guard !l.isEmpty, !l.hasPrefix("#"), !l.hasPrefix("//") else { continue }
            guard ShareLinkParser.looksLikeShareLink(l) else {
                if l.contains("://") { result.errors.append("Unsupported link: \(l.prefix(24))…") }
                continue
            }
            do {
                let p = try ShareLinkParser.parse(l)
                if seen.insert(p.id).inserted { result.profiles.append(p) }
                if result.profiles.count >= maxProfiles { break }
            } catch {
                result.errors.append("\(error)")
            }
        }
        return result
    }

    /// `subscription-userinfo: upload=1; download=2; total=3; expire=1700000000`
    public struct UsageInfo: Codable, Hashable, Sendable {
        public var upload: Int64 = 0
        public var download: Int64 = 0
        public var total: Int64 = 0
        public var expire: Date?
    }

    public static func parseUserInfo(_ header: String?) -> UsageInfo? {
        guard let header, !header.isEmpty else { return nil }
        var info = UsageInfo()
        var any = false
        for part in header.split(separator: ";") {
            let kv = part.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard kv.count == 2, let n = Int64(kv[1]) else { continue }
            any = true
            switch kv[0].lowercased() {
            case "upload": info.upload = n
            case "download": info.download = n
            case "total": info.total = n
            case "expire" where n > 0: info.expire = Date(timeIntervalSince1970: TimeInterval(n))
            default: break
            }
        }
        return any ? info : nil
    }
}
