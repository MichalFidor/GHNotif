import Foundation

public struct ReleaseVersion: Comparable, Equatable, Sendable {
    private let parts: [Int]

    public init?(_ value: String) {
        let text = value.hasPrefix("v") ? String(value.dropFirst()) : value
        let fields = text.split(separator: ".", omittingEmptySubsequences: false)
        guard fields.count == 3 else { return nil }
        let numbers = fields.compactMap { field -> Int? in
            guard !field.isEmpty, field.allSatisfy({ $0.isASCII && $0.isNumber }) else { return nil }
            return Int(field)
        }
        guard numbers.count == 3 else { return nil }
        parts = numbers
    }

    public static func < (lhs: Self, rhs: Self) -> Bool {
        lhs.parts.lexicographicallyPrecedes(rhs.parts)
    }
}

public struct GitHubRelease: Decodable, Sendable {
    public let tagName: String
    public let htmlURL: URL
    public let draft: Bool
    public let prerelease: Bool

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name", htmlURL = "html_url", draft, prerelease
    }

    public func isNewer(than installed: String) throws -> Bool {
        guard let latest = ReleaseVersion(tagName), let current = ReleaseVersion(installed),
              htmlURL.scheme == "https", htmlURL.host == "github.com",
              htmlURL.path.hasPrefix("/MichalFidor/GHNotif/releases/") else {
            throw GitHubError.graphQL(L("Nieprawidłowa wersja lub adres wydania GHNotif.", "Invalid GHNotif release version or URL."))
        }
        return !draft && !prerelease && latest > current
    }

    public static func latest(session: URLSession = .shared) async throws -> GitHubRelease? {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/MichalFidor/GHNotif/releases/latest")!)
        request.setValue("GHNotif", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw GitHubError.http(0, L("Brak odpowiedzi HTTP", "No HTTP response")) }
        if http.statusCode == 404 { return nil }
        guard http.statusCode == 200 else { throw GitHubError.http(http.statusCode, L("Nie udało się sprawdzić aktualizacji.", "Could not check for updates.")) }
        return try JSONDecoder().decode(Self.self, from: data)
    }
}
