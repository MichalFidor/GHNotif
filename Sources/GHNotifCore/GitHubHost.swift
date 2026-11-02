import Foundation

/// Endpoints for github.com, GHE.com (data residency) and GitHub Enterprise Server.
public struct GitHubHost: Equatable, Sendable {
    public static let dotCom = "github.com"

    public let name: String

    public init(_ raw: String) {
        name = Self.normalize(raw)
    }

    /// Strips the scheme, path and trailing slashes; empty input means github.com.
    /// Anything that isn't a plain DNS hostname (userinfo, port, query, odd characters) falls back to github.com,
    /// so the token can't be redirected to an unexpected server.
    public static func normalize(_ raw: String) -> String {
        let value = stripped(raw)
        return isHostname(value) ? value : dotCom
    }

    /// True when the input names a host GHNotif will talk to (empty means github.com).
    public static func isValid(_ raw: String) -> Bool {
        let value = stripped(raw)
        return value.isEmpty || isHostname(value)
    }

    private static func stripped(_ raw: String) -> String {
        var value = raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        for scheme in ["https://", "http://"] where value.hasPrefix(scheme) { value.removeFirst(scheme.count) }
        if let slash = value.firstIndex(of: "/") { value = String(value[..<slash]) }
        if value.hasPrefix("api.") && (value == "api.github.com" || value.hasSuffix(".ghe.com")) {
            value.removeFirst("api.".count)
        }
        return value
    }

    private static func isHostname(_ value: String) -> Bool {
        let labels = value.split(separator: ".", omittingEmptySubsequences: false)
        let allowed = Set("abcdefghijklmnopqrstuvwxyz0123456789-")
        return value.count <= 253 && labels.count >= 2 && labels.allSatisfy { label in
            (1...63).contains(label.count) && label.allSatisfy(allowed.contains)
                && label.first != "-" && label.last != "-"
        }
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

    /// Page creating a read-only fine-grained PAT with the permissions GHNotif needs preselected.
    public var newTokenURL: URL {
        webURL("/settings/personal-access-tokens/new?name=GHNotif&description=Read-only+access+for+GHNotif"
            + "&pull_requests=read&statuses=read&members=read&expires_in=90")
    }

    /// Page creating a classic PAT with only the `repo` scope (classic tokens have no read-only private repo scope).
    public var newClassicTokenURL: URL { webURL("/settings/tokens/new?scopes=repo&description=GHNotif") }
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
