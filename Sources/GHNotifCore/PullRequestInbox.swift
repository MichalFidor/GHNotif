import Foundation

public struct PullRequestInbox: Codable, Sendable {
    public struct Entry: Codable, Sendable {
        var snapshot: NotificationSnapshot
        public private(set) var isNew = false
        var activity: [Activity] = []
        var hasNewCommits = false
        var commitAuthor: String?

        public var isUnread: Bool { isNew || hasNewCommits || !activity.isEmpty }

        public var details: [String] {
            var lines: [String] = []
            if hasNewCommits {
                lines.append(AlertEvent.commits.line(commitAuthor.map {
                    L("\($0) - nowe commity", "\($0) - new commits")
                } ?? L("Nowe commity", "New commits")))
            }
            lines += activity.map { $0.kind.event.line("\($0.login) - \($0.kind.label)") }
            return lines
        }
    }

    public private(set) var entries: [String: Entry] = [:]
    private var initialized = false

    public init() {}

    public mutating func observe(_ prs: [PullRequest], viewer: String?, baseline: Bool = false) {
        for pr in prs {
            guard var entry = entries[pr.id] else {
                entries[pr.id] = Entry(snapshot: pr.notificationSnapshot(preserving: nil),
                                       isNew: initialized && !baseline)
                continue
            }
            if let old = entry.snapshot.headCommitOID, let current = pr.headCommitOID, old != current {
                entry.hasNewCommits = true
                entry.commitAuthor = pr.headCommitAuthor
            }
            for item in pr.activity.sorted(by: { $0.date < $1.date })
                where entry.snapshot.activityDates[item.notificationKey] == nil
                    && (viewer.map { item.login.caseInsensitiveCompare($0) != .orderedSame } ?? true) {
                if !entry.activity.contains(where: {
                    $0.login.caseInsensitiveCompare(item.login) == .orderedSame && $0.kind == item.kind
                }) {
                    entry.activity.append(item)
                }
            }
            entry.snapshot = pr.notificationSnapshot(preserving: entry.snapshot)
            entries[pr.id] = entry
        }
        initialized = true
    }

    public mutating func markRead(_ pr: PullRequest) {
        entries[pr.id] = Entry(snapshot: pr.notificationSnapshot(preserving: entries[pr.id]?.snapshot))
    }
}
