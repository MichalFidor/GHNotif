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
                if model.lastError == nil {
                    Image("TrayMark", bundle: .module).renderingMode(.template)
                } else {
                    Image(systemName: "exclamationmark.triangle")
                }
                if !model.pullRequests.isEmpty {
                    Text("\(model.pullRequests.count)")
                }
            }
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
