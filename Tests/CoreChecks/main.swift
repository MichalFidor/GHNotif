import Foundation
import GHNotifCore

var failures = 0

func check(_ condition: @autoclosure () throws -> Bool, _ message: String, line: UInt = #line) {
    do {
        if try condition() { return }
        print("FAIL (line \(line)): \(message)")
    } catch {
        print("FAIL (line \(line)): \(message) – threw \(error)")
    }
    failures += 1
}

// MARK: SearchQueryBuilder

do {
    let queries = SearchQueryBuilder.queries(authors: (1...23).map { "user\($0)" }, batchSize: 10)
    check(queries.count == 3, "23 authors split into 3 batches")
    check(queries.allSatisfy { $0.hasPrefix("is:pr is:open draft:false ") && !$0.contains(" OR ") },
          "queries use base qualifiers and no OR operators")
    check(queries.last == "is:pr is:open draft:false author:user21 author:user22 author:user23", "last batch content")

    let normalized = SearchQueryBuilder.queries(authors: [" @Alice ", "alice", "", "bob"], extraQualifiers: " org:Acme ")
    check(normalized == ["is:pr is:open draft:false author:Alice author:bob org:Acme"], "normalization + extra qualifiers")
    check(SearchQueryBuilder.queries(authors: ["  "]).isEmpty, "no queries without authors")
}

// MARK: GitHubClient parsing

do {
    let json = """
    {"data":{"viewer":{"login":"me"},"search":{"pageInfo":{"hasNextPage":true,"endCursor":"abc"},"nodes":[
      {"id":"PR_1","number":12,"title":"Fix","url":"https://github.com/o/r/pull/12",
       "createdAt":"2026-10-20T10:00:00Z","updatedAt":"2026-10-21T10:00:00Z","reviewDecision":"APPROVED",
       "author":{"login":"alice","avatarUrl":"https://avatars.githubusercontent.com/u/1"},
       "repository":{"nameWithOwner":"o/r"},
       "commits":{"nodes":[{"commit":{"oid":"abc123","statusCheckRollup":{"state":"FAILURE"}}}]},
       "comments":{"nodes":[{"createdAt":"2026-10-22T10:00:00Z","author":{"__typename":"User","login":"bob"}},
                            {"createdAt":"2026-10-22T11:00:00Z","author":{"__typename":"Bot","login":"ci-bot"}},
                            {"createdAt":"2026-10-22T12:00:00Z","author":null}]},
       "reviews":{"nodes":[{"submittedAt":"2026-10-23T10:00:00Z","author":{"__typename":"User","login":"carol"}},
                           {"submittedAt":null,"author":{"__typename":"User","login":"pending"}}]}},
      {},
      {"id":"PR_2","number":3,"title":"No CI","url":"https://github.com/o/r/pull/3",
       "createdAt":"2026-10-20T10:00:00Z","updatedAt":"2026-10-20T10:00:00Z","reviewDecision":null,
       "author":null,"repository":{"nameWithOwner":"o/r"},
       "commits":{"nodes":[{"commit":{"statusCheckRollup":null}}]}}
    ]}}}
    """
    let page = try GitHubClient.parseSearchResponse(Data(json.utf8))
    check(page.hasNextPage && page.endCursor == "abc", "page info parsed")
    check(page.pullRequests.count == 2, "non-PR node skipped")
    let first = page.pullRequests[0]
    check(first.repository == "o/r" && first.author == "alice", "repository and author parsed")
    check(first.reviewDecision == .approved && first.checks == .failure, "review decision and CI state parsed")
    check(page.pullRequests[1].author == "ghost" && page.pullRequests[1].checks == nil, "missing author / CI tolerated")
    check(page.viewerLogin == "me", "viewer login parsed")
    check(first.headCommitOID == "abc123", "head commit parsed")
    check(first.activity.map(\.login) == ["bob", "carol"], "human comments and submitted reviews kept, bots/ghosts/pending skipped")
    check(page.pullRequests[1].activity.isEmpty && page.pullRequests[1].headCommitOID == nil, "missing activity tolerated")
} catch {
    check(false, "search response parsing threw \(error)")
}

