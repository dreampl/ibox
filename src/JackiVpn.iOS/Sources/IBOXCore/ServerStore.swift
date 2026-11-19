import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct Subscription: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var name: String
    /// nil for profiles pasted by hand.
    public var url: String?
    public var profiles: [Profile]
    public var usage: SubscriptionParser.UsageInfo?
    public var updatedAt: Date?

    public init(id: String, name: String, url: String?, profiles: [Profile] = []) {
        self.id = id
        self.name = name
        self.url = url
        self.profiles = profiles
    }
}

/// Saved subscriptions and the selected server, as one JSON file. On iOS the file lives in the
/// App Group container so the packet tunnel extension can read the same data.
public struct ServerState: Codable, Sendable {
    public var subscriptions: [Subscription] = []
    public var selectedProfileId: String?
    public init() {}

    public var allProfiles: [Profile] { subscriptions.flatMap(\.profiles) }
    public var selectedProfile: Profile? {
        allProfiles.first { $0.id == selectedProfileId } ?? allProfiles.first
    }

    public static let manualSubscriptionId = "manual"

    /// Adds pasted links to the "Manual" group. Returns how many were new.
    @discardableResult
    public mutating func addManual(_ profiles: [Profile]) -> Int {
        var sub = subscriptions.first { $0.id == Self.manualSubscriptionId }
            ?? Subscription(id: Self.manualSubscriptionId, name: "Manual", url: nil)
        let existing = Set(sub.profiles.map(\.id))
        let fresh = profiles.filter { !existing.contains($0.id) }
        sub.profiles += fresh
        upsert(sub)
        return fresh.count
    }

    public mutating func upsert(_ sub: Subscription) {
        if let i = subscriptions.firstIndex(where: { $0.id == sub.id }) {
            subscriptions[i] = sub
        } else {
            subscriptions.append(sub)
        }
    }

    public mutating func remove(subscriptionId: String) {
        subscriptions.removeAll { $0.id == subscriptionId }
    }
}

public final class ServerStore: @unchecked Sendable {
    public let fileURL: URL
    private let lock = NSLock()

    public init(fileURL: URL) { self.fileURL = fileURL }

    public func load() -> ServerState {
        lock.lock(); defer { lock.unlock() }
        guard let data = try? Data(contentsOf: fileURL),
              let state = try? JSONDecoder().decode(ServerState.self, from: data) else { return ServerState() }
        return state
    }

    public func save(_ state: ServerState) throws {
        lock.lock(); defer { lock.unlock() }
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try JSONEncoder().encode(state)
        #if os(iOS)
        // Readable by the tunnel extension while the device is locked after first unlock.
        try data.write(to: fileURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        #else
        try data.write(to: fileURL, options: .atomic)
        #endif
    }
}

public enum SubscriptionFetcher {
    public struct FetchError: Error, CustomStringConvertible {
        public let description: String
    }

    /// Downloads and parses a subscription. Only http(s) URLs; body capped at `SubscriptionParser.maxBodyBytes`.
    public static func fetch(_ urlText: String, name: String? = nil, session: URLSession = .shared) async throws -> (Subscription, [String]) {
        guard let url = URL(string: urlText.trimmingCharacters(in: .whitespacesAndNewlines)),
              let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http", url.host != nil else {
            throw FetchError(description: "Not an http(s) subscription URL.")
        }
        var request = URLRequest(url: url, timeoutInterval: 30)
        // Panels (Marzban/PasarGuard/3x-ui) return the plain link list to v2ray-style clients.
        request.setValue("v2rayNG/1.9 IBOX-iOS", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw FetchError(description: "Server answered \((response as? HTTPURLResponse)?.statusCode ?? 0).")
        }
        guard data.count <= SubscriptionParser.maxBodyBytes else { throw FetchError(description: "Subscription is too large.") }
        let body = String(decoding: data, as: UTF8.self)
        let parsed = SubscriptionParser.parse(body)
        guard !parsed.profiles.isEmpty else {
            throw FetchError(description: parsed.errors.first ?? "No supported servers in this subscription.")
        }

        var sub = Subscription(id: Profile.stableId(for: url.absoluteString),
                               name: name ?? profileTitle(http) ?? url.host ?? "Subscription",
                               url: url.absoluteString, profiles: parsed.profiles)
        sub.usage = SubscriptionParser.parseUserInfo(http.value(forHTTPHeaderField: "subscription-userinfo"))
        sub.updatedAt = Date()
        return (sub, parsed.errors)
    }

    /// `profile-title: base64:SUJPWA==` or plain text.
    static func profileTitle(_ http: HTTPURLResponse) -> String? {
        guard let v = http.value(forHTTPHeaderField: "profile-title")?.trimmingCharacters(in: .whitespaces), !v.isEmpty else { return nil }
        if v.lowercased().hasPrefix("base64:") { return Base64Text.decodeToString(String(v.dropFirst(7)), maxChars: 1024) }
        return v
    }
}
