import Foundation

public enum GitHubError: LocalizedError, Equatable {
    case unauthorized
    case http(Int, String)
    case graphQL(String)

    public var errorDescription: String? {
        switch self {
        case .unauthorized: return L("Token GitHub jest nieprawidłowy lub wygasł.", "The GitHub token is invalid or expired.")
        case let .http(code, body): return L("GitHub API zwróciło HTTP \(code): \(body.prefix(200))", "GitHub API returned HTTP \(code): \(body.prefix(200))")
        case let .graphQL(message): return "GitHub API: \(message)"
        }

    }
}

/// Refuses redirects that leave the original HTTPS host, so the Authorization header never reaches another server.
final class RedirectGuard: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest) async -> URLRequest? {
        guard let target = request.url, target.scheme == "https",
              target.host?.lowercased() == task.originalRequest?.url?.host?.lowercased() else { return nil }
        return request
    }
}

public struct GitHubClient: Sendable {
    public let token: String
    public let host: GitHubHost
    let session: URLSession

    public init(token: String, host: GitHubHost = GitHubHost(GitHubHost.dotCom), session: URLSession = .shared) {
        self.token = token
        self.host = host
        self.session = session
    }

    public func searchPullRequests(config: AppConfig) async throws -> SearchOutcome {
        let searches = SearchQueryBuilder.searches(for: config)
        let outcomes = try await withThrowingTaskGroup(of: (SearchOutcome, MatchReason).self) { group in
            for item in searches {
                group.addTask { (try await search(query: item.query), item.reason) }
            }
            return try await group.reduce(into: []) { $0.append($1) }
        }
        var merged = Self.merge(outcomes)
        if config.includeWatchedCommits, !config.users.isEmpty {
            let repositories = Self.repositoriesToScan(merged.pullRequests, excluding: config.excludedRepositories)
            var scans: [PageResults] = []
            for batch in SearchQueryBuilder.repositoryBatches(repositories: repositories, config: config) {
                scans.append(contentsOf: try await scanRepositories(batch, config: config))
            }
            merged = Self.addingWatchedCommitters(
                to: merged, scanned: scans.flatMap(\.pullRequests), commitAuthors: scans.reduce(into: [:]) {
                    $0.merge($1.commitAuthors) { $1 }
                },
                watched: config.users, ssoBlockedOrgIDs: scans.reduce(into: Set()) { $0.formUnion($1.ssoBlockedOrgIDs) }
            )
        }
        merged = SearchOutcome(
            pullRequests: merged.pullRequests.filter(config.includes),
            ssoBlockedOrgIDs: merged.ssoBlockedOrgIDs,
            viewerLogin: merged.viewerLogin
        )
        return merged
    }

    /// Combines search results, merging the match reasons of PRs found by several searches.
    public static func merge(_ outcomes: [(SearchOutcome, MatchReason)]) -> SearchOutcome {
        var byID: [String: PullRequest] = [:]
        var blocked = Set<String>()
        var viewer: String?
        for (outcome, reason) in outcomes {
            for var pr in outcome.pullRequests {
                pr.reasons = (byID[pr.id]?.reasons ?? []).union([reason])
                byID[pr.id] = pr
            }
            blocked.formUnion(outcome.ssoBlockedOrgIDs)
            viewer = viewer ?? outcome.viewerLogin
        }
        let sorted = byID.values.sorted { $0.updatedAt > $1.updatedAt }
        return SearchOutcome(pullRequests: sorted, ssoBlockedOrgIDs: blocked, viewerLogin: viewer)
    }

    /// Repositories of the found PRs, minus excluded ones, in a stable order.
    public static func repositoriesToScan(_ pullRequests: [PullRequest], excluding excluded: [String]) -> [String] {
        var seen = Set(excluded.map { $0.lowercased() })
        return pullRequests.map(\.repository).sorted().filter { seen.insert($0.lowercased()).inserted }
    }

