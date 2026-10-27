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

    public init(
        id: String, number: Int, title: String, url: URL, repository: String, author: String,
        authorAvatarURL: URL?, createdAt: Date, updatedAt: Date,
        reviewDecision: ReviewDecision?, checks: CheckState?
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
    }
}

public struct SearchOutcome: Sendable {
    public let pullRequests: [PullRequest]
    /// IDs of organizations whose results GitHub hid because the token isn't SAML SSO-authorized for them.
    public let ssoBlockedOrgIDs: Set<String>
}
