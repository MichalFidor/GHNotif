import Foundation

public enum SearchQueryBuilder {
    public static let baseQualifiers = "is:pr is:open draft:false"

    /// GitHub search rejects queries with more than 5 boolean operators, so authors are not joined with `OR`.
    /// Repeated `author:` qualifiers are OR-ed implicitly; batching keeps each query well within length limits.
    public static func queries(authors: [String], extraQualifiers: String = "", batchSize: Int = 10) -> [String] {
        let cleaned = normalize(authors)
        guard !cleaned.isEmpty else { return [] }
        let size = max(1, batchSize)
        let extra = extraQualifiers.trimmingCharacters(in: .whitespacesAndNewlines)
        return stride(from: 0, to: cleaned.count, by: size).map { start in
            let batch = cleaned[start..<min(start + size, cleaned.count)]
            var parts = [baseQualifiers]
            parts.append(contentsOf: batch.map { "author:\($0)" })
            if !extra.isEmpty { parts.append(extra) }
            return parts.joined(separator: " ")
        }
    }

    /// All searches for a config, tagged with why a matching PR is shown.
    public static func searches(for config: AppConfig, batchSize: Int = 10) -> [(query: String, reason: MatchReason)] {
        var result = queries(authors: config.users, extraQualifiers: config.extraQualifiers, batchSize: batchSize)
            .map { (query: $0, reason: MatchReason.watchedAuthor) }
        let extra = config.extraQualifiers.trimmingCharacters(in: .whitespacesAndNewlines)
        func viewerQuery(_ qualifiers: String...) -> String {
            ([baseQualifiers] + qualifiers + (extra.isEmpty ? [] : [extra])).joined(separator: " ")
        }
        // Mentions match any author, including the user; GitHub can't request a review from a PR's own author.
        if config.includeMentions { result.append((viewerQuery("mentions:@me"), .mention)) }
        if config.includeReviewRequests {
            result.append((viewerQuery("user-review-requested:@me", "-author:@me"), .reviewRequested))
        }
        return result
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

    public static func normalizeLogins(_ raw: [String]) -> [String] {
        var seen = Set<String>()
        return raw.map(normalizeLogin).filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
    }
}
