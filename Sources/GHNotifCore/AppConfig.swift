import Foundation

public struct AppConfig: Codable, Equatable, Sendable {
    public var users: [String]
    /// Extra search qualifiers, e.g. `org:my-org`. Empty means every repository the token can see.
    public var extraQualifiers: String
    public var refreshMinutes: Int
    public var notify: Bool

    public init(users: [String], extraQualifiers: String = "", refreshMinutes: Int = 5, notify: Bool = true) {
        self.users = users
        self.extraQualifiers = extraQualifiers
        self.refreshMinutes = refreshMinutes
        self.notify = notify
    }

    public static let `default` = AppConfig(users: [])
}

public struct ConfigStore: Sendable {
    public let fileURL: URL

    public init(fileURL: URL = ConfigStore.defaultURL) {
        self.fileURL = fileURL
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("GHNotif", isDirectory: true)
            .appendingPathComponent("config.json")
    }

    /// Returns the default config only when no file exists; a corrupt file is reported instead of silently replaced.
    public func load() throws -> AppConfig {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return .default }
        return try JSONDecoder().decode(AppConfig.self, from: Data(contentsOf: fileURL))
    }

    public func save(_ config: AppConfig) throws {
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(config).write(to: fileURL, options: .atomic)
    }
}
