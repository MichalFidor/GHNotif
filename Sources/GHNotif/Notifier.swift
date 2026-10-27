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
        center?.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func notify(about newPRs: [PullRequest]) {
        guard let center, !newPRs.isEmpty else { return }
        if newPRs.count > 3 {
            let content = UNMutableNotificationContent()
            content.title = "\(newPRs.count) nowych PR-ów do review"
            content.body = Set(newPRs.map(\.author)).sorted().joined(separator: ", ")
            content.sound = .default
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
            return
        }
        for pr in newPRs {
            let content = UNMutableNotificationContent()
            content.title = "\(pr.repository) #\(pr.number)"
            content.subtitle = pr.author
            content.body = pr.title
            content.sound = .default
            content.userInfo = ["url": pr.url.absoluteString]
            center.add(UNNotificationRequest(identifier: pr.id, content: content, trigger: nil))
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
