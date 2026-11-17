// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "IBOX",
    platforms: [.iOS(.v17)],
    products: [
        .library(name: "IBOX", targets: ["IBOX"]),
    ],
    targets: [
        .target(name: "IBOX"),
    ]
)
