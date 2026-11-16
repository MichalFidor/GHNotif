import Foundation

public enum SearchQueryBuilder {
    public static let baseQualifiers = "is:pr is:open draft:false"
    public static let baseQualifiersWithDrafts = "is:pr is:open"

    public static func base(includeDrafts: Bool) -> String {
        includeDrafts ? baseQualifiersWithDrafts : baseQualifiers
    }

    /// GitHub search rejects queries with more than 5 boolean operators, so authors are not joined with `OR`.
    /// Repeated `author:` qualifiers are OR-ed implicitly; batching keeps each query well within length limits.
    public static func queries(
        authors: [String], extraQualifiers: String = "", batchSize: Int = 10, includeDrafts: Bool = false
    ) -> [String] {
        let cleaned = normalize(authors)
        guard !cleaned.isEmpty else { return [] }
        let size = max(1, batchSize)
        let extra = extraQualifiers.trimmingCharacters(in: .whitespacesAndNewlines)
        return stride(from: 0, to: cleaned.count, by: size).map { start in
            let batch = cleaned[start..<min(start + size, cleaned.count)]
            var parts = [base(includeDrafts: includeDrafts)]
            parts.append(contentsOf: batch.map { "author:\($0)" })
            if !extra.isEmpty { parts.append(extra) }
            return parts.joined(separator: " ")
        }
    }

    /// All searches for a config, tagged with why a matching PR is shown.
    public static func searches(for config: AppConfig, batchSize: Int = 10) throws -> [(query: String, reason: MatchReason)] {
        let extra = config.extraQualifiers.trimmingCharacters(in: .whitespacesAndNewlines)
        var parser = try ScopeParser(extra)
        let branches = try parser.parse().branches().map { $0.filter { !$0.isEmpty } }
        func searchQueries(_ qualifiers: [String]) -> [String] {
            branches.map { branch in
                ([base(includeDrafts: config.includeDrafts)] + qualifiers + branch).joined(separator: " ")
            }
        }
        var result: [(query: String, reason: MatchReason)] = []
        let cleaned = normalize(config.users)
        let size = max(1, batchSize)
        for start in stride(from: 0, to: cleaned.count, by: size) {
            let batch = cleaned[start..<min(start + size, cleaned.count)]
            result += searchQueries(batch.map { "author:\($0)" }).map { (query: $0, reason: .watchedAuthor) }
        }
        // Mentions match any author, including the user; GitHub can't request a review from a PR's own author.
        if config.includeMentions {
            result += searchQueries(["mentions:@me"]).map { (query: $0, reason: .mention) }
        }
        if config.includeReviewRequests {
            result += searchQueries(["user-review-requested:@me", "-author:@me"])
                .map { (query: $0, reason: .reviewRequested) }
        }
        // `review-requested:@me` also matches personal requests; the personal reason wins when both apply.
        if config.includeReviewRequests && config.includeTeamReviewRequests {
            result += searchQueries(["review-requested:@me", "-author:@me"])
                .map { (query: $0, reason: .teamReviewRequested) }
        }
        return result
    }

    public struct RepositorySearch: Sendable {
        public var repositories: [String]
        let qualifiers: String
        let includeDrafts: Bool

        public var query: String {
            ([base(includeDrafts: includeDrafts)] + repositories.map { "repo:\($0)" }
                + (qualifiers.isEmpty ? [] : [qualifiers])).joined(separator: " ")
        }
    }

    /// Expand Boolean scopes into separate searches so repo clauses can be intersected without leaving dangling operators.
    public static func repositorySearches(
        repositories: [String], config: AppConfig, batchSize: Int = 5
    ) throws -> [RepositorySearch] {
        var parser = try ScopeParser(config.extraQualifiers)
        let branches = try parser.parse().branches()
        var scopes: [RepositorySearch] = []
        for branch in branches {
            let explicitRepositories = branch.compactMap { qualifier -> String? in
                guard qualifier.lowercased().hasPrefix("repo:") else { return nil }
                return String(qualifier.dropFirst("repo:".count)).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            }
            let eligible = repositories.filter { repository in
                explicitRepositories.allSatisfy {
                    $0.caseInsensitiveCompare(repository) == .orderedSame
                }
            }
            guard !eligible.isEmpty else { continue }
            let qualifiers = branch.filter { !$0.lowercased().hasPrefix("repo:") }.joined(separator: " ")
            if let index = scopes.firstIndex(where: { $0.qualifiers == qualifiers }) {
                for repository in eligible where !scopes[index].repositories.contains(repository) {
                    scopes[index].repositories.append(repository)
                }
            } else {
                scopes.append(RepositorySearch(repositories: eligible, qualifiers: qualifiers, includeDrafts: config.includeDrafts))
            }
        }
        let size = max(1, batchSize)
        return scopes.flatMap { scope in
            stride(from: 0, to: scope.repositories.count, by: size).map { start in
                var batch = scope
                batch.repositories = Array(scope.repositories[start..<min(start + size, scope.repositories.count)])
                return batch
            }
        }
    }

    public static func repositoryQueries(repositories: [String], config: AppConfig, batchSize: Int = 5) throws -> [String] {
        try repositorySearches(repositories: repositories, config: config, batchSize: batchSize).map(\.query)
    }

