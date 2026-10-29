import Foundation

public enum ReviewDecision: String, Decodable, Sendable {
    case approved = "APPROVED"
    case changesRequested = "CHANGES_REQUESTED"
    case reviewRequired = "REVIEW_REQUIRED"
}

public enum CheckState: String, Decodable, Sendable {
    case success = "SUCCESS"
    case failure = "FAILURE"
    case error = "ERROR"
    case pending = "PENDING"
    case expected = "EXPECTED"
}

public enum MatchReason: String, Hashable, Sendable, CaseIterable {
    case watchedAuthor
    case mention
    case reviewRequested
}

public struct PullRequestReview: Hashable, Sendable {
    public let login: String
    public let commitOID: String?
    public let submittedAt: Date?

    public init(login: String, commitOID: String?, submittedAt: Date?) {
        self.login = login
        self.commitOID = commitOID
        self.submittedAt = submittedAt
    }
}

public struct PullRequest: Identifiable, Hashable, Sendable {
    public let id: String
    public let number: Int
    public let title: String
    public let url: URL
    public let repository: String
    public let author: String
    public let authorAvatarURL: URL?
    public let createdAt: Date
    public let updatedAt: Date
    public let reviewDecision: ReviewDecision?
    public let checks: CheckState?
    public let headCommitOID: String?
    /// GitHub login of the head commit's author; the pusher isn't exposed by the API.
    public let headCommitAuthor: String?
    public let additions: Int
    public let deletions: Int
    public let labels: [String]
    public let reviews: [PullRequestReview]
    /// Recent comments and reviews by humans (bots excluded).
    public let activity: [Activity]
    /// Why the PR is listed; a PR can match several searches.
    public var reasons: Set<MatchReason>

    public init(
        id: String, number: Int, title: String, url: URL, repository: String, author: String,
        authorAvatarURL: URL?, createdAt: Date, updatedAt: Date,
        reviewDecision: ReviewDecision?, checks: CheckState?,
        headCommitOID: String? = nil, activity: [Activity] = [], reasons: Set<MatchReason> = [.watchedAuthor],
        additions: Int = 0, deletions: Int = 0, labels: [String] = [], reviews: [PullRequestReview] = [],
        headCommitAuthor: String? = nil
    ) {
        self.id = id
        self.number = number
        self.title = title
        self.url = url
        self.repository = repository
        self.author = author
        self.authorAvatarURL = authorAvatarURL
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.reviewDecision = reviewDecision
        self.checks = checks
        self.headCommitOID = headCommitOID
        self.headCommitAuthor = headCommitAuthor
        self.activity = activity
        self.reasons = reasons
        self.additions = additions
        self.deletions = deletions
        self.labels = labels
        self.reviews = reviews
    }

    /// True when, since the dismissal, someone pushed commits or someone other than `viewer` commented/reviewed.
    public func hasNewActivity(since snapshot: DismissalSnapshot, viewer: String?) -> Bool {
        if let old = snapshot.headCommitOID, let current = headCommitOID, old != current { return true }
        return activity.contains { item in
            item.date > snapshot.dismissedAt
                && (viewer.map { item.login.caseInsensitiveCompare($0) != .orderedSame } ?? true)
        }
    }

    /// Human-readable list of what changed since the snapshot, e.g. "✅ bob - zatwierdzenie".
    public func changes(since snapshot: DismissalSnapshot, viewer: String?) -> [String] {
        var lines: [String] = []
        if let old = snapshot.headCommitOID, let current = headCommitOID, old != current {
            lines.append(AlertEvent.commits.line(headCommitAuthor.map { L("\($0) - nowe commity", "\($0) - new commits") } ?? L("Nowe commity", "New commits")))
        }
        var seen = Set<String>()
        for item in activity.sorted(by: { $0.date < $1.date }) where item.date > snapshot.dismissedAt
            && (viewer.map { item.login.caseInsensitiveCompare($0) != .orderedSame } ?? true) {
            let line = item.kind.event.line("\(item.login) - \(item.kind.label)")
            if seen.insert(line.lowercased()).inserted { lines.append(line) }
        }
        return lines
    }

