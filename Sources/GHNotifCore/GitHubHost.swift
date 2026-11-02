import Foundation

/// Endpoints for github.com, GHE.com (data residency) and GitHub Enterprise Server.
public struct GitHubHost: Equatable, Sendable {
    public static let dotCom = "github.com"

    public let name: String

    public init(_ raw: String) {
        name = Self.normalize(raw)
    }

    /// Strips the scheme, path and trailing slashes; empty input means github.com.
    public static func normalize(_ raw: String) -> String {
        var value = raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        for scheme in ["https://", "http://"] where value.hasPrefix(scheme) { value.removeFirst(scheme.count) }
        if let slash = value.firstIndex(of: "/") { value = String(value[..<slash]) }
        if value.hasPrefix("api.") && (value == "api.github.com" || value.hasSuffix(".ghe.com")) {
            value.removeFirst("api.".count)
        }
        return value.isEmpty ? dotCom : value
    }

    public var isDotCom: Bool { name == Self.dotCom }

    /// GitHub Enterprise Server serves the API under `/api`; github.com and GHE.com use an `api.` subdomain.
    private var usesAPISubdomain: Bool { isDotCom || name.hasSuffix(".ghe.com") }

    public var graphQLURL: URL {
        URL(string: usesAPISubdomain ? "https://api.\(name)/graphql" : "https://\(name)/api/graphql")!
    }

    public var restBaseURL: URL {
        URL(string: usesAPISubdomain ? "https://api.\(name)" : "https://\(name)/api/v3")!
    }

    public func restURL(_ path: String) -> URL {
        URL(string: restBaseURL.absoluteString + path)!
    }

    public func webURL(_ path: String = "") -> URL {
        URL(string: "https://\(name)\(path)")!
    }

    /// Page creating a classic PAT with the scopes GHNotif needs preselected.
    public var newTokenURL: URL { webURL("/settings/tokens/new?scopes=repo,read:org&description=GHNotif") }
    public var tokensURL: URL { webURL("/settings/tokens") }

    /// Where the user authorizes the token for SAML SSO organizations.
    public func ssoSettingsURL(usingGitHubCLI: Bool) -> URL {
        // The GitHub CLI OAuth app ID is only known on github.com.
        if usingGitHubCLI {
            return isDotCom ? webURL("/settings/connections/applications/178c6fc778ccc68e1d6a")
                : webURL("/settings/applications")
        }
        return tokensURL
    }

    /// True for links that belong to this host, e.g. PR URLs opened from the list.
    public func owns(_ url: URL) -> Bool {
        url.scheme == "https" && url.host?.lowercased() == name
    }
}
