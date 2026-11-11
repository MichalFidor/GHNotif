import Foundation
import AppKit
import GHNotifCore
import ServiceManagement

@MainActor
final class AppModel: ObservableObject {
    @Published var config: AppConfig {
        didSet {
            Localization.language = config.language
            configDidChange(from: oldValue)
        }
    }

    /// Every PR returned by the last search, including ones the user dismissed.
    @Published private(set) var allPullRequests: [PullRequest] = []
    @Published private(set) var inbox: PullRequestInbox {
        didSet {
            do {
                preferences.set(try JSONEncoder().encode(inbox), forKey: Self.inboxKey)
            } catch {
                lastError = L("Nie udało się zapisać stanu przeczytania: \(error.localizedDescription)",
                              "Could not save read status: \(error.localizedDescription)")
                NSLog("GHNotif: %@", error.localizedDescription)
            }
        }
    }
    /// Dismissed PRs stay hidden until the user restores them or someone pushes commits / comments / reviews.
    @Published private(set) var dismissals: [String: DismissalSnapshot] {
        didSet { preferences.set(try? JSONEncoder().encode(dismissals), forKey: Self.dismissalsKey) }
    }
    /// Repositories whose PR group is collapsed in the list.
    @Published private(set) var collapsedRepositories: Set<String> {
        didSet { preferences.set(collapsedRepositories.sorted(), forKey: Self.collapsedKey) }
    }
    @Published private(set) var snoozedPullRequests: [String: Date] {
        didSet { preferences.set(try? JSONEncoder().encode(snoozedPullRequests), forKey: Self.snoozedKey) }
    }
    private var notificationSnapshots: [String: NotificationSnapshot] = [:] {
        didSet {
            do {
                preferences.set(try JSONEncoder().encode(notificationSnapshots), forKey: Self.notificationSnapshotsKey)
            } catch {
                NSLog("GHNotif: nie udało się zapisać stanu powiadomień: %@", error.localizedDescription)
            }
        }
    }
    private var reminderState: ReviewReminderState {
        didSet { preferences.set(try? JSONEncoder().encode(reminderState), forKey: Self.reminderStateKey) }
    }
    @Published private(set) var isLoading = false
    @Published private(set) var lastError: String?
    @Published private(set) var lastRefresh: Date?
    @Published private(set) var ssoBlockedOrgCount = 0
    @Published private(set) var tokenSource: TokenSource?
    @Published private(set) var availableRelease: GitHubRelease?
    @Published private(set) var updateStatus = ""
    @Published private(set) var checkingUpdates = false
    @Published private(set) var notificationStatus = ""
    @Published private(set) var launchAtLogin = SMAppService.mainApp.status == .enabled
    @Published private(set) var checkingConnection = false
    /// Findings of the last "Check connection"; empty until the user runs it.
    @Published private(set) var connectionReport: [String] = []
    @Published private(set) var connectionHasProblems = false
    @Published private(set) var connectionSSOURL: URL?
    /// True when no config file existed at launch; Settings opens on the "Get started" pane once.
    private(set) var isFirstLaunch = false

    var installedVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    func checkForUpdates() async {
        guard !checkingUpdates else { return }
        checkingUpdates = true
        defer { checkingUpdates = false }
        do {
            let release = try await releaseLoader()
            availableRelease = try release.flatMap { try $0.isNewer(than: installedVersion) ? $0 : nil }
            updateStatus = availableRelease.map { L("Dostępna wersja \($0.tagName)", "Version \($0.tagName) is available") }
                ?? L("Masz najnowszą wersję.", "You have the latest version.")
            if let availableRelease,
               preferences.string(forKey: Self.notifiedUpdateVersionKey) != availableRelease.tagName,
               try await notifier.notifyUpdateAvailable(availableRelease) {
                preferences.set(availableRelease.tagName, forKey: Self.notifiedUpdateVersionKey)
            }
        } catch {
            updateStatus = L("Nie udało się sprawdzić aktualizacji: \(error.localizedDescription)", "Could not check for updates: \(error.localizedDescription)")
        }
    }

    func openRelease() {
        guard let availableRelease else { return }
        do {
            _ = try availableRelease.isNewer(than: installedVersion)
            guard urlOpener(availableRelease.htmlURL) else {
                throw GitHubError.graphQL(L("Nie udało się otworzyć strony wydania.", "Could not open the release page."))
            }
            updateStatus = L("Samoinstalacja jest wyłączona. Zbuduj aktualizację z przejrzanych źródeł.",
                             "Self-installation is disabled. Build the update from reviewed source.")
        } catch {
            updateStatus = L("Nie udało się otworzyć wydania: \(error.localizedDescription)",
                             "Could not open the release: \(error.localizedDescription)")
        }
    }

