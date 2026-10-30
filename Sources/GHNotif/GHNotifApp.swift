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
            let icon = MenuBarIcon.image(
                count: model.pullRequests.count, hasError: model.lastError != nil,
                isReady: model.lastRefresh != nil && model.config.hasSearches && model.ssoBlockedOrgCount == 0
            )
            Image(nsImage: icon)
                .help(icon.accessibilityDescription ?? "GHNotif")
        }
        .menuBarExtraStyle(.window)

        Window(L("GHNotif - ustawienia", "GHNotif - Settings"), id: SettingsView.windowID) {
            SettingsView()
                .environmentObject(model)
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)
    }
}
