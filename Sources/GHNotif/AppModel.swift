import Foundation
import GHNotifCore

@MainActor
final class AppModel: ObservableObject {
    @Published var config: AppConfig {
        didSet { configDidChange(from: oldValue) }
    }

    /// Every PR returned by the last search, including ones the user dismissed.
    @Published private(set) var allPullRequests: [PullRequest] = []
    /// Dismissed PRs stay hidden across refreshes until the user restores them.
    @Published private(set) var dismissedIDs: Set<String> {
        didSet { preferences.set(Array(dismissedIDs), forKey: Self.dismissedIDsKey) }
    }
    @Published private(set) var isLoading = false
    @Published private(set) var lastError: String?
    @Published private(set) var lastRefresh: Date?
    @Published private(set) var ssoBlockedOrgCount = 0
    @Published private(set) var tokenSource: TokenSource?

    private let store: ConfigStore
    private let preferences: UserDefaults
    private let notifier = Notifier()
    private var token: ResolvedToken?
    private var refreshLoop: Task<Void, Never>?
    private var refreshPending = false
    /// After the watched set changes, the next result is treated as a baseline so existing PRs don't trigger alerts.
    private var suppressNextNotifications = false

    private static let dismissedIDsKey = "dismissedPullRequestIDs"
    private static let seenIDsKey = "seenPullRequestIDs"

    var pullRequests: [PullRequest] { allPullRequests.filter { !dismissedIDs.contains($0.id) } }
    var dismissedPullRequests: [PullRequest] { allPullRequests.filter { dismissedIDs.contains($0.id) } }

    func dismiss(_ pr: PullRequest) { dismissedIDs.insert(pr.id) }
    func restore(_ pr: PullRequest) { dismissedIDs.remove(pr.id) }
    private var seenIDs: Set<String>? {
        get { preferences.stringArray(forKey: Self.seenIDsKey).map(Set.init) }
        set { preferences.set(newValue.map(Array.init), forKey: Self.seenIDsKey) }
    }

    init(store: ConfigStore = ConfigStore(), preferences: UserDefaults = .standard) {
        self.store = store
        self.preferences = preferences
        dismissedIDs = Set(preferences.stringArray(forKey: Self.dismissedIDsKey) ?? [])
        do {
            config = try store.load()
        } catch {
            config = .default
            lastError = "Nie udało się wczytać \(store.fileURL.path): \(error.localizedDescription). Używam domyślnej listy (plik nie zostanie nadpisany do pierwszej zmiany)."
        }
        if config.notify { notifier.requestAuthorization() }
        startRefreshLoop()
    }

    var ssoSettingsURL: URL {
        tokenSource == .ghCLI
            ? URL(string: "https://github.com/settings/connections/applications/178c6fc778ccc68e1d6a")!
            : URL(string: "https://github.com/settings/tokens")!
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

        guard !config.users.isEmpty else {
            allPullRequests = []
            lastError = nil
            return
        }
        guard let token = await currentToken() else {
            lastError = "Brak tokenu GitHub. Zaloguj się przez `gh auth login` albo dodaj PAT w ustawieniach."
            return
        }
        do {
            let outcome = try await GitHubClient(token: token.value)
                .searchPullRequests(authors: config.users, extraQualifiers: config.extraQualifiers)
            handleNotifications(for: outcome.pullRequests)
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
        guard !login.isEmpty else { return "Podaj login." }
        if config.users.contains(where: { $0.caseInsensitiveCompare(login) == .orderedSame }) {
            return "\(login) jest już obserwowany."
        }
        guard let token = await currentToken() else { return "Brak tokenu GitHub." }
        do {
            guard let canonical = try await GitHubClient(token: token.value).canonicalLogin(for: login) else {
                return "Nie znaleziono użytkownika \(login)."
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
            Keychain.savePAT(value.trimmingCharacters(in: .whitespacesAndNewlines))
        } else {
            Keychain.deletePAT()
        }
        token = nil
        tokenSource = nil
        suppressNextNotifications = true
        Task { await refresh() }
    }

    private func currentToken() async -> ResolvedToken? {
        if let token { return token }
        let resolved = await Task.detached { TokenProvider.resolve() }.value
        token = resolved
        tokenSource = resolved?.source
        return resolved
    }

    private func handleNotifications(for prs: [PullRequest]) {
        let ids = Set(prs.map(\.id))
        defer {
            seenIDs = ids
            suppressNextNotifications = false
        }
        guard config.notify, !suppressNextNotifications, let seen = seenIDs else { return }
        notifier.notify(about: prs.filter { !seen.contains($0.id) && !dismissedIDs.contains($0.id) })
    }

    private func configDidChange(from old: AppConfig) {
        guard config != old else { return }
        do {
            try store.save(config)
        } catch {
            lastError = "Nie udało się zapisać konfiguracji: \(error.localizedDescription)"
        }
        if config.notify && !old.notify { notifier.requestAuthorization() }
        if config.users != old.users || config.extraQualifiers != old.extraQualifiers {
            suppressNextNotifications = true
            Task { await refresh() }
        }
        if config.refreshMinutes != old.refreshMinutes { startRefreshLoop() }
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