    func testNotification() async {
        do {
            notificationStatus = try await notifier.sendTest()
        } catch {
            notificationStatus = L("Błąd powiadomienia: \(error.localizedDescription)", "Notification error: \(error.localizedDescription)")
        }
    }

    private let store: ConfigStore
    private let preferences: UserDefaults
    private let releaseLoader: () async throws -> GitHubRelease?
    private let urlOpener: (URL) -> Bool
    private let notifier = Notifier()
    private let reminderPresenter = ReviewReminderPresenter()
    private var token: ResolvedToken?
    private var refreshLoop: Task<Void, Never>?
    private var updateLoop: Task<Void, Never>?
    private var reminderLoop: Task<Void, Never>?
    private var refreshPending = false
    /// After the watched set changes, the next result is treated as a baseline so existing PRs don't trigger alerts.
    private var suppressNextNotifications = false

    private static let legacyDismissedIDsKey = "dismissedPullRequestIDs"
    private static let dismissalsKey = "dismissedPullRequests"
    private static let collapsedKey = "collapsedRepositories"
    private static let seenIDsKey = "seenPullRequestIDs"
    private static let snoozedKey = "snoozedPullRequests"
    private static let notificationSnapshotsKey = "notificationActivitySnapshots"
    private static let inboxKey = "pullRequestInbox"
    private static let reminderStateKey = "reviewReminderState"
    private static let notifiedUpdateVersionKey = "notifiedUpdateVersion"

    var pullRequests: [PullRequest] {
        allPullRequests.filter { dismissals[$0.id] == nil && !isSnoozed($0) }
    }
    var dismissedPullRequests: [PullRequest] { allPullRequests.filter { dismissals[$0.id] != nil } }
    var snoozedRequests: [PullRequest] { allPullRequests.filter { isSnoozed($0) } }

    func markRead(_ pr: PullRequest) { inbox.markRead(pr) }

    func openPullRequest(_ pr: PullRequest) {
        guard pr.url.scheme == "https", urlOpener(pr.url) else {
            lastError = L("Nie udało się otworzyć PR-a w przeglądarce.",
                          "Could not open the PR in the browser.")
            return
        }
        markRead(pr)
    }

    func markAllRead() {
        var updated = inbox
        for pr in allPullRequests { updated.markRead(pr) }
        inbox = updated
    }

    private func isSnoozed(_ pr: PullRequest) -> Bool {
        guard let until = snoozedPullRequests[pr.id] else { return false }
        return until > Date()
    }

