#if os(iOS)
import Foundation
import NetworkExtension
import IBOXCore
#if canImport(Libbox)
import Libbox
#endif

/// NetworkExtension packet tunnel. The sing-box core (libbox, built with gomobile from
/// github.com/SagerNet/sing-box `experimental/libbox`) owns the TUN: it calls back into a
/// `LibboxPlatformInterface` to apply `NEPacketTunnelNetworkSettings` and to get the utun fd.
///
/// Libbox.xcframework is not in the repo yet (it can only be built on macOS with Xcode, or
/// taken from a sing-box Apple release). Until it is, this provider refuses to start rather
/// than installing routes that would send traffic nowhere.
final class PacketTunnelProvider: NEPacketTunnelProvider {
    override func startTunnel(options: [String: NSObject]?, completionHandler: @escaping (Error?) -> Void) {
        let config: String
        if let fromApp = options?[AppShared.configOptionKey] as? String {
            config = fromApp
        } else if let p = ServerStore(fileURL: AppShared.stateFileURL()).load().selectedProfile,
                  let built = try? SingBoxConfig.json(p) {
            // Started from iOS Settings or On Demand: no options, so rebuild from the saved selection.
            config = built
        } else {
            completionHandler(TunnelError.noServer)
            return
        }

        #if canImport(Libbox)
        // TODO(libbox): mirror sing-box's own Apple client (clients/apple, ExtensionProvider.swift):
        // LibboxSetup(...), create a LibboxCommandServer, implement LibboxPlatformInterfaceProtocol
        // (openTun -> setTunnelNetworkSettings + packetFlow fd, autoDetectInterfaceControl, etc.),
        // then LibboxNewService(config, platform) and service.start().
        _ = config
        completionHandler(TunnelError.coreMissing)
        #else
        _ = config
        completionHandler(TunnelError.coreMissing)
        #endif
    }

    override func stopTunnel(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        completionHandler()
    }

    enum TunnelError: LocalizedError {
        case noServer, coreMissing
        var errorDescription: String? {
            switch self {
            case .noServer: L10n.noServers
            case .coreMissing: L10n.coreMissing
            }
        }
    }
}
#endif
