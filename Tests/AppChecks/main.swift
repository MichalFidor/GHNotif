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
    static var notifiedDetails: [[String]] = []
    func requestAuthorization() {}
    func sendTest() async throws -> String { "Test" }
    func diagnostics() async -> String { "Test" }
    func notify(about alerts: [PullRequestAlert]) {
        Self.notifiedIDs += alerts.map(\.pullRequest.id)
        Self.notifiedDetails += alerts.map(\.details)
    }
}
struct Outcome {
    let pullRequests: [PullRequest]
    let ssoBlockedOrgIDs: Set<String> = []
    let viewerLogin: String? = "me"
}
struct GitHubClient {
    let token: String
    static var results: [PullRequest] = []
    func searchPullRequests(config: AppConfig) async throws -> Outcome {
        Outcome(pullRequests: Self.results.filter(config.includes))
    }
    func canonicalLogin(for login: String) async throws -> String? { login }
    func teamMembers(organization: String, team: String) async throws -> [String] { ["member1", "member2"] }
}

@MainActor
func runChecks() async throws {
    Notifier.notifiedIDs = []
    Notifier.notifiedDetails = []
    let domain = "GHNotifTests.\(UUID().uuidString)"
    let preferences = UserDefaults(suiteName: domain)!
    preferences.removePersistentDomain(forName: domain)
    defer { preferences.removePersistentDomain(forName: domain) }

    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = ConfigStore(fileURL: directory.appendingPathComponent("config.json"))
    try store.save(AppConfig(users: ["test-user"]))
    func makePR(oid: String = "c1", activity: [Activity] = [], reviews: [PullRequestReview] = []) -> PullRequest {
        PullRequest(
            id: "PR_TEST", number: 1, title: "Test", url: URL(string: "https://github.com/o/r/pull/1")!,
            repository: "o/r", author: "test-user", authorAvatarURL: nil,
            createdAt: Date(), updatedAt: Date(), reviewDecision: nil, checks: nil,
            headCommitOID: oid, activity: activity, reviews: reviews
        )
    }
    let pr = makePR()
    GitHubClient.results = [pr]
    var releaseResult: GitHubRelease?
    var releaseFails = false
    let model = AppModel(store: store, preferences: preferences, releaseLoader: {
        if releaseFails { throw GitHubError.http(403, "Rate limit") }
        return releaseResult
    })
    // Let the initial refresh loop finish before explicitly refreshing.
    for _ in 0..<100 {
        if model.lastRefresh != nil { break }
        try await Task.sleep(for: .milliseconds(10))
    }
    precondition(model.pullRequests.map(\.id) == [pr.id], "Initial PR is visible")
    let activeReview = Activity(login: "reviewer", date: Date(), kind: .approved, id: "review-active")
    GitHubClient.results = [makePR(activity: [activeReview])]
    await model.refresh()
    precondition(Notifier.notifiedDetails.last == ["reviewer — zatwierdzenie ✅"],
                 "Activity on an active PR notifies with actor and action")
    let activeAlertCount = Notifier.notifiedIDs.count
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == activeAlertCount, "Activity does not notify twice on refresh")
    GitHubClient.results = [makePR(activity: [Activity(login: "me", date: Date(), id: "own-comment")])]
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == activeAlertCount, "Own activity does not notify")
    GitHubClient.results = [pr]
    await model.refresh()
    let snapshotsData = preferences.data(forKey: "notificationActivitySnapshots")!
    let snapshots = try JSONDecoder().decode([String: NotificationSnapshot].self, from: snapshotsData)
    precondition(snapshots[pr.id]?.activityDates["review-active"] != nil,
                 "Seen activity is persisted to avoid repeats after restart")
    Notifier.notifiedIDs = []
    Notifier.notifiedDetails = []
    let releaseData = Data("""
    {"tag_name":"v0.2.0","html_url":"https://github.com/MichalFidor/GHNotif/releases/tag/v0.2.0","draft":false,"prerelease":false}
    """.utf8)
    releaseResult = try JSONDecoder().decode(GitHubRelease.self, from: releaseData)
    await model.checkForUpdates()
    precondition(model.availableRelease?.tagName == "v0.2.0", "Update is exposed to the UI")
    releaseFails = true
    await model.checkForUpdates()
    precondition(model.updateStatus.contains("Rate limit") && !model.checkingUpdates, "Update failure is visible")
    precondition(model.pullRequests.map(\.id) == [pr.id], "Update failure preserves PR results")
    releaseFails = false
    releaseResult = nil
    await model.checkForUpdates()
    precondition(model.availableRelease == nil, "No release clears the update banner")
    model.dismiss(pr)
    precondition(model.pullRequests.isEmpty && model.dismissedPullRequests.map(\.id) == [pr.id])
    await model.refresh()
    precondition(model.pullRequests.isEmpty, "Dismissal survives refresh")
    precondition(preferences.data(forKey: "dismissedPullRequests") != nil, "Dismissal snapshot is persisted")
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
    precondition(restarted.dismissals[pr.id]?.headCommitOID == "c1", "A new model restores persisted dismissal")

    // Own comments don't bring the PR back; comments by others and new commits do.
    let later = Date().addingTimeInterval(60)
    GitHubClient.results = [makePR(activity: [Activity(login: "me", date: later)])]
    await model.refresh()
    precondition(model.pullRequests.isEmpty, "Own comment keeps the PR dismissed")
    GitHubClient.results = [makePR(activity: [Activity(login: "reviewer", date: later, kind: .approved)])]
    await model.refresh()
    precondition(model.pullRequests.map(\.id) == [pr.id], "Review by someone else brings the PR back")
    precondition(Notifier.notifiedIDs == [pr.id], "Reactivated PR notifies: \(Notifier.notifiedIDs)")
    precondition(Notifier.notifiedDetails.last == ["reviewer — zatwierdzenie ✅"],
                 "Notification says who did what: \(Notifier.notifiedDetails)")
    model.dismiss(model.pullRequests[0])
    GitHubClient.results = [makePR(oid: "c2", activity: [Activity(login: "reviewer", date: later)])]
    await model.refresh()
    precondition(model.pullRequests.map(\.id) == [pr.id], "New commit brings the PR back")
    precondition(Notifier.notifiedDetails.last?.first == "Nowe commity", "Push is described: \(Notifier.notifiedDetails)")

    model.dismiss(model.pullRequests[0])
    model.restore(pr)
    precondition(model.pullRequests.map(\.id) == [pr.id] && model.dismissedPullRequests.isEmpty)
    precondition(model.dismissals.isEmpty)

    // Collapsed repositories persist.
    model.toggleCollapsed("o/r")
    precondition(model.isCollapsed("o/r") && AppModel(store: store, preferences: preferences).isCollapsed("o/r"))
    model.toggleCollapsed("o/r")
    precondition(!model.isCollapsed("o/r") && preferences.stringArray(forKey: "collapsedRepositories") == [])

    model.snooze(pr, until: Date().addingTimeInterval(60))
    precondition(model.pullRequests.isEmpty && model.snoozedRequests.map(\.id) == [pr.id], "Snooze hides but preserves the PR")
    model.unsnooze(pr)
    precondition(model.pullRequests.map(\.id) == [pr.id], "Unsnooze restores the PR")
    let reviewAt = Date()
    GitHubClient.results = [makePR(reviews: [PullRequestReview(login: "ME", commitOID: "c1", submittedAt: reviewAt)])]
    await model.refresh()
    precondition(model.pullRequests.isEmpty && model.dismissedPullRequests.map(\.id) == [pr.id],
                 "Review of the current commit automatically marks the PR done")
    GitHubClient.results = [makePR(oid: "c2", reviews: [PullRequestReview(login: "me", commitOID: "c1", submittedAt: reviewAt)])]
    await model.refresh()
    precondition(model.pullRequests.map(\.id) == [pr.id], "A commit after my review reactivates the PR")

    try model.importWatchlist(Data(#"["other-user"]"#.utf8))
    precondition(model.config.users.contains("other-user"), "Watchlist import merges users")
    let watchlist = try JSONDecoder().decode(WatchlistFixture.self, from: model.watchlistData())
    precondition(watchlist.users.contains("other-user") && watchlist.formatVersion == 1, "Watchlist export has only versioned users")
    try await model.importTeam(organization: "example", team: "qa")
    precondition(model.config.users.contains("member1") && model.config.users.contains("member2"), "Team members are imported")

    // Legacy ID-only storage is migrated and gets a commit baseline on the next refresh.
    let legacyDomain = domain + ".legacy"
    let legacy = UserDefaults(suiteName: legacyDomain)!
    defer { legacy.removePersistentDomain(forName: legacyDomain) }
    legacy.set([pr.id], forKey: "dismissedPullRequestIDs")
    GitHubClient.results = [makePR(oid: "c9")]
    let migrated = AppModel(store: store, preferences: legacy)
    precondition(migrated.dismissals[pr.id] != nil && legacy.object(forKey: "dismissedPullRequestIDs") == nil, "Legacy IDs migrated")
    await migrated.refresh()
    precondition(migrated.pullRequests.isEmpty && migrated.dismissals[pr.id]?.headCommitOID == "c9", "Migrated dismissal gets baseline")
    model.config.users = []
    model.config.includeMentions = false
    await model.refresh()
    precondition(model.allPullRequests.isEmpty, "An empty watch list clears results")
    print("All app checks passed (dismiss, refresh, reactivation, persistence, migration, collapse, restore, notifications)")
}

private struct WatchlistFixture: Decodable {
    let formatVersion: Int
    let users: [String]
}

if CommandLine.arguments.count == 3 && CommandLine.arguments[1] == "--verify-persistence" {
    let preferences = UserDefaults(suiteName: CommandLine.arguments[2])!
    let data = preferences.data(forKey: "dismissedPullRequests") ?? Data()
    let dismissals = try? JSONDecoder().decode([String: DismissalSnapshot].self, from: data)
    let activityData = preferences.data(forKey: "notificationActivitySnapshots") ?? Data()
    let activitySnapshots = try? JSONDecoder().decode([String: NotificationSnapshot].self, from: activityData)
    exit(dismissals?["PR_TEST"] != nil && activitySnapshots?["PR_TEST"]?.activityDates["review-active"] != nil ? 0 : 1)
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
