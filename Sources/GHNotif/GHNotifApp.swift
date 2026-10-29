import SwiftUI
import GHNotifCore

@main
struct GHNotifApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(model)
        } label: {
            Image(nsImage: MenuBarIcon.image(count: model.pullRequests.count, hasError: model.lastError != nil))
                .help(model.lastError == nil ? L("GHNotif - PR-y wymagające uwagi", "GHNotif - PRs needing attention")
                      : L("GHNotif - błąd pobierania PR-ów", "GHNotif - failed to fetch PRs"))
        }
        .menuBarExtraStyle(.window)

        Window(L("GHNotif – ustawienia", "GHNotif – Settings"), id: SettingsView.windowID) {
            SettingsView()
                .environmentObject(model)
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)
    }
}
