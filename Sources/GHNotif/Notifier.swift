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
                          userInfo: [NSLocalizedDescriptionKey: "Powiadomienia wymagają uruchomienia z GHNotif.app."])
        }
        let granted = try await center.requestAuthorization(options: [.alert, .sound])
        guard granted else {
            return "Brak zgody. Włącz GHNotif w Ustawienia systemowe → Powiadomienia."
        }
        let content = UNMutableNotificationContent()
        content.title = "GHNotif — test powiadomień"
        content.body = "Tak wygląda alert o nowym PR-ze lub nowej aktywności."
        content.sound = .default
        try await center.add(UNNotificationRequest(
            identifier: "ghnotif-test", content: content,
            trigger: UNTimeIntervalNotificationTrigger(timeInterval: 5, repeats: false)
        ))
        let settings = await center.notificationSettings()
        return "System przyjął powiadomienie (za 5 sekund). Alerty: \(settings.alertSetting == .enabled ? "włączone" : "wyłączone"). Jeśli banner się nie pojawił, sprawdź tryb skupienia i ustawienia powiadomień GHNotif."
    }

    func diagnostics() async -> String {
        guard let center else { return "Brak kontekstu aplikacji." }
        let settings = await center.notificationSettings()
        let delivered = await center.deliveredNotifications()
        let testDelivered = delivered.contains { $0.request.identifier == "ghnotif-test" }
        return "Zgoda: \(settings.authorizationStatus.rawValue), styl alertów: \(settings.alertStyle.rawValue), Centrum powiadomień: \(settings.notificationCenterSetting.rawValue), test w Centrum: \(testDelivered ? "tak" : "nie")."
    }

    func notify(about newPRs: [PullRequest]) {
        guard center != nil, !newPRs.isEmpty else { return }
        if newPRs.count > 3 {
            let content = UNMutableNotificationContent()
            content.title = "\(newPRs.count) PR-ów wymaga uwagi"
            content.body = Set(newPRs.map(\.author)).sorted().joined(separator: ", ")
            content.sound = .default
            enqueue(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
            return
        }
        for pr in newPRs {
            let content = UNMutableNotificationContent()
            content.title = "\(pr.repository) #\(pr.number)"
            content.subtitle = pr.author
            content.body = pr.title
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
