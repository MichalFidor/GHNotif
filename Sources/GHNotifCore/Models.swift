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
        additions: Int = 0, deletions: Int = 0, labels: [String] = [], reviews: [PullRequestReview] = []
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

public struct Activity: Hashable, Sendable {
    public let login: String
    public let date: Date

    public init(login: String, date: Date) {
        self.login = login
        self.date = date
    }
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