    public func newActivity(since snapshot: NotificationSnapshot, viewer: String?) -> [String] {
        var lines: [String] = []
        if let old = snapshot.headCommitOID, let current = headCommitOID, old != current {
            lines.append(AlertEvent.commits.line(headCommitAuthor.map { L("\($0) - nowe commity", "\($0) - new commits") } ?? L("Nowe commity", "New commits")))
        }
        for item in activity.sorted(by: { $0.date < $1.date })
            where snapshot.activityDates[item.notificationKey] == nil
                && (viewer.map { item.login.caseInsensitiveCompare($0) != .orderedSame } ?? true) {
            lines.append(item.kind.event.line("\(item.login) - \(item.kind.label)"))
        }
        return lines
    }

    public func notificationSnapshot(preserving previous: NotificationSnapshot?) -> NotificationSnapshot {
        var activityDates = previous?.activityDates ?? [:]
        for item in activity { activityDates[item.notificationKey] = item.date }
        if activityDates.count > 200 {
            activityDates = Dictionary(uniqueKeysWithValues:
                activityDates.sorted { $0.value > $1.value }.prefix(200).map { ($0.key, $0.value) })
        }
        return NotificationSnapshot(
            headCommitOID: headCommitOID ?? previous?.headCommitOID,
            activityDates: activityDates
        )
    }

    public func hasViewerReviewedCurrentHead(_ viewer: String?) -> Bool {
        viewerReviewAt(viewer) != nil
    }

    public func viewerReviewAt(_ viewer: String?) -> Date? {
        guard let viewer, let headCommitOID else { return nil }
        return reviews.filter {
            $0.login.caseInsensitiveCompare(viewer) == .orderedSame && $0.commitOID == headCommitOID
        }.compactMap(\.submittedAt).max()
        }
}

public enum ActivityKind: Hashable, Sendable {
    case comment
    case approved
    case changesRequested
    case reviewComment
    case reviewDismissed

    /// Maps a GitHub `PullRequestReviewState`.
    public init(reviewState: String?) {
        switch reviewState {
        case "APPROVED": self = .approved
        case "CHANGES_REQUESTED": self = .changesRequested
        case "DISMISSED": self = .reviewDismissed
        default: self = .reviewComment
        }
    }

    public var label: String {
        switch self {
        case .comment: L("komentarz", "comment")
        case .approved: L("zatwierdzenie", "approved")
        case .changesRequested: L("prośba o zmiany", "changes requested")
        case .reviewComment: L("komentarz w review", "review comment")
        case .reviewDismissed: L("odrzucone review", "review dismissed")
        }
    }

    public var event: AlertEvent {
        switch self {
        case .comment, .reviewComment: .comment
        case .approved: .approved
        case .changesRequested: .changesRequested
        case .reviewDismissed: .reviewDismissed
        }
    }
}

/// What a notification line is about; drives the GitHub-style emoji prefix and the notification icon.
public enum AlertEvent: String, CaseIterable, Hashable, Sendable {
    // Ordered by importance: the first event found in an alert picks its icon.
    case changesRequested, approved, reviewRequested, mention, reviewDismissed, comment, commits, newPullRequest, snoozeEnded, activity

    public var emoji: String {
        switch self {
        case .changesRequested: "🔴"
        case .approved: "✅"
        case .reviewRequested: "👀"
        case .mention: "📣"
        case .reviewDismissed: "🚫"
        case .comment: "💬"
        case .commits: "⬆️"
        case .newPullRequest: "🟢"
        case .snoozeEnded: "⏰"
        case .activity: "🔔"
        }
    }

    /// Name shown in the notification filter settings.
    public var title: String {
        switch self {
        case .changesRequested: L("Prośba o zmiany", "Changes requested")
        case .approved: L("Zatwierdzenie", "Approved")
        case .reviewRequested: L("Prośba o Twoje review", "Review requested from you")
        case .mention: L("Wzmianka", "Mention")
        case .reviewDismissed: L("Odrzucone review", "Review dismissed")
        case .comment: L("Komentarz", "Comment")
        case .commits: L("Nowe commity", "New commits")
        case .newPullRequest: L("Nowy PR", "New PR")
        case .snoozeEnded: L("Koniec odłożenia", "Snooze ended")
        case .activity: L("Inna aktywność", "Other activity")
        }
    }

