import SwiftUI

@main
struct GHNotifApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(model)
        } label: {
            HStack(spacing: 2) {
                Image(systemName: model.lastError == nil ? "arrow.triangle.pull" : "exclamationmark.triangle")
                if !model.pullRequests.isEmpty {
                    Text("\(model.pullRequests.count)")
                }
            }
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
