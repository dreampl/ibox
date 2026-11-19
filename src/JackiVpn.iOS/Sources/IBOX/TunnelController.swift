#if os(iOS)
import Foundation
import NetworkExtension
import IBOXCore

/// Owns the app's single NETunnelProviderManager (the "IBOX" VPN profile in iOS Settings).
@MainActor
final class TunnelController: ObservableObject {
    @Published private(set) var status: NEVPNStatus = .invalid
    private var manager: NETunnelProviderManager?
    private var observer: NSObjectProtocol?

    var isActive: Bool { status == .connected || status == .connecting || status == .reasserting }

    var statusText: String {
        switch status {
        case .connected: L10n.connected
        case .connecting, .reasserting: L10n.connecting
        case .disconnecting: L10n.disconnecting
        default: L10n.notConnected
        }
    }

    func load() {
        NETunnelProviderManager.loadAllFromPreferences { [weak self] managers, _ in
            Task { @MainActor in
                guard let self else { return }
                self.manager = managers?.first
                self.observe()
            }
        }
    }

    func start(name: String, config: String, completion: @escaping @MainActor (Error?) -> Void) {
        let manager = self.manager ?? NETunnelProviderManager()
        let proto = (manager.protocolConfiguration as? NETunnelProviderProtocol) ?? NETunnelProviderProtocol()
        proto.providerBundleIdentifier = AppShared.tunnelBundleId
        proto.serverAddress = name
        manager.protocolConfiguration = proto
        manager.localizedDescription = "IBOX"
        manager.isEnabled = true

        manager.saveToPreferences { [weak self] error in
            Task { @MainActor in
                if let error { completion(error); return }
                // Reload after save, or the first start after creating the profile fails with "configuration is stale".
                manager.loadFromPreferences { error in
                    Task { @MainActor in
                        if let error { completion(error); return }
                        self?.manager = manager
                        self?.observe()
                        do {
                            try manager.connection.startVPNTunnel(options: [AppShared.configOptionKey: config as NSString])
                            completion(nil)
                        } catch {
                            completion(error)
                        }
                    }
                }
            }
        }
    }

    func stop() {
        manager?.connection.stopVPNTunnel()
    }

    private func observe() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        guard let connection = manager?.connection else { status = .invalid; return }
        status = connection.status
        observer = NotificationCenter.default.addObserver(forName: .NEVPNStatusDidChange, object: connection, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.status = connection.status }
        }
    }
}
#endif
