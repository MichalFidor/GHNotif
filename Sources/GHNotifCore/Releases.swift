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
    public let assets: [ReleaseAsset]

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name", htmlURL = "html_url", draft, prerelease, assets
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        tagName = try container.decode(String.self, forKey: .tagName)
        htmlURL = try container.decode(URL.self, forKey: .htmlURL)
        draft = try container.decode(Bool.self, forKey: .draft)
        prerelease = try container.decode(Bool.self, forKey: .prerelease)
        assets = try container.decodeIfPresent([ReleaseAsset].self, forKey: .assets) ?? []
    }

    public func isNewer(than installed: String) throws -> Bool {
        guard let latest = ReleaseVersion(tagName), let current = ReleaseVersion(installed),
              htmlURL.scheme == "https", htmlURL.host == "github.com",
              htmlURL.path.hasPrefix("/MichalFidor/GHNotif/releases/") else {
            throw GitHubError.graphQL(L("Nieprawidłowa wersja lub adres wydania GHNotif.", "Invalid GHNotif release version or URL."))
        }
        return !draft && !prerelease && latest > current
    }

    public func downloadURL(named name: String) throws -> URL {
        guard let asset = assets.first(where: { $0.name == name }),
              asset.browserDownloadURL.scheme == "https",
              asset.browserDownloadURL.host == "github.com",
              asset.browserDownloadURL.path.contains("/MichalFidor/GHNotif/releases/download/\(tagName)/"),
              asset.browserDownloadURL.lastPathComponent == name else {
            throw GitHubError.graphQL(L("Nie znaleziono prawidłowego pliku aktualizacji GHNotif.", "A valid GHNotif update asset was not found."))
        }
        return asset.browserDownloadURL
    }

    public func checksum(for fileName: String, in manifest: String) throws -> String {
        guard let line = manifest.split(whereSeparator: \.isNewline).first(where: {
            let fields = $0.split(whereSeparator: \.isWhitespace)
            return fields.count >= 2 && String(fields[1]).trimmingCharacters(in: CharacterSet(charactersIn: "*")) == fileName
        }) else {
            throw GitHubError.graphQL(L("Brak sumy kontrolnej dla pliku aktualizacji.", "No checksum found for the update asset."))
        }
        let hash = String(line.split(whereSeparator: \.isWhitespace)[0])
        guard hash.count == 64, hash.allSatisfy({ $0.isHexDigit }) else {
            throw GitHubError.graphQL(L("Nieprawidłowa suma kontrolna aktualizacji.", "The update checksum is invalid."))
        }
        return hash.lowercased()
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

    public struct ReleaseAsset: Decodable, Sendable {
        public let name: String
        public let browserDownloadURL: URL

        enum CodingKeys: String, CodingKey {
            case name
            case browserDownloadURL = "browser_download_url"
        }
    }
}