    public func line(_ text: String) -> String { "\(emoji) \(text)" }

    /// Event of a single line, recognised by its emoji prefix.
    public static func of(_ line: String) -> AlertEvent {
        allCases.first { line.hasPrefix($0.emoji + " ") } ?? .activity
    }

    /// Most important event among the lines.
    public static func primary(in lines: [String]) -> AlertEvent {
        let events = Set(lines.map(of))
        return allCases.first(where: events.contains) ?? .activity
    }
}

public struct Activity: Hashable, Sendable {
    public let login: String
    public let date: Date
    public let kind: ActivityKind
    public let id: String?

    public init(login: String, date: Date, kind: ActivityKind = .comment, id: String? = nil) {
        self.login = login
        self.date = date
        self.kind = kind
        self.id = id
    }

    public var notificationKey: String {
        id ?? "\(login.lowercased())|\(kind.key)|\(Int64(date.timeIntervalSince1970 * 1_000_000))"
    }
}

public struct NotificationSnapshot: Codable, Hashable, Sendable {
    public var headCommitOID: String?
    public var activityDates: [String: Date]

    public init(headCommitOID: String?, activityDates: [String: Date] = [:]) {
        self.headCommitOID = headCommitOID
        self.activityDates = activityDates
    }
}

private extension ActivityKind {
    var key: String {
        switch self {
        case .comment: "comment"
        case .approved: "approved"
        case .changesRequested: "changes-requested"
        case .reviewComment: "review-comment"
        case .reviewDismissed: "review-dismissed"
        }
    }
}

/// A PR to announce together with the reasons shown in the notification body.
public struct PullRequestAlert: Sendable {
    public let pullRequest: PullRequest
    public let details: [String]
    public let event: AlertEvent

    public init(pullRequest: PullRequest, details: [String]) {
        self.pullRequest = pullRequest
        self.details = details
        self.event = AlertEvent.primary(in: details)
    }

    public static func new(_ pr: PullRequest) -> PullRequestAlert {
        let detail = pr.reasons.contains(.reviewRequested) ? AlertEvent.reviewRequested.line(L("Poproszono Cię o review", "Your review was requested"))
            : pr.reasons.contains(.mention) ? AlertEvent.mention.line(L("Wspomniano Cię w PR-ze", "You were mentioned"))
            : AlertEvent.newPullRequest.line(L("Nowy PR od \(pr.author)", "New PR by \(pr.author)"))
        return PullRequestAlert(pullRequest: pr, details: [detail])
    }
}

/// Notification body lines: at most `limit` entries, with a "+N więcej" suffix for the rest.
public func summarizeChanges(_ lines: [String], limit: Int = 3) -> String {
    guard lines.count > limit else { return lines.joined(separator: "\n") }
    return (lines.prefix(limit) + [L("+\(lines.count - limit) więcej", "+\(lines.count - limit) more")]).joined(separator: "\n")
}

/// State of a PR at the moment it was dismissed; newer activity brings the PR back.
public struct DismissalSnapshot: Codable, Hashable, Sendable {
    public var dismissedAt: Date
    public var headCommitOID: String?

    public init(dismissedAt: Date, headCommitOID: String?) {
        self.dismissedAt = dismissedAt
        self.headCommitOID = headCommitOID
    }
}

public struct SearchOutcome: Sendable {
    public let pullRequests: [PullRequest]
    /// IDs of organizations whose results GitHub hid because the token isn't SAML SSO-authorized for them.
    public let ssoBlockedOrgIDs: Set<String>
    /// Login of the token owner, used to ignore the user's own comments.
    public let viewerLogin: String?

    public init(pullRequests: [PullRequest], ssoBlockedOrgIDs: Set<String>, viewerLogin: String? = nil) {
        self.pullRequests = pullRequests
        self.ssoBlockedOrgIDs = ssoBlockedOrgIDs
        self.viewerLogin = viewerLogin
    }
}
