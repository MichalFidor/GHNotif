import Foundation

public struct AppConfig: Codable, Equatable, Sendable {
    public enum SortOrder: String, Codable, CaseIterable, Sendable {
        case recentlyUpdated
        case oldestFirst
        case largestChanges
    }

    public var users: [String]
    /// Extra search qualifiers, e.g. `org:my-org`. Empty means every repository the token can see.
    public var extraQualifiers: String
    public var refreshMinutes: Int
    public var notify: Bool
    /// Also show open PRs (by anyone) that @mention the signed-in user.
    public var includeMentions: Bool
    /// Also show open PRs where a review is requested from the signed-in user personally (not via a team).
    public var includeReviewRequests: Bool
    /// With `includeReviewRequests`, also show PRs where a review is requested from one of the user's teams.
    public var includeTeamReviewRequests: Bool
    /// Show draft PRs too; by default searches use `draft:false`.
    public var includeDrafts: Bool
    /// Hide PRs whose review decision is already "approved".
    public var hideApproved: Bool
    /// PRs open longer than this many days are highlighted; 0 turns it off.
    public var staleAfterDays: Int
    /// `github.com`, a GHE.com host, or a GitHub Enterprise Server host name.
    public var host: String
    public var autoDismissReviewed: Bool
    public var excludedRepositories: [String]
    public var excludedLabels: [String]
    public var sortOrder: SortOrder
    public var reviewReminder: ReviewReminderSettings
    public var language: AppLanguage
    public var notifications: NotificationPreferences

    public init(
        users: [String], extraQualifiers: String = "", refreshMinutes: Int = 5, notify: Bool = true,
        includeMentions: Bool = true, includeReviewRequests: Bool = false, includeTeamReviewRequests: Bool = false,
        includeDrafts: Bool = false, hideApproved: Bool = false, staleAfterDays: Int = 7,
        host: String = GitHubHost.dotCom, autoDismissReviewed: Bool = true,
        excludedRepositories: [String] = [], excludedLabels: [String] = [], sortOrder: SortOrder = .recentlyUpdated,
        reviewReminder: ReviewReminderSettings = ReviewReminderSettings(), language: AppLanguage = .system,
        notifications: NotificationPreferences = NotificationPreferences()
    ) {
        self.users = users
        self.extraQualifiers = extraQualifiers
        self.refreshMinutes = refreshMinutes
        self.notify = notify
        self.includeMentions = includeMentions
        self.includeReviewRequests = includeReviewRequests
        self.includeTeamReviewRequests = includeTeamReviewRequests
        self.includeDrafts = includeDrafts
        self.hideApproved = hideApproved
        self.staleAfterDays = staleAfterDays
        self.host = host
        self.autoDismissReviewed = autoDismissReviewed
        self.excludedRepositories = excludedRepositories
        self.excludedLabels = excludedLabels
        self.sortOrder = sortOrder
        self.reviewReminder = reviewReminder
        self.language = language
        self.notifications = notifications
    }

    /// Missing keys (config files from older versions) fall back to defaults.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let fallback = AppConfig.default
        users = try container.decode([String].self, forKey: .users)
        extraQualifiers = try container.decodeIfPresent(String.self, forKey: .extraQualifiers) ?? fallback.extraQualifiers
        refreshMinutes = try container.decodeIfPresent(Int.self, forKey: .refreshMinutes) ?? fallback.refreshMinutes
        notify = try container.decodeIfPresent(Bool.self, forKey: .notify) ?? fallback.notify
        includeMentions = try container.decodeIfPresent(Bool.self, forKey: .includeMentions) ?? fallback.includeMentions
        includeReviewRequests = try container.decodeIfPresent(Bool.self, forKey: .includeReviewRequests)
            ?? fallback.includeReviewRequests
        includeTeamReviewRequests = try container.decodeIfPresent(Bool.self, forKey: .includeTeamReviewRequests)
            ?? fallback.includeTeamReviewRequests
        includeDrafts = try container.decodeIfPresent(Bool.self, forKey: .includeDrafts) ?? fallback.includeDrafts
        hideApproved = try container.decodeIfPresent(Bool.self, forKey: .hideApproved) ?? fallback.hideApproved
        staleAfterDays = max(0, try container.decodeIfPresent(Int.self, forKey: .staleAfterDays) ?? fallback.staleAfterDays)
        host = GitHubHost.normalize(try container.decodeIfPresent(String.self, forKey: .host) ?? fallback.host)
        autoDismissReviewed = try container.decodeIfPresent(Bool.self, forKey: .autoDismissReviewed)
            ?? fallback.autoDismissReviewed
        excludedRepositories = try container.decodeIfPresent([String].self, forKey: .excludedRepositories) ?? []
        excludedLabels = try container.decodeIfPresent([String].self, forKey: .excludedLabels) ?? []
        sortOrder = try container.decodeIfPresent(SortOrder.self, forKey: .sortOrder) ?? fallback.sortOrder
        reviewReminder = try container.decodeIfPresent(ReviewReminderSettings.self, forKey: .reviewReminder)
            ?? fallback.reviewReminder
        // Configs from before the language option were always Polish; keep them that way.
        language = try container.decodeIfPresent(AppLanguage.self, forKey: .language) ?? .polish
        notifications = try container.decodeIfPresent(NotificationPreferences.self, forKey: .notifications)
            ?? fallback.notifications
    }

    /// True when at least one search would run.
    public var hasSearches: Bool { !users.isEmpty || includeMentions || includeReviewRequests }

    public var gitHubHost: GitHubHost { GitHubHost(host) }

    public func includes(_ pr: PullRequest) -> Bool {
        !(hideApproved && pr.reviewDecision == .approved)
            && !excludedRepositories.contains(where: { $0.caseInsensitiveCompare(pr.repository) == .orderedSame })
            && !pr.labels.contains { label in
                excludedLabels.contains(where: { $0.caseInsensitiveCompare(label) == .orderedSame })
            }
    }

    /// True when the PR has been open for at least `staleAfterDays` days.
    public func isStale(_ pr: PullRequest, now: Date = Date()) -> Bool {
        staleAfterDays > 0 && now.timeIntervalSince(pr.createdAt) >= Double(staleAfterDays) * 86_400
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
