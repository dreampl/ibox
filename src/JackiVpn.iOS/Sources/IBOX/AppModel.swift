#if os(iOS)
import Foundation
import IBOXCore

@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var state: ServerState
    @Published var message: String?
    @Published var busy = false
    let tunnel = TunnelController()

    private let store = ServerStore(fileURL: AppShared.stateFileURL())

    init() {
        state = store.load()
        tunnel.load()
    }

    var selected: Profile? { state.selectedProfile }

    func select(_ p: Profile) {
        state.selectedProfileId = p.id
        persist()
    }

    /// Accepts a subscription URL or pasted share links (one per line, or a Base64 list).
    func add(_ input: String) async {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        busy = true
        defer { busy = false }

        let lower = text.lowercased()
        if (lower.hasPrefix("https://") || lower.hasPrefix("http://")) && !text.contains("\n") {
            do {
                let (sub, _) = try await SubscriptionFetcher.fetch(text)
                state.upsert(sub)
                if state.selectedProfileId == nil { state.selectedProfileId = sub.profiles.first?.id }
                message = L10n.added(sub.profiles.count)
                persist()
            } catch {
                message = "\(error)"
            }
            return
        }

        let parsed = SubscriptionParser.parse(text)
        guard !parsed.profiles.isEmpty else {
            message = parsed.errors.first ?? L10n.nothingFound
            return
        }
        let n = state.addManual(parsed.profiles)
        if state.selectedProfileId == nil { state.selectedProfileId = parsed.profiles.first?.id }
        message = L10n.added(n)
        persist()
    }

    func refresh(_ sub: Subscription) async {
        guard let url = sub.url else { return }
        busy = true
        defer { busy = false }
        do {
            var (fresh, _) = try await SubscriptionFetcher.fetch(url, name: sub.name)
            fresh.id = sub.id
            state.upsert(fresh)
            persist()
        } catch {
            message = "\(error)"
        }
    }

    func remove(_ sub: Subscription) {
        state.remove(subscriptionId: sub.id)
        persist()
    }

    func toggleConnection() {
        if tunnel.isActive {
            tunnel.stop()
            return
        }
        guard let p = selected else { message = L10n.noServers; return }
        do {
            let config = try SingBoxConfig.json(p)
            tunnel.start(name: p.displayName, config: config) { [weak self] error in
                if let error { self?.message = error.localizedDescription }
            }
        } catch {
            message = "\(error)"
        }
    }

    private func persist() {
        do { try store.save(state) } catch { message = error.localizedDescription }
    }
}
#endif