    /// Marks scanned PRs that contain commits by a watched user other than the PR author, adding new ones to the list.
    public static func addingWatchedCommitters(
        to outcome: SearchOutcome, scanned: [PullRequest], commitAuthors: [String: [String]], watched: [String],
        ssoBlockedOrgIDs: Set<String> = []
    ) -> SearchOutcome {
        let watched = Set(watched.map { $0.lowercased() })
        var byID = Dictionary(outcome.pullRequests.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        for scannedPR in scanned {
            let author = scannedPR.author.lowercased()
            let hasWatchedCommit = (commitAuthors[scannedPR.id] ?? []).contains {
                let login = $0.lowercased()
                return login != author && watched.contains(login)
            }
            guard hasWatchedCommit else { continue }
            var pr = byID[scannedPR.id] ?? scannedPR
            pr.reasons = byID[scannedPR.id] == nil ? [.watchedCommitter] : pr.reasons.union([.watchedCommitter])
            byID[pr.id] = pr
        }
        return SearchOutcome(
            pullRequests: byID.values.sorted { $0.updatedAt > $1.updatedAt },
            ssoBlockedOrgIDs: outcome.ssoBlockedOrgIDs.union(ssoBlockedOrgIDs), viewerLogin: outcome.viewerLogin
        )
    }

    public func search(query: String, maxPages: Int = 10) async throws -> SearchOutcome {
        let pages = try await fetchPages(query: query, graphQL: Self.searchQuery, maxPages: maxPages)
        return SearchOutcome(pullRequests: pages.pullRequests, ssoBlockedOrgIDs: pages.ssoBlockedOrgIDs, viewerLogin: pages.viewerLogin)
    }

    struct PageResults: Sendable {
        var pullRequests: [PullRequest] = []
        var commitAuthors: [String: [String]] = [:]
        var ssoBlockedOrgIDs = Set<String>()
        var viewerLogin: String?
    }

    private struct SearchResultLimit: Error {
        let issueCount: Int
    }

    private func scanRepositories(_ repositories: [String], config: AppConfig) async throws -> [PageResults] {
        let query = SearchQueryBuilder.repositoryQuery(repositories, config: config)
        do {
            return [try await fetchPages(query: query, graphQL: Self.commitScanQuery, maxPages: 20,
                                         maximumIssueCount: 1_000)]
        } catch let limit as SearchResultLimit {
            guard repositories.count > 1 else {
                throw GitHubError.graphQL(L(
                    "Repozytorium \(repositories[0]) ma ponad \(limit.issueCount) otwartych PR-ów i nie może zostać w całości przeskanowane. Zawęź kwalifikatory lub wyłącz wyszukiwanie commitów obserwowanych.",
                    "Repository \(repositories[0]) has over \(limit.issueCount) open PRs and cannot be fully scanned. Narrow the qualifiers or turn off watched-commit search."
                ))
            }
            let middle = repositories.count / 2
            let firstHalf = try await scanRepositories(Array(repositories[..<middle]), config: config)
            let secondHalf = try await scanRepositories(Array(repositories[middle...]), config: config)
            return firstHalf + secondHalf
        }
    }

    private func fetchPages(
        query: String, graphQL: String, maxPages: Int = 10, maximumIssueCount: Int? = nil
    ) async throws -> PageResults {
        var cursor: String?
        var results = PageResults()
        for pageNumber in 0..<maxPages {
            var variables: [String: String] = ["q": query]
            if let cursor { variables["after"] = cursor }
            let (data, response) = try await post(query: graphQL, variables: variables)
            results.ssoBlockedOrgIDs.formUnion(Self.ssoBlockedOrgIDs(from: response.value(forHTTPHeaderField: "X-GitHub-SSO")))
            let page = try Self.parseSearchResponse(data)
            if let maximumIssueCount, page.issueCount > maximumIssueCount {
                throw SearchResultLimit(issueCount: page.issueCount)
            }
            results.pullRequests.append(contentsOf: page.pullRequests)
            results.commitAuthors.merge(page.commitAuthors) { $1 }
            results.viewerLogin = results.viewerLogin ?? page.viewerLogin
            guard page.hasNextPage, let next = page.endCursor else { break }
            guard pageNumber + 1 < maxPages else {
                throw GitHubError.graphQL(L("GitHub ma więcej wyników, niż można przeskanować.", "GitHub has more results than can be scanned."))
            }
            cursor = next
        }
        return results
    }

    /// Returns the canonical login for an existing user, or `nil` when no such user exists.
    public func canonicalLogin(for login: String) async throws -> String? {
        let (data, _) = try await post(
            query: "query($login: String!) { user(login: $login) { login } }",
            variables: ["login": login]
        )
        let decoded = try JSONDecoder().decode(GraphQLResponse<UserData>.self, from: data)
        if let user = decoded.data?.user { return user.login }
        if let errors = decoded.errors, !errors.allSatisfy({ $0.type == "NOT_FOUND" }) {
            throw GitHubError.graphQL(errors.map(\.message).joined(separator: "; "))
        }
        return nil
    }

    public func teamMembers(organization: String, team: String) async throws -> [String] {
        let safe = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")
        guard organization.unicodeScalars.allSatisfy(safe.contains),
              team.unicodeScalars.allSatisfy(safe.contains), !organization.isEmpty, !team.isEmpty else {
            throw GitHubError.graphQL(L("Podaj poprawną nazwę organizacji i slug zespołu.", "Enter a valid organization name and team slug."))
        }
        var members: [String] = []
        for page in 1...10 {
            let (data, http) = try await get("/orgs/\(organization)/teams/\(team)/members?per_page=100&page=\(page)")
            guard (200..<300).contains(http.statusCode) else {
                throw GitHubError.http(http.statusCode, String(decoding: data, as: UTF8.self))
            }
            let users = try JSONDecoder().decode([TeamMember].self, from: data)
            members.append(contentsOf: users.map(\.login))
            if users.count < 100 { return members }
        }
        throw GitHubError.graphQL(L("Zespół ma ponad 1000 członków; ogranicz import.", "The team has over 1000 members; narrow the import."))
    }

    /// REST GET relative to the host's API root; only 401 throws, other statuses are left to the caller.
    func get(_ path: String) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: host.restURL(path))
        request.httpMethod = "GET"
        request.setValue("bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("GHNotif", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await session.data(for: request, delegate: RedirectGuard())
        guard let http = response as? HTTPURLResponse else { throw GitHubError.http(0, L("Brak odpowiedzi HTTP", "No HTTP response")) }
        if http.statusCode == 401 { throw GitHubError.unauthorized }
        return (data, http)
    }

