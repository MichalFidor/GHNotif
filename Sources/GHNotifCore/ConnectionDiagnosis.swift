import Foundation

/// Result of "Check connection": who the token belongs to, its scopes and which organizations it can read.
public struct ConnectionDiagnosis: Equatable, Sendable {
    public enum OrgStatus: Equatable, Sendable {
        case ok
        /// The token isn't authorized for the organization's SAML SSO; `url` starts the authorization.
        case ssoRequired(URL?)
        /// Organization policy rejects the token, e.g. classic PATs or OAuth app access restrictions.
        case blocked(String)
        case error(String)
    }

    public struct Organization: Equatable, Sendable {
        public let login: String
        public let status: OrgStatus

        public init(login: String, status: OrgStatus) {
            self.login = login
            self.status = status
        }
    }

    public let login: String
    /// Scopes of a classic PAT or OAuth token; `nil` for fine-grained tokens and GitHub Apps (no scope header).
    public let scopes: [String]?
    public let organizations: [Organization]

    public init(login: String, scopes: [String]?, organizations: [Organization]) {
        self.login = login
        self.scopes = scopes
        self.organizations = organizations
    }

    /// Required scopes missing from a classic token; broader scopes that imply them count as present.
    public var missingScopes: [String] {
        guard let scopes else { return [] }
        let granted = Set(scopes.map { $0.lowercased() })
        var missing: [String] = []
        if !granted.contains("repo") { missing.append("repo") }
        if granted.isDisjoint(with: ["read:org", "write:org", "admin:org"]) { missing.append("read:org") }
        return missing
    }

    public var hasProblems: Bool {
        !missingScopes.isEmpty || organizations.contains { $0.status != .ok }
    }

    /// Plain-text report shown in Settings, one finding per line.
    public var lines: [String] {
        var result = [L("✓ Zalogowano jako \(login)", "✓ Signed in as \(login)")]
        if let scopes {
            let list = scopes.isEmpty ? L("(brak)", "(none)") : scopes.joined(separator: ", ")
            result.append(L("Uprawnienia tokena: \(list)", "Token scopes: \(list)"))
            for scope in missingScopes {
                switch scope {
                case "repo":
                    result.append(L("✗ Brak uprawnienia repo - nie zobaczysz PR-ów z prywatnych repozytoriów.",
                                    "✗ Missing repo scope - PRs from private repositories won't show."))
                default:
                    result.append(L("✗ Brak uprawnienia read:org - lista organizacji i import zespołów mogą być niepełne.",
                                    "✗ Missing read:org scope - the organization list and team import may be incomplete."))
                }
            }
        } else {
            result.append(L("Token fine-grained lub GitHub App - upewnij się, że ma dostęp do repozytoriów organizacji (Pull requests: read).",
                            "Fine-grained token or GitHub App - make sure it can read the organization's repositories (Pull requests: read)."))
        }
        if organizations.isEmpty {
            result.append(L("Nie widać żadnej organizacji - jeśli należysz do jakiejś, sprawdź read:org i autoryzację SSO.",
                            "No organizations visible - if you belong to one, check read:org and SSO authorization."))
        }
        for org in organizations {
            switch org.status {
            case .ok:
                result.append(L("✓ \(org.login) - dostęp OK", "✓ \(org.login) - access OK"))
            case .ssoRequired:
                result.append(L("✗ \(org.login) - wymaga autoryzacji SSO (Configure SSO → Authorize)",
                                "✗ \(org.login) - needs SSO authorization (Configure SSO → Authorize)"))
            case let .blocked(message):
                result.append(L("✗ \(org.login) - organizacja blokuje ten token: \(message)",
                                "✗ \(org.login) - the organization blocks this token: \(message)"))
            case let .error(message):
                result.append(L("? \(org.login) - nie udało się sprawdzić: \(message)",
                                "? \(org.login) - couldn't check: \(message)"))
            }
        }
        return result
    }

    /// First SSO authorization link, if any organization needs one.
    public var ssoAuthorizationURL: URL? {
        for org in organizations { if case let .ssoRequired(url?) = org.status { return url } }
        return nil
    }

    /// Parses the `X-OAuth-Scopes` header; `nil` when the header is absent.
    public static func scopes(from header: String?) -> [String]? {
        guard let header else { return nil }
        return header.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    /// Parses e.g. `required; url=https://github.com/orgs/acme/sso?authorization_request=...`.
    public static func isSSORequired(_ header: String?) -> Bool {
        header?.lowercased().hasPrefix("required") ?? false
    }

    public static func ssoURL(from header: String?) -> URL? {
        guard let header else { return nil }
        for part in header.split(separator: ";") {
            let trimmed = part.trimmingCharacters(in: .whitespaces)
            if trimmed.lowercased().hasPrefix("url=") { return URL(string: String(trimmed.dropFirst(4))) }
        }
        return nil
    }

    /// Classifies a probe of `GET /orgs/{org}/repos`.
    public static func orgStatus(statusCode: Int, ssoHeader: String?, body: Data) -> OrgStatus {
        if (200..<300).contains(statusCode) { return .ok }
        let message = (try? JSONDecoder().decode(ErrorBody.self, from: body))?.message
            ?? String(decoding: body.prefix(200), as: UTF8.self)
        if statusCode == 403 {
            if isSSORequired(ssoHeader) { return .ssoRequired(ssoURL(from: ssoHeader)) }
            return .blocked(message)
        }
        return .error("HTTP \(statusCode): \(message)")
    }

    private struct ErrorBody: Decodable { let message: String }
}

extension GitHubClient {
    /// Checks the token, its scopes and access to each organization the user belongs to.
    public func diagnose() async throws -> ConnectionDiagnosis {
        let (userData, userResponse) = try await get("/user")
        guard (200..<300).contains(userResponse.statusCode) else {
            throw GitHubError.http(userResponse.statusCode, String(decoding: userData, as: UTF8.self))
        }
        let login = try JSONDecoder().decode(Login.self, from: userData).login
        let scopes = ConnectionDiagnosis.scopes(from: userResponse.value(forHTTPHeaderField: "X-OAuth-Scopes"))

        let (orgData, orgResponse) = try await get("/user/orgs?per_page=100")
        let orgLogins = (200..<300).contains(orgResponse.statusCode)
            ? (try? JSONDecoder().decode([Login].self, from: orgData).map(\.login)) ?? []
            : []
        let organizations = try await withThrowingTaskGroup(of: ConnectionDiagnosis.Organization.self) { group in
            for org in orgLogins {
                group.addTask {
                    do {
                        let (data, response) = try await get("/orgs/\(org)/repos?per_page=1")
                        return .init(login: org, status: ConnectionDiagnosis.orgStatus(
                            statusCode: response.statusCode,
                            ssoHeader: response.value(forHTTPHeaderField: "X-GitHub-SSO"), body: data))
                    } catch GitHubError.unauthorized {
                        throw GitHubError.unauthorized
                    } catch {
                        return .init(login: org, status: .error(error.localizedDescription))
                    }
                }
            }
            return try await group.reduce(into: []) { $0.append($1) }
        }
        return ConnectionDiagnosis(
            login: login, scopes: scopes,
            organizations: organizations.sorted { $0.login.lowercased() < $1.login.lowercased() })
    }

    private struct Login: Decodable { let login: String }
}
