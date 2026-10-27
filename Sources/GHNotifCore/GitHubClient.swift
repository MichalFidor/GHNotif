import Foundation

public enum GitHubError: LocalizedError, Equatable {
    case unauthorized
    case http(Int, String)
    case graphQL(String)

    public var errorDescription: String? {
        switch self {
        case .unauthorized: return "Token GitHub jest nieprawidłowy lub wygasł."
        case let .http(code, body): return "GitHub API zwróciło HTTP \(code): \(body.prefix(200))"
        case let .graphQL(message): return "GitHub API: \(message)"
        }
    }
}

public struct GitHubClient: Sendable {
    public let token: String
    private let endpoint = URL(string: "https://api.github.com/graphql")!
    private let session: URLSession

    public init(token: String, session: URLSession = .shared) {
        self.token = token
        self.session = session
    }

    public func searchPullRequests(authors: [String], extraQualifiers: String) async throws -> SearchOutcome {
        let queries = SearchQueryBuilder.queries(authors: authors, extraQualifiers: extraQualifiers)
        let outcomes = try await withThrowingTaskGroup(of: SearchOutcome.self) { group in
            for query in queries {
                group.addTask { try await search(query: query) }
            }
            return try await group.reduce(into: []) { $0.append($1) }
        }
        var byID: [String: PullRequest] = [:]
        var blocked = Set<String>()
        var viewer: String?
        for outcome in outcomes {
            outcome.pullRequests.forEach { byID[$0.id] = $0 }
            blocked.formUnion(outcome.ssoBlockedOrgIDs)
            viewer = viewer ?? outcome.viewerLogin
        }
        let sorted = byID.values.sorted { $0.updatedAt > $1.updatedAt }
        return SearchOutcome(pullRequests: sorted, ssoBlockedOrgIDs: blocked, viewerLogin: viewer)
    }

    public func search(query: String, maxPages: Int = 10) async throws -> SearchOutcome {
        var cursor: String?
        var prs: [PullRequest] = []
        var blocked = Set<String>()
        var viewer: String?
        for _ in 0..<maxPages {
            var variables: [String: String] = ["q": query]
            if let cursor { variables["after"] = cursor }
            let (data, response) = try await post(query: Self.searchQuery, variables: variables)
            blocked.formUnion(Self.ssoBlockedOrgIDs(from: response.value(forHTTPHeaderField: "X-GitHub-SSO")))
            let page = try Self.parseSearchResponse(data)
            prs.append(contentsOf: page.pullRequests)
            viewer = viewer ?? page.viewerLogin
            guard page.hasNextPage, let next = page.endCursor else { break }
            cursor = next
        }
        return SearchOutcome(pullRequests: prs, ssoBlockedOrgIDs: blocked, viewerLogin: viewer)
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

    private func post(query: String, variables: [String: String]) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue("bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("GHNotif", forHTTPHeaderField: "User-Agent")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["query": query, "variables": variables])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw GitHubError.http(0, "Brak odpowiedzi HTTP") }
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
    }

    public static func parseSearchResponse(_ data: Data) throws -> SearchPage {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let decoded = try decoder.decode(GraphQLResponse<SearchData>.self, from: data)
        guard let search = decoded.data?.search else {
            throw GitHubError.graphQL(decoded.errors?.map(\.message).joined(separator: "; ") ?? "Pusta odpowiedź")
        }
        let prs = search.nodes.compactMap { $0.value?.pullRequest }
        return SearchPage(
            pullRequests: prs, hasNextPage: search.pageInfo.hasNextPage, endCursor: search.pageInfo.endCursor,
            viewerLogin: decoded.data?.viewer?.login
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

    static let searchQuery = """
    query($q: String!, $after: String) {
      viewer { login }
      search(query: $q, type: ISSUE, first: 50, after: $after) {
        pageInfo { hasNextPage endCursor }
        nodes {
          ... on PullRequest {
            id number title url createdAt updatedAt reviewDecision
            author { login avatarUrl }
            repository { nameWithOwner }
            commits(last: 1) { nodes { commit { oid statusCheckRollup { state } } } }
            comments(last: 10) { nodes { createdAt author { __typename login } } }
            reviews(last: 10) { nodes { submittedAt author { __typename login } } }
          }
        }
      }
    }
    """
}

// MARK: - GraphQL DTOs

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
                let oid: String?
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
            let createdAt: Date
            let author: Actor?
        }
        let nodes: [Lossy<Node>]
    }

    struct Reviews: Decodable {
        struct Node: Decodable {
            let submittedAt: Date?
            let author: Actor?
        }
        let nodes: [Lossy<Node>]
    }

    let id: String
    let number: Int
    let title: String
    let url: URL
    let createdAt: Date
    let updatedAt: Date
    let reviewDecision: ReviewDecision?
    let author: Author?
    let repository: Repository
    let commits: Commits?
    let comments: Comments?
    let reviews: Reviews?

    private static func activity(_ author: Actor?, _ date: Date?) -> Activity? {
        guard let author, author.__typename != "Bot", let date else { return nil }
        return Activity(login: author.login, date: date)
    }

    var pullRequest: PullRequest {
        let commentActivity = comments?.nodes.compactMap { $0.value.flatMap { Self.activity($0.author, $0.createdAt) } } ?? []
        let reviewActivity = reviews?.nodes.compactMap { $0.value.flatMap { Self.activity($0.author, $0.submittedAt) } } ?? []
        return PullRequest(
            id: id, number: number, title: title, url: url,
            repository: repository.nameWithOwner,
            author: author?.login ?? "ghost",
            authorAvatarURL: author?.avatarUrl,
            createdAt: createdAt, updatedAt: updatedAt,
            reviewDecision: reviewDecision,
            checks: commits?.nodes.last?.commit.statusCheckRollup?.state,
            headCommitOID: commits?.nodes.last?.commit.oid,
            activity: commentActivity + reviewActivity
        )
    }
}