    private static var invalidScope: GitHubError {
        .graphQL(L("Nieprawidłowy lub zbyt złożony zakres wyszukiwania. Sprawdź cudzysłowy i operatory logiczne.",
                   "Invalid or overly complex search scope. Check quotes and Boolean operators."))
    }

    private indirect enum ScopeExpression {
        case term(String), and(ScopeExpression, ScopeExpression), or(ScopeExpression, ScopeExpression), not(ScopeExpression)

        func branches(negated: Bool = false) throws -> [[String]] {
            switch self {
            case let .term(term):
                return [[negated ? (term.hasPrefix("-") ? String(term.dropFirst()) : "-\(term)") : term]]
            case let .not(expression):
                return try expression.branches(negated: !negated)
            case let .and(left, right), let .or(left, right):
                let lhs = try left.branches(negated: negated)
                let rhs = try right.branches(negated: negated)
                let isAnd: Bool
                if case .and = self { isAnd = !negated } else { isAnd = negated }
                guard (isAnd ? lhs.count * rhs.count : lhs.count + rhs.count) <= 32 else { throw invalidScope }
                return isAnd ? lhs.flatMap { a in rhs.map { a + $0 } } : lhs + rhs
            }
        }
    }

    private struct ScopeParser {
        var tokens: [String] = []
        var index = 0

        init(_ text: String) throws {
            var token = ""
            var quoted = false
            var escaped = false
            for character in text {
                if escaped {
                    token.append(character)
                    escaped = false
                } else if character == "\\" && quoted {
                    token.append(character)
                    escaped = true
                } else if character == "\"" {
                    token.append(character)
                    quoted.toggle()
                } else if !quoted && (character.isWhitespace || character == "(" || character == ")") {
                    if !token.isEmpty { tokens.append(token); token = "" }
                    if !character.isWhitespace { tokens.append(String(character)) }
                } else {
                    token.append(character)
                }
            }
            guard !quoted else { throw invalidScope }
            if !token.isEmpty { tokens.append(token) }
        }

        mutating func parse() throws -> ScopeExpression {
            guard !tokens.isEmpty else { return .term("") }
            let expression = try disjunction()
            guard index == tokens.count else { throw invalidScope }
            return expression
        }

        mutating func disjunction() throws -> ScopeExpression {
            var expression = try conjunction()
            while consume("OR") { expression = .or(expression, try conjunction()) }
            return expression
        }

        mutating func conjunction() throws -> ScopeExpression {
            var expression = try implicitConjunction()
            while consume("AND") { expression = .and(expression, try implicitConjunction()) }
            return expression
        }

        mutating func implicitConjunction() throws -> ScopeExpression {
            var terms = [try primary()]
            while index < tokens.count && !["AND", "OR", ")"].contains(tokens[index]) {
                terms.append(try primary())
            }
            var repositories: ScopeExpression?
            var filters: [ScopeExpression] = []
            for term in terms {
                if case let .term(value) = term, value.lowercased().hasPrefix("repo:") {
                    repositories = repositories.map { .or($0, term) } ?? term
                } else {
                    filters.append(term)
                }
            }
            if let repositories { filters.insert(repositories, at: 0) }
            return filters.dropFirst().reduce(filters[0]) { .and($0, $1) }
        }

        mutating func primary() throws -> ScopeExpression {
            if consume("NOT") || consume("-") { return .not(try primary()) }
            if consume("(") {
                let expression = try disjunction()
                guard consume(")") else { throw invalidScope }
                return expression
            }
            guard index < tokens.count, !["AND", "OR", ")"].contains(tokens[index]) else { throw invalidScope }
            defer { index += 1 }
            return .term(tokens[index])
        }

        mutating func consume(_ token: String) -> Bool {
            guard index < tokens.count, tokens[index] == token else { return false }
            index += 1
            return true
        }
    }

    /// Trims, strips a leading `@`, and removes case-insensitive duplicates while preserving order.
    public static func normalize(_ logins: [String]) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for raw in logins {
            let login = normalizeLogin(raw)
            guard !login.isEmpty, seen.insert(login.lowercased()).inserted else { continue }
            result.append(login)
        }
        return result
    }

    public static func normalizeLogin(_ raw: String) -> String {
        var login = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if login.hasPrefix("@") { login.removeFirst() }
        return login
    }

    public static func isValidLogin(_ login: String) -> Bool {
        let base = login.hasSuffix("[bot]") ? String(login.dropLast(5)) : login
        return !base.isEmpty && base.utf8.count <= 39
            && base.range(of: #"\A[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*(?:_[A-Za-z0-9]+)?\z"#,
                          options: .regularExpression) != nil
    }

    public static func validatedLogins(_ raw: [String]) throws -> [String] {
        let logins = raw.map(normalizeLogin)
        guard !logins.isEmpty, logins.allSatisfy(isValidLogin) else {
            throw GitHubError.graphQL(L("Lista zawiera nieprawidłowy login GitHub lub jest pusta.",
                                       "The list contains an invalid GitHub login or is empty."))
        }
        return normalizeLogins(logins)
    }

    public static func normalizeLogins(_ raw: [String]) -> [String] {
        var seen = Set<String>()
        return raw.map(normalizeLogin).filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
    }
}
