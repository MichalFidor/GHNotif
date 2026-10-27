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
    {"data":{"search":{"pageInfo":{"hasNextPage":true,"endCursor":"abc"},"nodes":[
      {"id":"PR_1","number":12,"title":"Fix","url":"https://github.com/o/r/pull/12",
       "createdAt":"2026-10-20T10:00:00Z","updatedAt":"2026-10-21T10:00:00Z","reviewDecision":"APPROVED",
       "author":{"login":"alice","avatarUrl":"https://avatars.githubusercontent.com/u/1"},
       "repository":{"nameWithOwner":"o/r"},
       "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"FAILURE"}}}]}},
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
