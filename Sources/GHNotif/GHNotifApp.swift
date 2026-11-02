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
            MenuBarLabel(model: model)
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

private struct MenuBarLabel: View {
    @ObservedObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        let icon = MenuBarIcon.image(
            count: model.pullRequests.count, hasError: model.lastError != nil,
            isReady: model.lastRefresh != nil && model.config.hasSearches && model.ssoBlockedOrgCount == 0
        )
        Image(nsImage: icon)
            .help(icon.accessibilityDescription ?? "GHNotif")
            .task {
                // The label is the only view rendered at launch, so first-run setup opens from here.
                guard model.isFirstLaunch else { return }
                NSApp.activate()
                openWindow(id: SettingsView.windowID)
            }
    }
}
