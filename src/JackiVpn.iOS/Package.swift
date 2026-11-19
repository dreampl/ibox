// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "IBOX",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        // xtool bundles library products into the app (IBOX) and its extensions (see xtool.yml).
        .library(name: "IBOX", targets: ["IBOX"]),
        .library(name: "PacketTunnel", targets: ["PacketTunnel"]),
    ],
    targets: [
        // Platform-neutral: parsing, sing-box config generation, storage. Builds and tests on Linux/Windows too.
        .target(name: "IBOXCore"),
        .target(name: "IBOX", dependencies: ["IBOXCore"]),
        .target(name: "PacketTunnel", dependencies: ["IBOXCore"]),
        .testTarget(name: "IBOXCoreTests", dependencies: ["IBOXCore"]),
    ],
    swiftLanguageModes: [.v5]
)
