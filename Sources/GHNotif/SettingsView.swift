import GHNotifCore
import SwiftUI

struct SettingsView: View {
    static let windowID = "settings"

    @EnvironmentObject private var model: AppModel
    // `@State` is a macro whose plugin ships only with full Xcode; `@StateObject` builds with Command Line Tools.
    @StateObject private var form = FormState()

    private var sortedUsers: [String] {
        model.config.users.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }

    var body: some View {
        Form {
            Section("Obserwowane osoby (\(model.config.users.count))") {
                HStack {
                    TextField("Login GitHub", text: $form.newLogin)
                        .onSubmit(addUser)
                    if form.isAdding { ProgressView().controlSize(.small) }
                    Button("Dodaj", action: addUser)
                        .disabled(form.isAdding || form.newLogin.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if let addError = form.addError {
                    Text(addError).font(.caption).foregroundStyle(.red)
                }
                ForEach(sortedUsers, id: \.self) { login in
                    HStack {
                        Link(login, destination: URL(string: "https://github.com/\(login)")!)
                        Spacer()
                        Button(role: .destructive) {
                            model.removeUser(login)
                        } label: {
                            Image(systemName: "minus.circle.fill").foregroundStyle(.red)
                        }
                        .buttonStyle(.borderless)
                        .help("Przestań obserwować \(login)")
                    }
                }
            }

            Section("Zakres wyszukiwania") {
                TextField("Dodatkowe kwalifikatory", text: $form.extraQualifiers, prompt: Text("np. org:my-org"))
                    .onSubmit { model.config.extraQualifiers = form.extraQualifiers }
                Text("Puste = wszystkie repozytoria widoczne dla tokenu. Zatwierdź Enterem. Organizacje z SAML SSO wymagają autoryzacji tokenu – aplikacja pokaże ostrzeżenie, jeśli wyniki są ukryte.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Odświeżanie i powiadomienia") {
                Stepper("Co \(model.config.refreshMinutes) min", value: $model.config.refreshMinutes, in: 1...60)
                Toggle("Powiadomienia o nowych PR-ach", isOn: $model.config.notify)
                Button("Wyślij testowe powiadomienie") {
                    Task { await model.testNotification() }
                }
                Link("Otwórz systemowe ustawienia powiadomień",
                     destination: URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension")!)
                if !model.notificationStatus.isEmpty {
                    Text(model.notificationStatus).font(.caption)
                }
            }

            Section("GHNotif \(model.installedVersion)") {
                Button("Sprawdź aktualizacje") {
                    Task { await model.checkForUpdates() }
                }
                .disabled(model.checkingUpdates)
                if !model.updateStatus.isEmpty { Text(model.updateStatus).font(.caption) }
                if let release = model.availableRelease {
                    Link("Pobierz \(release.tagName)", destination: release.htmlURL)
                }
            }

            Section("Token GitHub") {
                LabeledContent("Aktualne źródło", value: model.tokenSource?.rawValue ?? "brak")
                SecureField("Personal access token (opcjonalnie)", text: $form.pat)
                HStack {
                    Button("Zapisz PAT") {
                        model.setPersonalAccessToken(form.pat)
                        form.pat = ""
                    }
                    .disabled(form.pat.trimmingCharacters(in: .whitespaces).isEmpty)
                    Button("Usuń PAT i użyj gh CLI") { model.setPersonalAccessToken(nil) }
                }
                Text("Domyślnie używany jest token z `gh auth login`. PAT (classic: scope `repo`; fine-grained: Pull requests – read) ma pierwszeństwo i jest przechowywany w Keychain.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 460, height: 620)
        .onAppear { form.extraQualifiers = model.config.extraQualifiers }
    }

    private func addUser() {
        let login = form.newLogin
        guard !form.isAdding, !login.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        form.isAdding = true
        form.addError = nil
        Task {
            let error = await model.addUser(login)
            form.isAdding = false
            form.addError = error
            if error == nil { form.newLogin = "" }
        }
    }
}

private final class FormState: ObservableObject {
    @Published var newLogin = ""
    @Published var isAdding = false
    @Published var addError: String?
    @Published var extraQualifiers = ""
    @Published var pat = ""
}
