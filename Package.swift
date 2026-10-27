// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "GHNotif",
    platforms: [.macOS(.v15)],
    targets: [
        .target(name: "GHNotifCore"),
        .executableTarget(name: "GHNotif", dependencies: ["GHNotifCore"]),
        // Plain executable instead of a test target: Command Line Tools ship neither XCTest nor Swift Testing macros.
        .executableTarget(name: "CoreChecks", dependencies: ["GHNotifCore"], path: "Tests/CoreChecks"),
    ],
    swiftLanguageModes: [.v5]
)