    func dismiss(_ pr: PullRequest) {
        snoozedPullRequests[pr.id] = nil
        dismissals[pr.id] = DismissalSnapshot(dismissedAt: Date(), headCommitOID: pr.headCommitOID)
    }
    func restore(_ pr: PullRequest) { dismissals[pr.id] = nil }
    func snooze(_ pr: PullRequest, until: Date) {
        dismissals[pr.id] = nil
        snoozedPullRequests[pr.id] = until
    }
    func unsnooze(_ pr: PullRequest) { snoozedPullRequests[pr.id] = nil }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            launchAtLogin = SMAppService.mainApp.status == .enabled
            if launchAtLogin != enabled { lastError = L("Nie udało się zmienić ustawienia uruchamiania przy logowaniu.", "Could not change the launch at login setting.") }
        } catch {
            lastError = L("Nie udało się zmienić uruchamiania przy logowaniu: \(error.localizedDescription)", "Could not change launch at login: \(error.localizedDescription)")
            launchAtLogin = SMAppService.mainApp.status == .enabled
        }
    }

    func watchlistData() throws -> Data {
        try JSONEncoder().encode(WatchlistDocument(users: config.users))
    }

    func importWatchlist(_ data: Data) throws {
        let imported: [String]
        let text = String(decoding: data, as: UTF8.self)
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("{") {
            imported = try JSONDecoder().decode(WatchlistDocument.self, from: data).users
        } else if trimmed.hasPrefix("[") {
            imported = try JSONDecoder().decode([String].self, from: data)
        } else {
            imported = text.split(whereSeparator: { $0 == "," || $0.isNewline })
                .map { String($0).trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            guard !imported.isEmpty else { throw GitHubError.graphQL(L("Plik listy osób jest pusty lub nieprawidłowy.", "The people list file is empty or invalid.")) }
        }
        let combined = SearchQueryBuilder.normalizeLogins(config.users + (try SearchQueryBuilder.validatedLogins(imported)))
        config.users = combined
    }

    func importTeam(organization: String, team: String) async throws {
        guard let token = await currentToken() else {
            throw GitHubError.graphQL(L("Brak tokenu GitHub.", "No GitHub token."))
        }
        let members = try await client(token).teamMembers(organization: organization, team: team)
        if !members.isEmpty {
            config.users = SearchQueryBuilder.normalizeLogins(config.users + (try SearchQueryBuilder.validatedLogins(members)))
        }
    }

    func isCollapsed(_ repository: String) -> Bool { collapsedRepositories.contains(repository) }
    func toggleCollapsed(_ repository: String) {
        if collapsedRepositories.remove(repository) == nil { collapsedRepositories.insert(repository) }
    }
    private var seenIDs: Set<String>? {
        get { preferences.stringArray(forKey: Self.seenIDsKey).map(Set.init) }
        set { preferences.set(newValue.map(Array.init), forKey: Self.seenIDsKey) }
    }

    init(store: ConfigStore = ConfigStore(), preferences: UserDefaults = .standard,
         urlOpener: @escaping (URL) -> Bool = { NSWorkspace.shared.open($0) },
         releaseLoader: @escaping () async throws -> GitHubRelease? = { try await GitHubRelease.latest() }) {
        self.store = store
        self.preferences = preferences
        self.releaseLoader = releaseLoader
        self.urlOpener = urlOpener
        isFirstLaunch = !FileManager.default.fileExists(atPath: store.fileURL.path)
        inbox = PullRequestInbox()
        if let data = preferences.data(forKey: Self.inboxKey) {
            do {
                inbox = try JSONDecoder().decode(PullRequestInbox.self, from: data)
            } catch {
                NSLog("GHNotif: nie udało się odczytać stanu przeczytania: %@", error.localizedDescription)
            }
        }
        dismissals = Self.loadDismissals(from: preferences)
        notificationSnapshots = Self.loadNotificationSnapshots(from: preferences)
        collapsedRepositories = Set(preferences.stringArray(forKey: Self.collapsedKey) ?? [])
        reminderState = preferences.data(forKey: Self.reminderStateKey)
            .flatMap { try? JSONDecoder().decode(ReviewReminderState.self, from: $0) } ?? ReviewReminderState()
        if let data = preferences.data(forKey: Self.snoozedKey),
           let decoded = try? JSONDecoder().decode([String: Date].self, from: data) {
            snoozedPullRequests = decoded
        } else {
            snoozedPullRequests = [:]
        }
        do {
            config = try store.load()
        } catch {
            config = .default
            lastError = L("Nie udało się wczytać \(store.fileURL.path): \(error.localizedDescription). Używam domyślnej listy (plik nie zostanie nadpisany do pierwszej zmiany).",
                          "Could not load \(store.fileURL.path): \(error.localizedDescription). Using the default list (the file won't be overwritten until the first change).")
        }
        Localization.language = config.language
        notifier.onUpdateNow = { [weak self] in
            Task { @MainActor in self?.openRelease() }
        }
        notifier.onPullRequestOpened = { [weak self] url in
            Task { @MainActor in
                if let pr = self?.allPullRequests.first(where: { $0.url == url }) {
                    self?.markRead(pr)
                }
            }
        }
        if config.notify { notifier.requestAuthorization() }
        if ProcessInfo.processInfo.arguments.contains("--test-notification") {
            Task {
                await testNotification()
                print(notificationStatus)
                fflush(stdout)
                try? await Task.sleep(for: .seconds(8))
                print(await notifier.diagnostics())
                fflush(stdout)
            }
        }
        startRefreshLoop()
        reminderLoop = Task { [weak self] in
            while !Task.isCancelled {
                self?.checkReviewReminder()
                do { try await Task.sleep(for: .seconds(30)) }
                catch { return }
            }
        }
        if Bundle.main.bundleIdentifier == "com.michalfidor.ghnotif" {
            updateLoop = Task { [weak self] in
                while !Task.isCancelled {
                    await self?.checkForUpdates()
                    do { try await Task.sleep(for: .seconds(6 * 60 * 60)) }
                    catch { return }
                }
            }
        }
    }

    var nextReviewReminder: Date? { config.reviewReminder.nextOccurrence(after: Date()) }
    var reviewReminderSnoozedUntil: Date? { reminderState.snoozedUntil }

    /// Called periodically; a reminder missed during sleep is caught up on the first check after wake.
    func checkReviewReminder(now: Date = Date()) {
        var state = reminderState
        let show = state.shouldShow(settings: config.reviewReminder, now: now)
        if state != reminderState { reminderState = state }
        if show { showReviewReminder() }
    }

    func showReviewReminder() {
        reminderPresenter.show(model: self)
        Task { await refresh() }
    }

    func snoozeReviewReminder(minutes: Int, now: Date = Date()) {
        reminderState.snoozedUntil = now.addingTimeInterval(TimeInterval(minutes * 60))
        objectWillChange.send()
    }

    var ssoSettingsURL: URL {
        config.gitHubHost.ssoSettingsURL(usingGitHubCLI: tokenSource == .ghCLI)
    }

    /// Returns true once, on the first launch without a config file.
    func consumeFirstLaunch() -> Bool {
        defer { isFirstLaunch = false }
        return isFirstLaunch
    }

    /// Resolves the token so Settings can show its source before the first search runs.
    func loadTokenSource() async {
        _ = await currentToken()
    }

    func checkConnection() async {
        guard !checkingConnection else { return }
        checkingConnection = true
        defer { checkingConnection = false }
        connectionSSOURL = nil
        guard let token = await currentToken() else {
            connectionReport = [L("✗ Brak tokenu - zaloguj się przez `gh auth login` albo zapisz PAT poniżej.",
                                  "✗ No token - sign in with `gh auth login` or save a PAT below.")]
            connectionHasProblems = true
            return
        }
        do {
            let diagnosis = try await client(token).diagnose()
            // A report for a server the user has switched away from in the meantime is dropped.
            guard token.host == config.gitHubHost.name else { return }
            connectionReport = diagnosis.lines
            connectionHasProblems = diagnosis.hasProblems
            connectionSSOURL = diagnosis.ssoAuthorizationURL
        } catch GitHubError.unauthorized {
            self.token = nil
            connectionReport = ["✗ " + (GitHubError.unauthorized.errorDescription ?? "")]
            connectionHasProblems = true
        } catch {
            connectionReport = ["✗ " + error.localizedDescription]
            connectionHasProblems = true
        }
    }

    private func client(_ token: ResolvedToken) -> GitHubClient {
        GitHubClient(token: token.value, host: GitHubHost(token.host))
    }

    func refresh() async {
        if isLoading {
            refreshPending = true
            return
        }
        isLoading = true
        defer {
            isLoading = false
            if refreshPending {
                refreshPending = false
                Task { await refresh() }
            }
        }

        guard config.hasSearches else {
            allPullRequests = []
            lastError = nil
            return
        }
        guard let token = await currentToken() else {
            lastError = L("Brak tokenu GitHub. Zaloguj się przez `gh auth login` albo dodaj PAT (scope `repo`) - szczegóły w Ustawieniach → Token.", "No GitHub token. Sign in with `gh auth login` or add a PAT (`repo` scope) - see Settings → Token.")
            return
        }
        do {
            let outcome = try await client(token)
                .searchPullRequests(config: config)
            // Results from a server the user has switched away from are dropped; the queued refresh loads the new one.
            guard token.host == config.gitHubHost.name else { return }
            let now = Date()
            let wokeFromSnooze = outcome.pullRequests.filter {
                snoozedPullRequests[$0.id].map { $0 <= now } == true
            }
            for pr in wokeFromSnooze { snoozedPullRequests[pr.id] = nil }
            if config.autoDismissReviewed, let viewer = outcome.viewerLogin {
                for pr in outcome.pullRequests {
                    if let reviewedAt = pr.viewerReviewAt(viewer), dismissals[pr.id] == nil {
                        let snapshot = DismissalSnapshot(dismissedAt: reviewedAt, headCommitOID: pr.headCommitOID)
                        guard !pr.hasNewActivity(since: snapshot, viewer: viewer) else { continue }
                        snoozedPullRequests[pr.id] = nil
                        dismissals[pr.id] = snapshot
                    }
                }
            }
            let reactivated = reactivateDismissed(in: outcome.pullRequests, viewer: outcome.viewerLogin)
            inbox.observe(outcome.pullRequests, viewer: outcome.viewerLogin, baseline: suppressNextNotifications)
            handleNotifications(for: outcome.pullRequests, reactivated: reactivated,
                                wokeFromSnooze: wokeFromSnooze, viewer: outcome.viewerLogin)
            allPullRequests = outcome.pullRequests
            ssoBlockedOrgCount = outcome.ssoBlockedOrgIDs.count
            lastError = nil
            lastRefresh = Date()
        } catch GitHubError.unauthorized {
            self.token = nil
            lastError = GitHubError.unauthorized.errorDescription
        } catch {
            lastError = error.localizedDescription
        }
    }

    /// Returns an error message, or `nil` when the user was added.
    func addUser(_ raw: String) async -> String? {
        let login = SearchQueryBuilder.normalizeLogin(raw)
        guard !login.isEmpty else { return L("Podaj login.", "Enter a login.") }
        guard SearchQueryBuilder.isValidLogin(login) else {
            return L("Nieprawidłowy login GitHub.", "Invalid GitHub login.")
        }
        if config.users.contains(where: { $0.caseInsensitiveCompare(login) == .orderedSame }) {
            return L("\(login) jest już obserwowany.", "\(login) is already watched.")
        }
        guard let token = await currentToken() else { return L("Brak tokenu GitHub.", "No GitHub token.") }
        do {
            guard let canonical = try await client(token).canonicalLogin(for: login) else {
                return L("Nie znaleziono użytkownika \(login).", "User \(login) not found.")
            }
            if !config.users.contains(where: { $0.caseInsensitiveCompare(canonical) == .orderedSame }) {
                config.users.append(canonical)
            }
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    func removeUser(_ login: String) {
        config.users.removeAll { $0 == login }
    }

    func setPersonalAccessToken(_ value: String?) {
        if let value, !value.trimmingCharacters(in: .whitespaces).isEmpty {
            Keychain.savePAT(value.trimmingCharacters(in: .whitespacesAndNewlines), host: config.gitHubHost.name)
        } else {
            Keychain.deletePAT(host: config.gitHubHost.name)
        }
        token = nil
        tokenSource = nil
        connectionReport = []
        suppressNextNotifications = true
        Task { await refresh() }
    }

    private func currentToken() async -> ResolvedToken? {
        // A token is cached and used only for the host it was resolved for. If the host changes while resolving,
        // the result is dropped and the token for the new host is resolved instead.
        while true {
            let host = config.gitHubHost.name
            if let token, token.host == host { return token }
            let resolved = await Task.detached { TokenProvider.resolve(host: host) }.value
            guard config.gitHubHost.name == host else { continue }
            token = resolved
            tokenSource = resolved?.source
            return resolved
        }
    }

    /// Un-dismisses PRs with new activity and returns them with a description of what changed.
    private func reactivateDismissed(in prs: [PullRequest], viewer: String?) -> [PullRequestAlert] {
        var updated = dismissals
        var reactivated: [PullRequestAlert] = []
        for pr in prs {
            guard let snapshot = updated[pr.id] else { continue }
            if pr.hasNewActivity(since: snapshot, viewer: viewer) {
                updated[pr.id] = nil
                let changes = pr.changes(since: snapshot, viewer: viewer)
                reactivated.append(PullRequestAlert(pullRequest: pr, details: changes.isEmpty ? [AlertEvent.activity.line(L("Nowa aktywność", "New activity"))] : changes))
            } else if snapshot.headCommitOID == nil, let oid = pr.headCommitOID {
                // Dismissals migrated from the ID-only format get their commit baseline on the first refresh.
                updated[pr.id]?.headCommitOID = oid
            }
        }
        if updated != dismissals { dismissals = updated }
        return reactivated
    }

    private func handleNotifications(for prs: [PullRequest], reactivated: [PullRequestAlert],
                                     wokeFromSnooze: [PullRequest], viewer: String?) {
        let ids = Set(prs.map(\.id))
        let seen = seenIDs
        var alerts: [PullRequestAlert] = []
        if config.notify, !suppressNextNotifications, let seen {
            let fresh = prs.filter { !seen.contains($0.id) && dismissals[$0.id] == nil && !isSnoozed($0) }
            let returned = reactivated.filter { seen.contains($0.pullRequest.id) }
            let woke = wokeFromSnooze.filter { seen.contains($0.id) && dismissals[$0.id] == nil }
            alerts = fresh.map(PullRequestAlert.new) + returned
                + woke.map { PullRequestAlert(pullRequest: $0, details: [AlertEvent.snoozeEnded.line(L("Koniec odłożenia", "Snooze ended"))]) }
            let handledIDs = Set(alerts.map { $0.pullRequest.id })
            for pr in prs where seen.contains(pr.id) && !handledIDs.contains(pr.id)
                && dismissals[pr.id] == nil && !isSnoozed(pr) {
                guard let snapshot = notificationSnapshots[pr.id] else { continue }
                let details = pr.newActivity(since: snapshot, viewer: viewer)
                if !details.isEmpty { alerts.append(PullRequestAlert(pullRequest: pr, details: details)) }
            }
        }
        notificationSnapshots = Dictionary(uniqueKeysWithValues: prs.map {
            ($0.id, $0.notificationSnapshot(preserving: notificationSnapshots[$0.id]))
        })
        seenIDs = ids
        suppressNextNotifications = false
        notifier.notify(about: config.notifications.filter(alerts))
    }

    private static func loadDismissals(from preferences: UserDefaults) -> [String: DismissalSnapshot] {
        if let data = preferences.data(forKey: dismissalsKey),
           let decoded = try? JSONDecoder().decode([String: DismissalSnapshot].self, from: data) {
            return decoded
        }
        let legacy = preferences.stringArray(forKey: legacyDismissedIDsKey) ?? []
        preferences.removeObject(forKey: legacyDismissedIDsKey)
        let now = Date()
        let migrated = Dictionary(uniqueKeysWithValues: Set(legacy).map { ($0, DismissalSnapshot(dismissedAt: now, headCommitOID: nil)) })
        if !migrated.isEmpty { preferences.set(try? JSONEncoder().encode(migrated), forKey: dismissalsKey) }
        return migrated
    }

    private static func loadNotificationSnapshots(from preferences: UserDefaults) -> [String: NotificationSnapshot] {
        guard let data = preferences.data(forKey: notificationSnapshotsKey) else { return [:] }
        do {
            return try JSONDecoder().decode([String: NotificationSnapshot].self, from: data)
        } catch {
            NSLog("GHNotif: nie udało się odczytać stanu powiadomień: %@", error.localizedDescription)
            return [:]
        }
    }

    private func configDidChange(from old: AppConfig) {
        guard config != old else { return }
        do {
            try store.save(config)
        } catch {
            lastError = L("Nie udało się zapisać konfiguracji: \(error.localizedDescription)", "Could not save the configuration: \(error.localizedDescription)")
        }
        if config.notify && !old.notify { notifier.requestAuthorization() }
        if config.host != old.host {
            token = nil
            tokenSource = nil
            connectionReport = []
        }
        if config.users != old.users || config.extraQualifiers != old.extraQualifiers
            || config.includeMentions != old.includeMentions || config.includeReviewRequests != old.includeReviewRequests
            || config.includeTeamReviewRequests != old.includeTeamReviewRequests
            || config.includeDrafts != old.includeDrafts || config.hideApproved != old.hideApproved
            || config.host != old.host
            || config.excludedRepositories != old.excludedRepositories || config.excludedLabels != old.excludedLabels
            || config.autoDismissReviewed != old.autoDismissReviewed {
            suppressNextNotifications = true
            Task { await refresh() }
        }
        if config.refreshMinutes != old.refreshMinutes { startRefreshLoop() }
        if config.reviewReminder != old.reviewReminder {
            // Changed schedules apply from now on; times already past today don't fire immediately.
            reminderState = ReviewReminderState(lastHandled: Date())
        }
    }

    private struct WatchlistDocument: Codable {
        let users: [String]
        let formatVersion: Int

        init(users: [String]) {
            self.users = users
            formatVersion = 1
        }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            users = try container.decode([String].self, forKey: .users)
            formatVersion = try container.decodeIfPresent(Int.self, forKey: .formatVersion) ?? 1
            guard formatVersion == 1 else {
                throw DecodingError.dataCorruptedError(forKey: .formatVersion, in: container,
                    debugDescription: L("Nieobsługiwana wersja pliku listy osób.", "Unsupported people list file version."))
            }
        }
    }

    private func startRefreshLoop() {
        refreshLoop?.cancel()
        refreshLoop = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.refresh()
                let minutes = max(1, self.config.refreshMinutes)
                try? await Task.sleep(for: .seconds(minutes * 60))
            }
        }
    }
}
