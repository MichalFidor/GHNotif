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
}
