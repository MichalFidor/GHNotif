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

do {
    let base = AppConfig(users: ["alice"], extraQualifiers: "org:acme")
    check(SearchQueryBuilder.searches(for: base).map(\.reason) == [.watchedAuthor, .mention], "mentions on, review requests off by default")
    check(SearchQueryBuilder.searches(for: base).last?.query == "is:pr is:open draft:false mentions:@me org:acme",
          "mention query keeps scope and includes every author")
    var all = base
    all.includeReviewRequests = true
    check(SearchQueryBuilder.searches(for: all).last?.query == "is:pr is:open draft:false user-review-requested:@me -author:@me org:acme",
          "review requests are personal only")
    var none = AppConfig(users: [])
    none.includeMentions = false
    check(!none.hasSearches && SearchQueryBuilder.searches(for: none).isEmpty, "nothing to search")
    check(AppConfig(users: []).hasSearches, "mentions alone still search")
}

// MARK: GitHubClient parsing

do {
    let json = """
    {"data":{"viewer":{"login":"me"},"search":{"pageInfo":{"hasNextPage":true,"endCursor":"abc"},"nodes":[
      {"id":"PR_1","number":12,"title":"Fix","url":"https://github.com/o/r/pull/12",
       "createdAt":"2026-10-20T10:00:00Z","updatedAt":"2026-10-21T10:00:00Z","reviewDecision":"APPROVED",
       "author":{"login":"alice","avatarUrl":"https://avatars.githubusercontent.com/u/1"},
       "repository":{"nameWithOwner":"o/r"},
       "additions":14,"deletions":3,"labels":{"nodes":[{"name":"bug"},{"name":"triage"}]},
       "commits":{"nodes":[{"commit":{"oid":"abc123","author":{"user":{"login":"dave"}},"statusCheckRollup":{"state":"FAILURE"}}}]},
       "comments":{"nodes":[{"id":"comment-1","createdAt":"2026-10-22T10:00:00Z","author":{"__typename":"User","login":"bob"}},
                            {"createdAt":"2026-10-22T11:00:00Z","author":{"__typename":"Bot","login":"ci-bot"}},
                            {"createdAt":"2026-10-22T12:00:00Z","author":null}]},
       "reviews":{"nodes":[{"id":"review-1","submittedAt":"2026-10-23T10:00:00Z","state":"CHANGES_REQUESTED","author":{"__typename":"User","login":"carol"}},
                           {"id":"review-2","submittedAt":"2026-10-23T11:00:00Z","state":"APPROVED","commit":{"oid":"abc123"},"author":{"__typename":"User","login":"me"}},
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
    check(first.additions == 14 && first.deletions == 3 && first.labels == ["bug", "triage"],
          "change size and labels parsed")
    check(first.activity.map(\.login) == ["bob", "carol", "me"],
          "human comments and submitted reviews kept, bots/ghosts/pending skipped")
    check(first.activity.map(\.kind) == [.comment, .changesRequested, .approved], "activity kinds parsed")
    check(first.headCommitAuthor == "dave", "head commit author parsed")
    let since = DismissalSnapshot(dismissedAt: Date(timeIntervalSince1970: 0), headCommitOID: "old")
    check(first.changes(since: since, viewer: "me") == ["dave — nowe commity", "bob — komentarz", "carol — prośba o zmiany"],
          "changes describe who did what, excluding the viewer")
    let activeSnapshot = first.notificationSnapshot(preserving: nil)
    let newReview = Activity(login: "erin", date: Date(), kind: .approved, id: "review-new")
    let activeUpdate = PullRequest(
        id: first.id, number: first.number, title: first.title, url: first.url, repository: first.repository,
        author: first.author, authorAvatarURL: first.authorAvatarURL, createdAt: first.createdAt,
        updatedAt: first.updatedAt, reviewDecision: first.reviewDecision, checks: first.checks,
        headCommitOID: first.headCommitOID, activity: first.activity + [newReview], reasons: first.reasons,
        additions: first.additions, deletions: first.deletions, labels: first.labels, reviews: first.reviews,
        headCommitAuthor: first.headCommitAuthor
    )
    check(activeUpdate.newActivity(since: activeSnapshot, viewer: "me") == ["erin — zatwierdzenie ✅"],
          "new activity on an active PR is detected")
    let updatedSnapshot = activeUpdate.notificationSnapshot(preserving: activeSnapshot)
    check(activeUpdate.newActivity(since: updatedSnapshot, viewer: "me").isEmpty,
          "activity snapshots prevent duplicate alerts")
    check(first.hasViewerReviewedCurrentHead("ME"), "review on current head identified case-insensitively")
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

do {
    let pr = PullRequest(id: "filtered", number: 2, title: "t",
        url: URL(string: "https://github.com/acme/archive/pull/2")!, repository: "acme/archive", author: "a",
        authorAvatarURL: nil, createdAt: Date(), updatedAt: Date(), reviewDecision: nil, checks: nil,
        labels: ["Do-Not-Review"])
    var config = AppConfig(users: [])
    config.excludedRepositories = ["ACME/ARCHIVE"]
    check(!config.includes(pr), "repository exclusion ignores case")
    config.excludedRepositories = []
    config.excludedLabels = ["do-not-review"]
    check(!config.includes(pr), "label exclusion ignores case")
    config.excludedLabels = ["wip"]
    check(config.includes(pr), "unmatched exclusions keep PR visible")
}

check(GitHubClient.ssoBlockedOrgIDs(from: "partial-results; organizations=21955855,20582480") == ["21955855", "20582480"],
      "SSO partial-results header parsed")
check(GitHubClient.ssoBlockedOrgIDs(from: "required; url=https://github.com/orgs/x/sso").isEmpty, "non-partial SSO header ignored")
check(GitHubClient.ssoBlockedOrgIDs(from: nil).isEmpty, "missing SSO header")

// MARK: Merging search reasons

do {
    func pr(_ id: String) -> PullRequest {
        PullRequest(id: id, number: 1, title: "t", url: URL(string: "https://github.com/o/r/pull/1")!, repository: "o/r",
                    author: "a", authorAvatarURL: nil, createdAt: Date(), updatedAt: Date(), reviewDecision: nil, checks: nil)
    }
    let merged = GitHubClient.merge([
        (SearchOutcome(pullRequests: [pr("A"), pr("B")], ssoBlockedOrgIDs: ["1"], viewerLogin: "me"), .watchedAuthor),
        (SearchOutcome(pullRequests: [pr("B"), pr("C")], ssoBlockedOrgIDs: ["2"]), .mention),
    ])
    let reasons = Dictionary(uniqueKeysWithValues: merged.pullRequests.map { ($0.id, $0.reasons) })
    check(reasons == ["A": [.watchedAuthor], "B": [.watchedAuthor, .mention], "C": [.mention]], "reasons merged per PR")
    check(merged.ssoBlockedOrgIDs == ["1", "2"] && merged.viewerLogin == "me", "SSO and viewer merged")
}

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
    let twice = pr(oid: "a1", activity: [after, Activity(login: "bob", date: dismissedAt.addingTimeInterval(90))])
    check(twice.changes(since: snapshot, viewer: "me") == ["bob — komentarz"], "repeated activity collapsed")
    check(pr(oid: "b2", activity: []).changes(since: snapshot, viewer: "me") == ["Nowe commity"], "push without known author")
    check(summarizeChanges(["a", "b", "c", "d", "e"]) == "a\nb\nc\n+2 więcej", "long change lists truncated")
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
do {
    let legacy = try JSONDecoder().decode(AppConfig.self, from: Data(#"{"users":["a"],"extraQualifiers":"","refreshMinutes":7,"notify":false}"#.utf8))
    check(legacy.refreshMinutes == 7 && !legacy.notify && legacy.includeMentions && !legacy.includeReviewRequests,
          "config from an older version gets new defaults")
} catch {
    check(false, "legacy config threw \(error)")
}

do {
    func release(_ tag: String, draft: Bool = false, prerelease: Bool = false,
                 url: String = "https://github.com/MichalFidor/GHNotif/releases/tag/v0.2.0") throws -> GitHubRelease {
        let data = try JSONSerialization.data(withJSONObject: [
            "tag_name": tag, "html_url": url, "draft": draft, "prerelease": prerelease
        ])
        return try JSONDecoder().decode(GitHubRelease.self, from: data)
    }
    check(try release("v0.2.0").isNewer(than: "0.1.0"), "new release detected")
    check(try !release("v0.1.0").isNewer(than: "0.1.0"), "same release ignored")
    check(try !release("v0.0.9").isNewer(than: "0.1.0"), "older release ignored")
    check(try release("v0.10.0").isNewer(than: "0.9.0"), "numeric rather than lexical comparison")
    check(try !release("v0.2.0", draft: true).isNewer(than: "0.1.0"), "draft ignored")
    check(try !release("v0.2.0", prerelease: true).isNewer(than: "0.1.0"), "prerelease ignored")
    check(ReleaseVersion("0.1") == nil && ReleaseVersion("0.2.0-beta") == nil, "invalid version rejected")
    do {
        _ = try release("v0.2.0", url: "https://example.com/").isNewer(than: "0.1.0")
        check(false, "foreign update URL must fail")
    } catch {}
}

if failures > 0 {
    print("\(failures) check(s) failed")
    exit(1)
}
print("All checks passed")
