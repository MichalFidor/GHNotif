import Foundation
import AppKit
import GHNotifCore

extension Bundle {
    static var module: Bundle { Bundle(path: CommandLine.arguments[1])! }
}

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
    var onUpdateNow: (() -> Void)?
    var onPullRequestOpened: ((URL) -> Void)?
    static var notifiedIDs: [String] = []
    static var notifiedDetails: [[String]] = []
    func requestAuthorization() {}
    func sendTest() async throws -> String { "Test" }
    func diagnostics() async -> String { "Test" }
    func notifyUpdateAvailable(_ release: GitHubRelease) async throws -> Bool { false }
    func notify(about alerts: [PullRequestAlert]) {
        Self.notifiedIDs += alerts.map(\.pullRequest.id)
        Self.notifiedDetails += alerts.map(\.details)
    }
}
enum UpdateInstaller {
    static func prepare(release: GitHubRelease, installedBundle: URL) async throws {}
}
final class ReviewReminderPresenter {
    static var shownCount = 0
    func show(model: AppModel) { Self.shownCount += 1 }
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
    let loadingIcon = MenuBarIcon.image(count: 0, hasError: false, isReady: false)
    let approvalIcon = MenuBarIcon.image(count: 0, hasError: false, isReady: true)
    precondition(loadingIcon.size.width == 18 && approvalIcon.size.width == 38,
                 "Approval badge appears beside the logo only after a successful load")
    precondition(approvalIcon.accessibilityDescription == L("GHNotif - brak PR-ów do review", "GHNotif - no PRs to review"),
                 "Empty review queue has an accessible status")
    precondition(MenuBarIcon.image(count: 3, hasError: false, isReady: true).size
                 == MenuBarIcon.image(count: 3, hasError: false, isReady: false).size,
                 "A nonempty queue keeps its count without an approval badge")
    precondition(MenuBarIcon.image(count: 0, hasError: true, isReady: true).size
                 == MenuBarIcon.image(count: 0, hasError: true, isReady: false).size,
                 "Errors never show an approval badge")
    precondition(NotificationIcon.image(for: .approved).isValid, "Real GitHub approval badge loads from resources")
    let renderedApproval = NSBitmapImageRep(data: approvalIcon.tiffRepresentation!)!
    var greenPixels = 0
    for x in 22..<renderedApproval.pixelsWide {
        for y in 0..<renderedApproval.pixelsHigh {
            let color = renderedApproval.colorAt(x: x, y: y)!.usingColorSpace(.deviceRGB)!
            if color.alphaComponent > 0.5 && color.greenComponent > color.redComponent * 1.5 {
                greenPixels += 1
            }
        }
    }
    precondition(greenPixels > 10, "Menu bar image actually draws the green GitHub badge beside the logo")
    Notifier.notifiedIDs = []
    Notifier.notifiedDetails = []
    let domain = "GHNotifTests.\(UUID().uuidString)"
    let preferences = UserDefaults(suiteName: domain)!
    preferences.removePersistentDomain(forName: domain)
    defer { preferences.removePersistentDomain(forName: domain) }

    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = ConfigStore(fileURL: directory.appendingPathComponent("config.json"))
    try store.save(AppConfig(users: ["test-user"], language: .polish))
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
    var openedURLs: [URL] = []
    var canOpenURL = true
    let model = AppModel(store: store, preferences: preferences, urlOpener: {
        openedURLs.append($0)
        return canOpenURL
    }, releaseLoader: {
        if releaseFails { throw GitHubError.http(403, "Rate limit") }
        return releaseResult
    })
    // Let the initial refresh loop finish before explicitly refreshing.
    for _ in 0..<100 {
        if model.lastRefresh != nil { break }
        try await Task.sleep(for: .milliseconds(10))
    }
    precondition(model.pullRequests.map(\.id) == [pr.id], "Initial PR is visible")
    precondition(model.inbox.entries[pr.id]?.isUnread == false, "Initial list establishes a read baseline")
    let activeReview = Activity(login: "reviewer", date: Date(), kind: .approved, id: "review-active")
    GitHubClient.results = [makePR(activity: [activeReview])]
    await model.refresh()
    precondition(Notifier.notifiedDetails.last == ["✓ reviewer - zatwierdzenie"],
                 "Activity on an active PR notifies with actor and action")
    precondition(model.inbox.entries[pr.id]?.details == ["✓ reviewer - zatwierdzenie"],
                 "Panel retains activity independently of delivered notifications")
    let restoredInbox = try JSONDecoder().decode(PullRequestInbox.self, from: preferences.data(forKey: "pullRequestInbox")!)
    precondition(restoredInbox.entries[pr.id]?.isUnread == true, "Unread changes are persisted")
    canOpenURL = false
    model.openPullRequest(GitHubClient.results[0])
    precondition(model.inbox.entries[pr.id]?.isUnread == true && model.lastError != nil,
                 "Failed browser launch keeps changes unread and surfaces the error")
    canOpenURL = true
    model.openPullRequest(GitHubClient.results[0])
    precondition(openedURLs.last == pr.url, "Opening a PR uses its browser URL")
    precondition(model.inbox.entries[pr.id]?.isUnread == false && model.pullRequests.count == 1,
                 "Marking read does not dismiss the PR")
    let activeAlertCount = Notifier.notifiedIDs.count
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == activeAlertCount, "Activity does not notify twice on refresh")
    precondition(model.inbox.entries[pr.id]?.isUnread == false, "Reading does not retrigger old activity")
    GitHubClient.results = [makePR(activity: [
        activeReview, Activity(login: "second-reviewer", date: Date(), kind: .approved, id: "another-review")
    ])]
    await model.refresh()
    precondition(model.inbox.entries[pr.id]?.isUnread == true, "Later activity marks the PR unread again")
    model.markRead(GitHubClient.results[0])
    let countAfterSecondReview = Notifier.notifiedIDs.count
    GitHubClient.results = [makePR(activity: [Activity(login: "me", date: Date(), id: "own-comment")])]
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == countAfterSecondReview, "Own activity does not notify")
    precondition(model.inbox.entries[pr.id]?.isUnread == false, "Own activity does not mark the PR unread")
    model.config.notify = false
    GitHubClient.results = [makePR(oid: "c2")]
    await model.refresh()
    precondition(model.inbox.entries[pr.id]?.details == ["↑ Nowe commity"],
                 "Panel tracks changes even when notifications are disabled")
    model.markAllRead()
    precondition(model.inbox.entries[pr.id]?.isUnread == false, "Mark all clears unread changes")
    model.config.notify = true
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

    // Scoped so the extra model is released before its refresh loop can run and notify concurrently.
    weak var released: AppModel?
    do {
        let restarted = AppModel(store: store, preferences: preferences)
        released = restarted
        precondition(restarted.dismissals[pr.id]?.headCommitOID == "c1", "A new model restores persisted dismissal")
    }
    precondition(released == nil, "The extra model must be released so it cannot refresh concurrently")

    // Own comments don't bring the PR back; comments by others and new commits do.
    let later = Date().addingTimeInterval(60)
    GitHubClient.results = [makePR(activity: [Activity(login: "me", date: later)])]
    await model.refresh()
    precondition(model.pullRequests.isEmpty, "Own comment keeps the PR dismissed")
    GitHubClient.results = [makePR(activity: [Activity(login: "reviewer", date: later, kind: .approved)])]
    await model.refresh()
    precondition(model.pullRequests.map(\.id) == [pr.id], "Review by someone else brings the PR back")
    precondition(Notifier.notifiedIDs == [pr.id], "Reactivated PR notifies: \(Notifier.notifiedIDs)")
    precondition(Notifier.notifiedDetails.last == ["✓ reviewer - zatwierdzenie"],
                 "Notification says who did what: \(Notifier.notifiedDetails)")
    model.dismiss(model.pullRequests[0])
    GitHubClient.results = [makePR(oid: "c2", activity: [Activity(login: "reviewer", date: later)])]
    await model.refresh()
    precondition(model.pullRequests.map(\.id) == [pr.id], "New commit brings the PR back")
    precondition(Notifier.notifiedDetails.last?.first == "↑ Nowe commity", "Push is described: \(Notifier.notifiedDetails)")

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
    let commentAfterReview = Activity(login: "commenter", date: reviewAt.addingTimeInterval(60), id: "after-review")
    let reviewedPR = makePR(
        activity: [commentAfterReview],
        reviews: [PullRequestReview(login: "me", commitOID: "c1", submittedAt: reviewAt)]
    )
    GitHubClient.results = [reviewedPR]
    let beforeComment = Notifier.notifiedIDs.count
    await model.refresh()
    precondition(model.pullRequests.map(\.id) == [pr.id], "Comment after my review reactivates the PR")
    precondition(Notifier.notifiedIDs.count == beforeComment + 1
                 && Notifier.notifiedDetails.last == ["• commenter - komentarz"],
                 "Comment after review notifies once")
    for _ in 0..<4 { await model.refresh() }
    precondition(Notifier.notifiedIDs.count == beforeComment + 1,
                 "Repeated refreshes must not re-dismiss and re-notify the same comment after review")
    let nextComment = Activity(login: "commenter", date: reviewAt.addingTimeInterval(120), id: "next-after-review")
    GitHubClient.results = [makePR(
        activity: [commentAfterReview, nextComment], reviews: reviewedPR.reviews
    )]
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == beforeComment + 2,
                 "A genuinely new comment after review still notifies")
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == beforeComment + 2, "New comment is not repeated either")
    model.config.notifications.mutedEvents = [.comment]
    let mutedComment = Activity(login: "commenter", date: reviewAt.addingTimeInterval(150), id: "muted-comment")
    GitHubClient.results = [makePR(
        activity: [commentAfterReview, nextComment, mutedComment], reviews: reviewedPR.reviews
    )]
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == beforeComment + 2, "Muted event type does not notify")
    model.config.notifications.mutedEvents = []
    await model.refresh()
    precondition(Notifier.notifiedIDs.count == beforeComment + 2, "Muted activity is not delivered later")
    model.snooze(pr, until: Date().addingTimeInterval(3600))
    await model.refresh()
    precondition(model.snoozedRequests.map(\.id) == [pr.id] && Notifier.notifiedIDs.count == beforeComment + 2,
                 "Old review does not cancel snooze after later activity")
    GitHubClient.results = [makePR(
        activity: [commentAfterReview, nextComment],
        reviews: [PullRequestReview(login: "me", commitOID: "c1", submittedAt: reviewAt.addingTimeInterval(180))]
    )]
    await model.refresh()
    precondition(model.pullRequests.isEmpty && model.dismissedPullRequests.map(\.id) == [pr.id],
                 "A newer review marks the PR done again")
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

    let calendar = Calendar.current
    let tomorrow = calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: Date()))!
    func tomorrowAt(_ hour: Int, _ minute: Int) -> Date {
        calendar.date(bySettingHour: hour, minute: minute, second: 0, of: tomorrow)!
    }
    ReviewReminderPresenter.shownCount = 0
    model.config.reviewReminder = ReviewReminderSettings(
        alarms: [ReminderAlarm(time: ReminderTime(hour: 9, minute: 0)!)], weekdays: Set(1...7)
    )
    precondition(model.nextReviewReminder == tomorrowAt(9, 0) || calendar.isDateInToday(model.nextReviewReminder!),
                 "Next reminder is exposed for settings")
    model.checkReviewReminder(now: tomorrowAt(8, 59))
    precondition(ReviewReminderPresenter.shownCount == 0, "Reminder waits for its time")
    model.checkReviewReminder(now: tomorrowAt(9, 0))
    precondition(ReviewReminderPresenter.shownCount == 1, "Reminder window opens at the scheduled time")
    model.checkReviewReminder(now: tomorrowAt(9, 1))
    precondition(ReviewReminderPresenter.shownCount == 1, "Reminder opens once per scheduled time")
    model.snoozeReviewReminder(minutes: 15, now: tomorrowAt(9, 1))
    model.checkReviewReminder(now: tomorrowAt(9, 15))
    precondition(ReviewReminderPresenter.shownCount == 1, "Snoozed reminder waits 15 minutes")
    model.checkReviewReminder(now: tomorrowAt(9, 16))
    precondition(ReviewReminderPresenter.shownCount == 2, "Snoozed reminder returns")
    let storedReminder = preferences.data(forKey: "reviewReminderState")
        .flatMap { try? JSONDecoder().decode(ReviewReminderState.self, from: $0) }
    precondition(storedReminder?.lastHandled == tomorrowAt(9, 16), "Reminder state is persisted")
    let reloadedConfig = try store.load()
    precondition(reloadedConfig.reviewReminder.enabled && reloadedConfig.reviewReminder.activeTimes.map(\.description) == ["09:00"],
                 "Reminder settings are saved in config")
    model.config.language = .english
    precondition(L("Odśwież", "Refresh") == "Refresh", "Choosing English switches texts immediately")
    let savedLanguage = try store.load().language
    precondition(savedLanguage == .english, "Language choice is saved")
    model.config.language = .polish
    precondition(L("Odśwież", "Refresh") == "Odśwież", "Choosing Polish switches texts back")
    print("All app checks passed (dismiss, refresh, reactivation, persistence, migration, collapse, restore, notifications, review reminder, language)")
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
