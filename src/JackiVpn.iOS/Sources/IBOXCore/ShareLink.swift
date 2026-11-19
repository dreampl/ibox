import Foundation

/// Lossless split of `scheme://userinfo@host:port/path?query#fragment` without `URL`, which rejects
/// many real-world share links. Port of `JackiVpn.Subscriptions.Parsing.Uris.ShareLink`.
struct ShareLink {
    let scheme: String
    let userInfo: String?
    let host: String
    let portText: String?
    let path: String?
    let query: LinkQuery
    let fragment: String?

    static func parse(_ raw: String) throws -> ShareLink {
        let s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let schemeRange = s.range(of: "://"), schemeRange.lowerBound > s.startIndex else {
            throw ProfileParseError("Not a share link (missing scheme).")
        }
        let scheme = s[..<schemeRange.lowerBound].lowercased()
        var rest = Substring(s[schemeRange.upperBound...])

        var fragment: String?
        if let hash = rest.firstIndex(of: "#") {
            fragment = LinkQuery.decode(String(rest[rest.index(after: hash)...])).trimmingCharacters(in: .whitespaces)
            rest = rest[..<hash]
        }

        var query: String?
        if let q = rest.firstIndex(of: "?") {
            query = String(rest[rest.index(after: q)...])
            rest = rest[..<q]
        }

        let at = rest.lastIndex(of: "@")
        let slashSearchStart = at.map { rest.index(after: $0) } ?? rest.startIndex
        var path: String?
        if let slash = rest[slashSearchStart...].firstIndex(of: "/") {
            path = String(rest[slash...])
            rest = rest[..<slash]
        }

        var userInfo: String?
        if let at {
            userInfo = String(rest[..<at])
            rest = rest[rest.index(after: at)...]
        }

        let (host, port) = try splitHostPort(String(rest))
        return ShareLink(scheme: scheme, userInfo: userInfo, host: host, portText: port, path: path,
                         query: LinkQuery(query), fragment: fragment)
    }

    func requirePort(_ defaultPort: Int? = nil) throws -> Int {
        guard let portText, !portText.isEmpty else {
            if let defaultPort { return defaultPort }
            throw ProfileParseError("Port is missing.")
        }
        // "443,20000-30000" (Hysteria2 multi-port): the first entry is the primary port.
        let first = portText.split(separator: ",").first.flatMap { $0.split(separator: "-").first } ?? ""
        guard first.allSatisfy(\.isASCII), let port = Int(first), (1...65535).contains(port) else {
            throw ProfileParseError("Port is invalid.")
        }
        return port
    }

    func requireHost() throws -> String {
        let h = host.hasPrefix("[") && host.hasSuffix("]") ? String(host.dropFirst().dropLast()) : host
        guard NetText.isValidServerAddress(h) else { throw ProfileParseError("Server address is missing or invalid.") }
        return h
    }

    private static func splitHostPort(_ authority: String) throws -> (String, String?) {
        let a = authority.trimmingCharacters(in: .whitespaces)
        if a.hasPrefix("[") {
            guard let close = a.firstIndex(of: "]") else { throw ProfileParseError("Unterminated IPv6 literal.") }
            let host = String(a[a.index(after: a.startIndex)..<close])
            let after = a[a.index(after: close)...]
            return (host, after.hasPrefix(":") ? String(after.dropFirst()) : nil)
        }
        if a.filter({ $0 == ":" }).count > 1 {
            if NetText.isIPv6(a) { return (a, nil) }
            if let lastColon = a.lastIndex(of: ":"), NetText.isIPv6(String(a[..<lastColon])) {
                return (String(a[..<lastColon]), String(a[a.index(after: lastColon)...]))
            }
            throw ProfileParseError("Ambiguous host:port.")
        }
        if let colon = a.lastIndex(of: ":") {
            return (String(a[..<colon]), String(a[a.index(after: colon)...]))
        }
        return (a, nil)
    }
}

/// Case-insensitive query bag. Port of `LinkQuery` (without the unconsumed-extension tracking).
struct LinkQuery {
    private var values: [String: String] = [:]

    init(_ query: String?) {
        guard let query, !query.isEmpty else { return }
        for part in query.split(separator: "&") where !part.isEmpty {
            let key: String
            let value: String
            if let eq = part.firstIndex(of: "=") {
                key = LinkQuery.decode(String(part[..<eq])).trimmingCharacters(in: .whitespaces)
                value = LinkQuery.decode(String(part[part.index(after: eq)...]))
            } else {
                key = LinkQuery.decode(String(part)).trimmingCharacters(in: .whitespaces)
                value = ""
            }
            if !key.isEmpty { values[key.lowercased()] = value }
        }
    }

    func get(_ keys: String...) -> String? { get(keys) }

    func get(_ keys: [String]) -> String? {
        for k in keys {
            if let v = values[k.lowercased()], !v.isEmpty { return v }
        }
        return nil
    }

    func bool(_ keys: String...) -> Bool {
        guard let v = get(keys)?.lowercased() else { return false }
        return v == "1" || v == "true" || v == "yes"
    }

    func int(_ keys: String...) -> Int? { get(keys).flatMap { Int($0) } }

    func list(_ keys: String...) -> [String] {
        guard let v = get(keys) else { return [] }
        return v.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    static func decode(_ s: String) -> String { s.removingPercentEncoding ?? s }
}

enum NetText {
    static func isValidServerAddress(_ host: String) -> Bool {
        guard !host.isEmpty, host.count <= 253 else { return false }
        if isIPv6(host) || isIPv4(host) { return true }
        return host.unicodeScalars.allSatisfy { c in
            switch c {
            case "a"..."z", "A"..."Z", "0"..."9", "-", ".", "_": return true
            default: return c.value > 0x7F // IDN hostnames are passed through to the core.
            }
        } && !host.hasPrefix(".") && !host.hasPrefix("-")
    }

    static func isIPv4(_ s: String) -> Bool {
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 4 && parts.allSatisfy { p in
            !p.isEmpty && p.count <= 3 && p.allSatisfy(\.isASCII) && (Int(p).map { $0 <= 255 } ?? false)
        }
    }

    static func isIPv6(_ s: String) -> Bool {
        guard s.contains(":"), s.count <= 45 else { return false }
        let groups = s.components(separatedBy: ":")
        guard groups.count >= 3, groups.count <= 8 || (groups.count == 9 && s.contains("::")) else { return false }
        let doubleColons = s.components(separatedBy: "::").count - 1
        guard doubleColons <= 1 else { return false }
        for (i, g) in groups.enumerated() {
            if g.isEmpty { continue }
            if i == groups.count - 1 && isIPv4(g) { continue }
            guard g.count <= 4, g.unicodeScalars.allSatisfy({ ("0"..."9").contains($0) || ("a"..."f").contains($0) || ("A"..."F").contains($0) }) else {
                return false
            }
        }
        return doubleColons == 1 || groups.count == 8 || (groups.count == 7 && isIPv4(groups.last!))
    }
}