    private func post(query: String, variables: [String: String]) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: host.graphQLURL)
        request.httpMethod = "POST"
        request.setValue("bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("GHNotif", forHTTPHeaderField: "User-Agent")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["query": query, "variables": variables])
        let (data, response) = try await session.data(for: request, delegate: RedirectGuard())
        guard let http = response as? HTTPURLResponse else { throw GitHubError.http(0, L("Brak odpowiedzi HTTP", "No HTTP response")) }
        if http.statusCode == 401 { throw GitHubError.unauthorized }
        guard (200..<300).contains(http.statusCode) else {
            throw GitHubError.http(http.statusCode, String(decoding: data, as: UTF8.self))
        }
        return (data, http)
    }

    // MARK: - Parsing

    public struct SearchPage {
        public let pullRequests: [PullRequest]
        public let hasNextPage: Bool
        public let endCursor: String?
        public let viewerLogin: String?
        public let issueCount: Int
        /// Commit author logins per PR id; only filled by the commit scan query.
        public let commitAuthors: [String: [String]]
    }

    public static func parseSearchResponse(_ data: Data) throws -> SearchPage {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let decoded = try decoder.decode(GraphQLResponse<SearchData>.self, from: data)
        guard let search = decoded.data?.search else {
            throw GitHubError.graphQL(decoded.errors?.map(\.message).joined(separator: "; ") ?? L("Pusta odpowiedź", "Empty response"))
        }
        let nodes = search.nodes.compactMap(\.value)
        var commitAuthors: [String: [String]] = [:]
        for node in nodes {
            if let authors = node.commitAuthors { commitAuthors[node.id] = authors.nodes.compactMap { $0.commit.author?.user?.login } }
        }
        return SearchPage(
            pullRequests: nodes.map(\.pullRequest), hasNextPage: search.pageInfo.hasNextPage,
            endCursor: search.pageInfo.endCursor, viewerLogin: decoded.data?.viewer?.login,
            issueCount: search.issueCount ?? 0, commitAuthors: commitAuthors
        )
    }

    /// Parses e.g. `partial-results; organizations=21955855,20582480`.
    public static func ssoBlockedOrgIDs(from header: String?) -> Set<String> {
        guard let header, header.contains("partial-results") else { return [] }
        for part in header.split(separator: ";") {
            let trimmed = part.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("organizations=") {
                let ids = trimmed.dropFirst("organizations=".count).split(separator: ",")
                return Set(ids.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty })
            }
        }
        return []
    }

    static let searchQuery = query(extraFields: "")

    /// Same fields plus authors of the last 50 commits; a commit's single author keeps the query cheap.
    static let commitScanQuery = query(extraFields: "commitAuthors: commits(last: 50) { nodes { commit { author { user { login } } } } }")

    private static func query(extraFields: String) -> String {
        """
    query($q: String!, $after: String) {
      viewer { login }
      search(query: $q, type: ISSUE, first: 50, after: $after) {
        issueCount
        pageInfo { hasNextPage endCursor }
        nodes {
          ... on PullRequest {
            \(extraFields)
            id number title url createdAt updatedAt reviewDecision isDraft
            author { login avatarUrl }
            repository { nameWithOwner }
            additions deletions
            labels(first: 100) { nodes { name } }
            commits(last: 1) { nodes { commit { oid author { user { login } } statusCheckRollup { state } } } }
            comments(last: 10) { nodes { id createdAt author { __typename login } } }
            reviews(last: 100) { nodes { id submittedAt state commit { oid } author { __typename login } } }
          }
        }
      }
    }
    """
    }
}

