import AppKit
import GHNotifCore
import UserNotifications

final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    private static let updateCategory = "GHNOTIF_UPDATE"
    private static let updateNowAction = "GHNOTIF_UPDATE_NOW"
    var onUpdateNow: (() -> Void)?
    var onPullRequestOpened: ((URL) -> Void)?

    /// UNUserNotificationCenter crashes outside an app bundle (e.g. `swift run`), so notifications are disabled there.
    private let center: UNUserNotificationCenter? =
        Bundle.main.bundleIdentifier != nil ? UNUserNotificationCenter.current() : nil

    override init() {
        super.init()
        center?.delegate = self
        let updateNow = UNNotificationAction(
            identifier: Self.updateNowAction, title: L("Zaktualizuj teraz", "Update now"), options: [.foreground]
        )
        let later = UNNotificationAction(
            identifier: "GHNOTIF_UPDATE_LATER", title: L("Później", "Later"), options: []
        )
        center?.setNotificationCategories([
            UNNotificationCategory(identifier: Self.updateCategory, actions: [updateNow, later], intentIdentifiers: [])
        ])
    }

    func requestAuthorization() {
        center?.requestAuthorization(options: [.alert, .sound]) { granted, error in
            if let error { NSLog("GHNotif: błąd uprawnień powiadomień: %@", error.localizedDescription) }
            else if !granted { NSLog("GHNotif: użytkownik nie zezwolił na powiadomienia.") }
        }
    }

    func sendTest() async throws -> String {
        guard let center else {
            throw NSError(domain: "GHNotif", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: L("Powiadomienia wymagają uruchomienia z GHNotif.app.", "Notifications require running GHNotif.app.")])
        }
        let granted = try await center.requestAuthorization(options: [.alert, .sound])
        guard granted else {
            return L("Brak zgody. Włącz GHNotif w Ustawienia systemowe → Powiadomienia.", "Permission denied. Enable GHNotif in System Settings → Notifications.")
        }
        let samples: [[String]] = [
            [AlertEvent.approved.line(L("octocat - zatwierdzenie", "octocat - approved")),
             AlertEvent.comment.line(L("hubot - komentarz", "hubot - comment"))],
            [AlertEvent.changesRequested.line(L("monalisa - prośba o zmiany", "monalisa - changes requested")),
             AlertEvent.commits.line(L("octocat - nowe commity", "octocat - new commits"))],
            [AlertEvent.reviewRequested.line(L("Poproszono Cię o review", "Your review was requested"))],
            [AlertEvent.mention.line(L("Wspomniano Cię w PR-ze", "You were mentioned"))]
        ]
        for (index, lines) in samples.enumerated() {
            let content = UNMutableNotificationContent()
            content.title = L("GHNotif - test powiadomień", "GHNotif - test notification")
            content.subtitle = "octo/repo #\(42 + index)"
            content.body = summarizeChanges(lines)
            content.sound = index == 0 ? .default : nil
            if let icon = NotificationIcon.attachment(for: AlertEvent.primary(in: lines)) { content.attachments = [icon] }
            try await center.add(UNNotificationRequest(
                identifier: "ghnotif-test-\(index)", content: content,
                trigger: UNTimeIntervalNotificationTrigger(timeInterval: 5 + Double(index) * 2, repeats: false)
            ))
        }
        let settings = await center.notificationSettings()
        let alerts = settings.alertSetting == .enabled
        return L("System przyjął powiadomienie (za 5 sekund). Alerty: \(alerts ? "włączone" : "wyłączone"). Jeśli banner się nie pojawił, sprawdź tryb skupienia i ustawienia powiadomień GHNotif.",
                 "The system accepted the notification (in 5 seconds). Alerts: \(alerts ? "on" : "off"). If no banner appears, check Focus mode and GHNotif notification settings.")
    }

    func diagnostics() async -> String {
        guard let center else { return L("Brak kontekstu aplikacji.", "No app context.") }
        let settings = await center.notificationSettings()
        let delivered = await center.deliveredNotifications()
        let testDelivered = delivered.contains { $0.request.identifier.hasPrefix("ghnotif-test") }
        return L("Zgoda: \(settings.authorizationStatus.rawValue), styl alertów: \(settings.alertStyle.rawValue), Centrum powiadomień: \(settings.notificationCenterSetting.rawValue), test w Centrum: \(testDelivered ? "tak" : "nie").",
                 "Permission: \(settings.authorizationStatus.rawValue), alert style: \(settings.alertStyle.rawValue), Notification Center: \(settings.notificationCenterSetting.rawValue), test in Center: \(testDelivered ? "yes" : "no").")
    }

    func notify(about alerts: [PullRequestAlert]) {
        guard center != nil, !alerts.isEmpty else { return }
        if alerts.count > 3 {
            let content = UNMutableNotificationContent()
            content.title = L("\(alerts.count) PR-ów wymaga uwagi", "\(alerts.count) PRs need attention")
            content.body = summarizeChanges(alerts.map {
                "#\($0.pullRequest.number): \($0.details.first ?? $0.pullRequest.title)"
            })
            content.sound = .default
            if let icon = NotificationIcon.attachment(for: AlertEvent.primary(in: alerts.flatMap(\.details))) {
                content.attachments = [icon]
            }

            enqueue(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
            return
        }
        for alert in alerts {
            let pr = alert.pullRequest
            let content = UNMutableNotificationContent()
            content.title = "\(pr.repository) #\(pr.number)"
            content.subtitle = pr.title
            content.body = summarizeChanges(alert.details)
            content.sound = .default
            content.userInfo = ["url": pr.url.absoluteString]
            content.threadIdentifier = pr.id
            if let icon = NotificationIcon.attachment(for: alert.event) { content.attachments = [icon] }
            enqueue(UNNotificationRequest(identifier: pr.id, content: content, trigger: nil))
        }
    }

    func notifyUpdateAvailable(_ release: GitHubRelease) async throws -> Bool {
        guard let center else { return false }
        let settings = await center.notificationSettings()
        guard settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional else { return false }
        let content = UNMutableNotificationContent()
        content.title = L("Dostępna aktualizacja GHNotif", "GHNotif update available")
        content.body = L("Wersja \(release.tagName) jest gotowa do instalacji.", "Version \(release.tagName) is ready to install.")
        content.categoryIdentifier = Self.updateCategory
        content.sound = .default
        try await center.add(UNNotificationRequest(
            identifier: "ghnotif-update-\(release.tagName)", content: content, trigger: nil
        ))
        return true
    }

    private func enqueue(_ request: UNNotificationRequest) {
        center?.add(request) { error in
            if let error { NSLog("GHNotif: nie udało się wysłać powiadomienia: %@", error.localizedDescription) }
        }
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .sound])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        if response.actionIdentifier == Self.updateNowAction {
            Task { @MainActor in onUpdateNow?() }
            completionHandler()
            return
        }
        if let string = response.notification.request.content.userInfo["url"] as? String, let url = URL(string: string) {
            Task { @MainActor in
                if NSWorkspace.shared.open(url) {
                    onPullRequestOpened?(url)
                } else {
                    NSLog("GHNotif: nie udało się otworzyć adresu powiadomienia: %@", url.absoluteString)
                }
            }
        }
        completionHandler()
    }
}
