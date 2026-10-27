import Foundation
import Security

enum TokenSource: String {
    case personalAccessToken = "Personal access token (Keychain)"
    case ghCLI = "gh CLI"
}

struct ResolvedToken {
    let value: String
    let source: TokenSource
}

enum TokenProvider {
    /// A PAT saved in Keychain wins; otherwise the token of the logged-in `gh` CLI user is used.
    static func resolve() -> ResolvedToken? {
        if let pat = Keychain.readPAT(), !pat.isEmpty {
            return ResolvedToken(value: pat, source: .personalAccessToken)
        }
        if let token = ghCLIToken() {
            return ResolvedToken(value: token, source: .ghCLI)
        }
        return nil
    }

    // Apps launched from Finder/Dock don't inherit the shell PATH, so common install locations are probed directly.
    private static let ghCandidates = ["/opt/homebrew/bin/gh", "/usr/local/bin/gh", "/usr/bin/gh"]

    private static func ghCLIToken() -> String? {
        guard let path = ghCandidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return nil }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = ["auth", "token", "--hostname", "github.com"]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return nil
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { return nil }
        let token = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return token.isEmpty ? nil : token
    }
}

enum Keychain {
    private static let service = "GHNotif"
    private static let account = "github-pat"

    private static var baseQuery: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func readPAT() -> String? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    static func savePAT(_ token: String) -> Bool {
        deletePAT()
        var query = baseQuery
        query[kSecValueData as String] = Data(token.utf8)
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(query as CFDictionary, nil) == errSecSuccess
    }

    static func deletePAT() {
        SecItemDelete(baseQuery as CFDictionary)
    }
}
