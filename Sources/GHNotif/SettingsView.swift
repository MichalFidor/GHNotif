import GHNotifCore
import SwiftUI

struct SettingsView: View {
    static let windowID = "settings"

    @EnvironmentObject private var model: AppModel
    // `@State` is a macro whose plugin ships only with full Xcode; `@StateObject` builds with Command Line Tools.
    @StateObject private var form = FormState()

    private var sortedUsers: [String] {
        model.config.users.sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    private var filteredUsers: [String] {
        let query = form.filter.trimmingCharacters(in: .whitespaces)
        return query.isEmpty ? sortedUsers : sortedUsers.filter { $0.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        NavigationSplitView {
            List(SettingsPane.allCases, selection: $form.pane) { pane in
                SidebarLabel(pane: pane).tag(pane)
            }
            .frame(minWidth: 190)
            .navigationSplitViewColumnWidth(190)
            .toolbar(removing: .sidebarToggle)
        } detail: {
            detail(for: form.pane ?? .people)
                .navigationTitle((form.pane ?? .people).title)
        }
        .frame(width: 680, height: 480)
        .onAppear { form.extraQualifiers = model.config.extraQualifiers }
    }

    @ViewBuilder
    private func detail(for pane: SettingsPane) -> some View {
        switch pane {
        case .people: peopleTab
        case .search: searchTab
        case .general: generalTab
        case .token: tokenTab
        }
    }

    private var peopleTab: some View {
        Form {
            Section {
                HStack {
                    TextField("Login GitHub", text: $form.newLogin, prompt: Text("Login GitHub, np. octocat"))
                        .labelsHidden()
                        .onSubmit(addUser)
                    if form.isAdding { ProgressView().controlSize(.small) }
                    Button("Dodaj", action: addUser)
                        .disabled(form.isAdding || form.newLogin.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if let addError = form.addError {
                    Text(addError).font(.caption).foregroundStyle(.red)
                }
            } header: {
                Text("Dodaj osobę")
            } footer: {
                Text("Pokazywane są otwarte PR-y (bez draftów) tych osób, w dowolnym repozytorium widocznym dla tokenu.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                DisclosureGroup(isExpanded: $form.showPeople) {
                    if model.config.users.count > 6 {
                        TextField("Filtruj", text: $form.filter, prompt: Text("Szukaj na liście"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                    }
                    ScrollView {
                        VStack(spacing: 0) {
                            ForEach(filteredUsers, id: \.self) { login in
                                personRow(login)
                                Divider()
                            }
                            if filteredUsers.isEmpty {
                                Text(model.config.users.isEmpty ? "Nikogo jeszcze nie obserwujesz." : "Brak wyników.")
                                    .foregroundStyle(.secondary)
                                    .padding(.vertical, 6)
                            }
                        }
                    }
                    .frame(height: min(CGFloat(max(filteredUsers.count, 1)) * 29, 190))
                } label: {
                    HStack {
                        Text("Obserwowane osoby")
                        Spacer()
                        Text(String(model.config.users.count)).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private func personRow(_ login: String) -> some View {
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
        .padding(.vertical, 5)
        .padding(.horizontal, 2)
    }

    private var searchTab: some View {
        Form {
            Section {
                Toggle("PR-y, w których ktoś mnie wspomniał (@)", isOn: $model.config.includeMentions)
                Toggle("PR-y z prośbą o moje review", isOn: $model.config.includeReviewRequests)
            } header: {
                Text("Dodatkowo pokazuj")
            } footer: {
                Text("Niezależnie od autora – wzmianki obejmują też Twoje PR-y. Prośby o review obejmują tylko te skierowane do Ciebie osobiście, nie do zespołu – mimo to mogą mocno wydłużyć listę, dlatego są domyślnie wyłączone.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                TextField("Dodatkowe kwalifikatory", text: $form.extraQualifiers, prompt: Text("Kwalifikatory, np. org:my-org"))
                    .labelsHidden()
                    .onSubmit { model.config.extraQualifiers = form.extraQualifiers }
            } header: {
                Text("Zakres")
            } footer: {
                Text("Puste = wszystkie repozytoria widoczne dla tokenu. Zatwierdź Enterem. Organizacje z SAML SSO wymagają autoryzacji tokenu – aplikacja pokaże ostrzeżenie, jeśli wyniki są ukryte.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private var generalTab: some View {
        Form {
            Section("Odświeżanie") {
                Stepper("Co \(model.config.refreshMinutes) min", value: $model.config.refreshMinutes, in: 1...60)
            }

            Section("Powiadomienia") {
                Toggle("Powiadamiaj o nowych PR-ach i nowej aktywności", isOn: $model.config.notify)
                HStack {
                    Button("Wyślij testowe") {
                        Task { await model.testNotification() }
                    }
                    Spacer()
                    Link("Ustawienia systemowe",
                         destination: URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension")!)
                }
                if !model.notificationStatus.isEmpty {
                    Text(model.notificationStatus).font(.caption)
                }
            }

            Section("GHNotif \(model.installedVersion)") {
                HStack {
                    Button("Sprawdź aktualizacje") {
                        Task { await model.checkForUpdates() }
                    }
                    .disabled(model.checkingUpdates)
                    Spacer()
                    if let release = model.availableRelease {
                        Link("Pobierz \(release.tagName)", destination: release.htmlURL)
                    }
                }
                if !model.updateStatus.isEmpty { Text(model.updateStatus).font(.caption) }
            }
        }
        .formStyle(.grouped)
    }

    private var tokenTab: some View {
        Form {
            Section {
                LabeledContent("Aktualne źródło", value: model.tokenSource?.rawValue ?? "brak")
            }
            Section {
                SecureField("Personal access token", text: $form.pat, prompt: Text("ghp_… (opcjonalnie)"))
                HStack {
                    Button("Zapisz PAT") {
                        model.setPersonalAccessToken(form.pat)
                        form.pat = ""
                    }
                    .disabled(form.pat.trimmingCharacters(in: .whitespaces).isEmpty)
                    Button("Usuń PAT i użyj gh CLI") { model.setPersonalAccessToken(nil) }
                }
            } footer: {
                Text("Domyślnie używany jest token z `gh auth login`. PAT (classic: scope `repo`; fine-grained: Pull requests – read) ma pierwszeństwo i jest przechowywany w Keychain.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
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

enum SettingsPane: String, CaseIterable, Identifiable {
    case people, search, general, token

    var id: Self { self }

    var title: String {
        switch self {
        case .people: "Osoby"
        case .search: "Wyszukiwanie"
        case .general: "Ogólne"
        case .token: "Token"
        }
    }

    var systemImage: String {
        switch self {
        case .people: "person.2.fill"
        case .search: "magnifyingglass"
        case .general: "gearshape.fill"
        case .token: "key.fill"
        }
    }

    var tint: Color {
        switch self {
        case .people: .blue
        case .search: .purple
        case .general: .gray
        case .token: .orange
        }
    }
}

/// System Settings-style row: white symbol on a coloured rounded square.
struct SidebarLabel: View {
    let pane: SettingsPane

    var body: some View {
        Label {
            Text(pane.title)
        } icon: {
            Image(systemName: pane.systemImage)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 20, height: 20)
                .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(pane.tint.gradient))
        }
    }
}

private final class FormState: ObservableObject {
    @Published var pane: SettingsPane? = .people
    @Published var newLogin = ""
    @Published var isAdding = false
    @Published var addError: String?
    @Published var extraQualifiers = ""
    @Published var pat = ""
    @Published var filter = ""
    @Published var showPeople = false
}
