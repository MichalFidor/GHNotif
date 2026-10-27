import Foundation
import GHNotifCore

// Compile with AppModel and isolated collaborators: no GitHub requests, notifications, or real preferences.
enum TokenSource { case ghCLI }
struct ResolvedToken { let value: String; let source: TokenSource }
enum TokenProvider {
    static func resolve() -> ResolvedToken? { ResolvedToken(value: "test", source: .ghCLI) }
}
enum Keychain {
    static func savePAT(_ value: String) {}
    static func deletePAT() {}
}
final class Notifier {
    static var notifiedIDs: [String] = []
    func requestAuthorization() {}
    func notify(about prs: [PullRequest]) { Self.notifiedIDs += prs.map(\.id) }
}
struct Outcome {
    let pullRequests: [PullRequest]
    let ssoBlockedOrgIDs: Set<String> = []
}
struct GitHubClient {
    let token: String
    static var results: [PullRequest] = []
    func searchPullRequests(authors: [String], extraQualifiers: String) async throws -> Outcome {
        Outcome(pullRequests: Self.results)
    }
    func canonicalLogin(for login: String) async throws -> String? { login }
}

@MainActor
func runChecks() async throws {
    let domain = "GHNotifTests.\(UUID().uuidString)"
    let preferences = UserDefaults(suiteName: domain)!
    preferences.removePersistentDomain(forName: domain)
    defer { preferences.removePersistentDomain(forName: domain) }

    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = ConfigStore(fileURL: directory.appendingPathComponent("config.json"))
    try store.save(AppConfig(users: ["test-user"]))
    let pr = PullRequest(
        id: "PR_TEST", number: 1, title: "Test", url: URL(string: "https://github.com/o/r/pull/1")!,
        repository: "o/r", author: "test-user", authorAvatarURL: nil,
        createdAt: Date(), updatedAt: Date(), reviewDecision: nil, checks: nil
    )
    GitHubClient.results = [pr]
    let model = AppModel(store: store, preferences: preferences)
    // Let the initial refresh loop finish before explicitly refreshing.
    for _ in 0..<100 {
        if model.lastRefresh != nil { break }
        try await Task.sleep(for: .milliseconds(10))
    }
    precondition(model.pullRequests.map(\.id) == [pr.id], "Initial PR is visible")
    model.dismiss(pr)
    precondition(model.pullRequests.isEmpty && model.dismissedPullRequests.map(\.id) == [pr.id])
    await model.refresh()
    precondition(model.pullRequests.isEmpty, "Dismissal survives refresh")
    precondition(preferences.stringArray(forKey: "dismissedPullRequestIDs") == [pr.id])
    precondition(preferences.synchronize(), "Preferences must be written before the restart check")
    let child = Process()
    child.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    child.arguments = ["--verify-persistence", domain]
    try child.run()
    child.waitUntilExit()
    precondition(child.terminationStatus == 0, "A fresh process reads the dismissed PR from disk")

    // Simulate the PR leaving search (closed/permissions/filter change), then reappearing.
    GitHubClient.results = []
    await model.refresh()
    GitHubClient.results = [pr]
    await model.refresh()
    precondition(model.pullRequests.isEmpty, "A reappearing PR remains dismissed")
    precondition(Notifier.notifiedIDs.isEmpty, "Dismissed PR must not notify again")

    let restarted = AppModel(store: store, preferences: preferences)
    precondition(restarted.dismissedIDs.contains(pr.id), "A new model restores persisted dismissal")
    model.restore(pr)
    precondition(model.pullRequests.map(\.id) == [pr.id] && model.dismissedPullRequests.isEmpty)
    precondition(preferences.stringArray(forKey: "dismissedPullRequestIDs") == [])
    model.config.users = []
    await model.refresh()
    precondition(model.allPullRequests.isEmpty, "An empty watch list clears results")
    print("All app checks passed (dismiss, refresh, reappearance, persistence, restore, notifications)")
}

if CommandLine.arguments.count == 3 && CommandLine.arguments[1] == "--verify-persistence" {
    let preferences = UserDefaults(suiteName: CommandLine.arguments[2])!
    exit(preferences.stringArray(forKey: "dismissedPullRequestIDs") == ["PR_TEST"] ? 0 : 1)
}

Task { @MainActor in
    do {
        try await runChecks()
        exit(0)
    } catch {
        fputs("App checks failed: \(error)\n", stderr)
        exit(1)
    }
}
RunLoop.main.run()
