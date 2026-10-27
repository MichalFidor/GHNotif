import AppKit
import GHNotifCore
import UserNotifications

final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    /// UNUserNotificationCenter crashes outside an app bundle (e.g. `swift run`), so notifications are disabled there.
    private let center: UNUserNotificationCenter? =
        Bundle.main.bundleIdentifier != nil ? UNUserNotificationCenter.current() : nil

    override init() {
        super.init()
        center?.delegate = self
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
        let content = UNMutableNotificationContent()
        content.title = L("GHNotif — test powiadomień", "GHNotif — test notification")
        content.body = L("Tak wygląda alert o nowym PR-ze lub nowej aktywności.", "This is how an alert about a new PR or new activity looks.")
        content.sound = .default
        try await center.add(UNNotificationRequest(
            identifier: "ghnotif-test", content: content,
            trigger: UNTimeIntervalNotificationTrigger(timeInterval: 5, repeats: false)
        ))
        let settings = await center.notificationSettings()
        let alerts = settings.alertSetting == .enabled
        return L("System przyjął powiadomienie (za 5 sekund). Alerty: \(alerts ? "włączone" : "wyłączone"). Jeśli banner się nie pojawił, sprawdź tryb skupienia i ustawienia powiadomień GHNotif.",
                 "The system accepted the notification (in 5 seconds). Alerts: \(alerts ? "on" : "off"). If no banner appears, check Focus mode and GHNotif notification settings.")
    }

    func diagnostics() async -> String {
        guard let center else { return L("Brak kontekstu aplikacji.", "No app context.") }
        let settings = await center.notificationSettings()
        let delivered = await center.deliveredNotifications()
        let testDelivered = delivered.contains { $0.request.identifier == "ghnotif-test" }
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
            enqueue(UNNotificationRequest(identifier: pr.id, content: content, trigger: nil))
        }
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
        if let string = response.notification.request.content.userInfo["url"] as? String, let url = URL(string: string) {
            NSWorkspace.shared.open(url)
        }
        completionHandler()
    }
}