// MARK: - GraphQL DTOs

private struct TeamMember: Decodable { let login: String }

private struct GraphQLResponse<T: Decodable>: Decodable {
    let data: T?
    let errors: [GraphQLErrorDTO]?
}

private struct GraphQLErrorDTO: Decodable {
    let message: String
    let type: String?
}

private struct UserData: Decodable {
    struct User: Decodable { let login: String }
    let user: User?
}

private struct SearchData: Decodable {
    struct PageInfo: Decodable {
        let hasNextPage: Bool
        let endCursor: String?
    }

    struct Search: Decodable {
        let issueCount: Int?
        let pageInfo: PageInfo
        let nodes: [Lossy<PRNode>]
    }

    struct Viewer: Decodable { let login: String }

    let viewer: Viewer?
    let search: Search
}

/// Search results may contain non-PR nodes (decoded as `{}`); those are skipped instead of failing the page.
private struct Lossy<T: Decodable>: Decodable {
    let value: T?
    init(from decoder: Decoder) throws { value = try? T(from: decoder) }
}

private struct PRNode: Decodable {
    struct Author: Decodable {
        let login: String
        let avatarUrl: URL?
    }

    struct Repository: Decodable { let nameWithOwner: String }

    struct Commits: Decodable {
        struct Node: Decodable {
            struct Commit: Decodable {
                struct Rollup: Decodable { let state: CheckState? }
                struct GitActor: Decodable {
                    struct User: Decodable { let login: String }
                    let user: User?
                }
                let oid: String?
                let author: GitActor?
                let statusCheckRollup: Rollup?
            }
            let commit: Commit
        }
        let nodes: [Node]
    }

    struct Actor: Decodable {
        let __typename: String?
        let login: String
    }

    struct Comments: Decodable {
        struct Node: Decodable {
            let id: String?
            let createdAt: Date
            let author: Actor?
        }
        let nodes: [Lossy<Node>]
    }

    struct Reviews: Decodable {
        struct Node: Decodable {
            struct ReviewedCommit: Decodable { let oid: String? }
            let id: String?
            let submittedAt: Date?
            let state: String?
            let author: Actor?
            let commit: ReviewedCommit?
        }
        let nodes: [Lossy<Node>]
    }

    struct Labels: Decodable {
        struct Node: Decodable { let name: String }
        let nodes: [Node]
    }

    let id: String
    let number: Int
    let title: String
    let url: URL
    let createdAt: Date
    let updatedAt: Date
    let reviewDecision: ReviewDecision?
    let isDraft: Bool?
    let author: Author?
    let repository: Repository
    let commits: Commits?
    let commitAuthors: Commits?
    let comments: Comments?
    let reviews: Reviews?
    let additions: Int?
    let deletions: Int?
    let labels: Labels?

    private static func activity(_ id: String?, _ author: Actor?, _ date: Date?, _ kind: ActivityKind) -> Activity? {
        guard let author, author.__typename != "Bot", let date else { return nil }
        return Activity(login: author.login, date: date, kind: kind, id: id)
    }

    var pullRequest: PullRequest {
        let commentActivity = comments?.nodes.compactMap {
            $0.value.flatMap { Self.activity($0.id, $0.author, $0.createdAt, .comment) }
        } ?? []
        let reviewActivity = reviews?.nodes.compactMap { node in
            node.value.flatMap { Self.activity($0.id, $0.author, $0.submittedAt, ActivityKind(reviewState: $0.state)) }
        } ?? []
        return PullRequest(
            id: id, number: number, title: title, url: url,
            repository: repository.nameWithOwner,
            author: author?.login ?? "ghost",
            authorAvatarURL: author?.avatarUrl,
            createdAt: createdAt, updatedAt: updatedAt,
            reviewDecision: reviewDecision,
            checks: commits?.nodes.last?.commit.statusCheckRollup?.state,
            headCommitOID: commits?.nodes.last?.commit.oid,
            activity: commentActivity + reviewActivity,
            additions: additions ?? 0,
            deletions: deletions ?? 0,
            labels: labels?.nodes.map(\.name) ?? [],
            reviews: reviews?.nodes.compactMap { node in
                guard let review = node.value, let login = review.author?.login else { return nil }
                return PullRequestReview(login: login, commitOID: review.commit?.oid, submittedAt: review.submittedAt)
            } ?? [],
            headCommitAuthor: commits?.nodes.last?.commit.author?.user?.login,
            isDraft: isDraft ?? false
        )
    }
}