do {
    _ = try GitHubClient.parseSearchResponse(Data(#"{"data":null,"errors":[{"message":"Bad query","type":"INVALID"}]}"#.utf8))
    check(false, "GraphQL errors should throw")
} catch let error as GitHubError {
    check(error == .graphQL("Bad query"), "GraphQL error message surfaced")
} catch {
    check(false, "unexpected error type \(error)")
}

check(GitHubClient.ssoBlockedOrgIDs(from: "partial-results; organizations=21955855,20582480") == ["21955855", "20582480"],
      "SSO partial-results header parsed")
check(GitHubClient.ssoBlockedOrgIDs(from: "required; url=https://github.com/orgs/x/sso").isEmpty, "non-partial SSO header ignored")
check(GitHubClient.ssoBlockedOrgIDs(from: nil).isEmpty, "missing SSO header")

// MARK: Dismissal reactivation

do {
    let dismissedAt = Date(timeIntervalSince1970: 1_000_000)
    func pr(oid: String?, activity: [Activity]) -> PullRequest {
        PullRequest(
            id: "PR", number: 1, title: "t", url: URL(string: "https://github.com/o/r/pull/1")!, repository: "o/r",
            author: "alice", authorAvatarURL: nil, createdAt: dismissedAt, updatedAt: dismissedAt,
            reviewDecision: nil, checks: nil, headCommitOID: oid, activity: activity
        )
    }
    let snapshot = DismissalSnapshot(dismissedAt: dismissedAt, headCommitOID: "a1")
    let before = Activity(login: "bob", date: dismissedAt.addingTimeInterval(-60))
    let after = Activity(login: "bob", date: dismissedAt.addingTimeInterval(60))
    let mine = Activity(login: "ME", date: dismissedAt.addingTimeInterval(60))
    check(!pr(oid: "a1", activity: [before]).hasNewActivity(since: snapshot, viewer: "me"), "old activity ignored")
    check(pr(oid: "b2", activity: []).hasNewActivity(since: snapshot, viewer: "me"), "new commit reactivates")
    check(pr(oid: "a1", activity: [after]).hasNewActivity(since: snapshot, viewer: "me"), "new comment by others reactivates")
    check(!pr(oid: "a1", activity: [mine]).hasNewActivity(since: snapshot, viewer: "me"), "own comment ignored (case-insensitive)")
    let unknownCommit = DismissalSnapshot(dismissedAt: dismissedAt, headCommitOID: nil)
    check(!pr(oid: "b2", activity: []).hasNewActivity(since: unknownCommit, viewer: "me"), "no commit baseline -> no commit reactivation")
    let encoded = try JSONEncoder().encode(snapshot)
    check(try JSONDecoder().decode(DismissalSnapshot.self, from: encoded) == snapshot, "snapshot round-trips")
} catch {
    check(false, "dismissal checks threw \(error)")
}

// MARK: ConfigStore

do {
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: dir) }
    let store = ConfigStore(fileURL: dir.appendingPathComponent("config.json"))
    check(try store.load() == .default, "default config when file missing")
    let config = AppConfig(users: ["a"], extraQualifiers: "org:x", refreshMinutes: 10, notify: false)
    try store.save(config)
    check(try store.load() == config, "config round-trips")

    let corrupt = dir.appendingPathComponent("corrupt.json")
    try Data("not json".utf8).write(to: corrupt)
    check((try? ConfigStore(fileURL: corrupt).load()) == nil, "corrupt config reported, not replaced with default")
} catch {
    check(false, "config store threw \(error)")
}

check(AppConfig.default.users.isEmpty, "default watch list is empty")

if failures > 0 {
    print("\(failures) check(s) failed")
    exit(1)
}
print("All checks passed")
