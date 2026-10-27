import SwiftUI

@main
struct GHNotifApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(model)
        } label: {
            Image(nsImage: MenuBarIcon.image(count: model.pullRequests.count, hasError: model.lastError != nil))
                .help(model.lastError == nil ? "GHNotif — PR-y wymagające uwagi" : "GHNotif — błąd pobierania PR-ów")
        }
        .menuBarExtraStyle(.window)

        Window("GHNotif – ustawienia", id: SettingsView.windowID) {
            SettingsView()
                .environmentObject(model)
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)
    }
}
