import AppKit
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
                SidebarLabel(pane: pane, language: Localization.language).tag(pane)
            }
            // Rows only take `pane` otherwise, so SwiftUI would keep the old titles after a language switch.
            .id(Localization.language)

            .frame(minWidth: 190)
            .navigationSplitViewColumnWidth(190)
            .toolbar(removing: .sidebarToggle)
        } detail: {
            detail(for: form.pane ?? .people)
                .navigationTitle((form.pane ?? .people).title)
        }
        .frame(width: 680, height: 480)
        .environment(\.locale, Localization.language.locale)
        .onAppear {
            form.extraQualifiers = model.config.extraQualifiers
            form.excludedRepositories = model.config.excludedRepositories.joined(separator: ", ")
            form.excludedLabels = model.config.excludedLabels.joined(separator: ", ")
        }
    }

    @ViewBuilder
    private func detail(for pane: SettingsPane) -> some View {
        switch pane {
        case .people: peopleTab
        case .search: searchTab
        case .reminders: remindersTab
        case .notifications: notificationsTab
        case .general: generalTab
        case .token: tokenTab
        }
    }

    private static var weekdaySymbols: [String] {
        L("Pn Wt Śr Cz Pt So Nd", "Mo Tu We Th Fr Sa Su").split(separator: " ").map(String.init)
    }

    private var remindersTab: some View {
        Form {
            Section(L("Dni", "Days")) {
                HStack(spacing: 6) {
                    ForEach(1...7, id: \.self) { day in
                        Toggle(Self.weekdaySymbols[day - 1], isOn: Binding(
                            get: { model.config.reviewReminder.weekdays.contains(day) },
                            set: { on in
                                if on { model.config.reviewReminder.weekdays.insert(day) }
                                else { model.config.reviewReminder.weekdays.remove(day) }
                            }
                        ))
                        .toggleStyle(.button)
                    }
                }
            }

            Section {
                ForEach(Array(model.config.reviewReminder.alarms.enumerated()), id: \.offset) { index, alarm in
                    HStack {
                        DatePicker(L("Godzina \(index + 1)", "Time \(index + 1)"), selection: Binding(
                            get: { Self.date(for: alarm.time) },
                            set: { setReminderTime($0, at: index) }
                        ), displayedComponents: .hourAndMinute)
                        .labelsHidden()
                        .opacity(alarm.enabled ? 1 : 0.5)
                        Spacer()
                        Toggle(L("Włączone", "On"), isOn: Binding(
                            get: { alarm.enabled },
                            set: { on in
                                guard model.config.reviewReminder.alarms.indices.contains(index) else { return }
                                model.config.reviewReminder.alarms[index].enabled = on
                            }
                        ))
                        .labelsHidden()
                        .toggleStyle(.switch)
                        .help(alarm.enabled ? L("Wyłącz tę godzinę", "Turn this time off") : L("Włącz tę godzinę", "Turn this time on"))
                        Button {
                            model.config.reviewReminder.alarms.remove(at: index)
                        } label: {
                            Image(systemName: "minus.circle.fill").foregroundStyle(.red)
                        }
                        .buttonStyle(.borderless)
                        .help(L("Usuń godzinę", "Remove time"))
                    }
                }
                Button(L("Dodaj godzinę", "Add time"), systemImage: "plus") {
                    let times = model.config.reviewReminder.alarms.map(\.time)
                    let last = times.max()
                    let hour = min((last?.hour ?? 8) + 1, 23)
                    if let time = ReminderTime(hour: hour, minute: last?.minute ?? 0), !times.contains(time) {
                        model.config.reviewReminder.alarms.append(ReminderAlarm(time: time))
                    }
                }
                .buttonStyle(.borderless)
            } header: {
                Text(L("Godziny", "Times"))
            } footer: {
                Text(L("Włączone godziny pokazują okno z listą PR-ów na środku ekranu, nad innymi aplikacjami. Jeśli komputer był uśpiony, przypomnienie pojawi się po wybudzeniu (tego samego dnia).", "Times that are on show a window with the PR list in the center of the screen, above other apps. If the computer was asleep, the reminder appears after wake (same day)."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                LabeledContent(L("Następne przypomnienie", "Next reminder"), value: nextReminderText)
                if let snoozed = model.reviewReminderSnoozedUntil, snoozed > Date() {
                    LabeledContent(L("Odłożone do", "Snoozed until"), value: snoozed.formatted(Date.FormatStyle(date: .omitted, time: .shortened).locale(Localization.language.locale)))
                }
                Button(L("Pokaż teraz", "Show now")) { model.showReviewReminder() }
            }
        }
        .formStyle(.grouped)
    }

    private var nextReminderText: String {
        let reminder = model.config.reviewReminder
        if reminder.alarms.isEmpty { return L("dodaj godzinę", "add a time") }
        guard reminder.enabled else { return L("wyłączone", "off") }
        if reminder.weekdays.isEmpty { return L("wybierz dni", "choose days") }
        return model.nextReviewReminder?.formatted(.dateTime.weekday(.wide).hour().minute().locale(Localization.language.locale)) ?? "-"
    }

    private static func date(for time: ReminderTime) -> Date {
        Calendar.current.date(bySettingHour: time.hour, minute: time.minute, second: 0, of: Date()) ?? Date()
    }

    private func setReminderTime(_ date: Date, at index: Int) {
        let parts = Calendar.current.dateComponents([.hour, .minute], from: date)
        guard let time = ReminderTime(hour: parts.hour ?? 0, minute: parts.minute ?? 0),
              model.config.reviewReminder.alarms.indices.contains(index) else { return }
        model.config.reviewReminder.alarms[index].time = time
    }

    private var peopleTab: some View {
        Form {
            Section {
                HStack {
                    TextField(L("Login GitHub", "GitHub login"), text: $form.newLogin, prompt: Text(L("Login GitHub, np. octocat", "GitHub login, e.g. octocat")))
                        .labelsHidden()
                        .onSubmit(addUser)
                    if form.isAdding { ProgressView().controlSize(.small) }
                    Button(L("Dodaj", "Add"), action: addUser)
                        .disabled(form.isAdding || form.newLogin.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if let addError = form.addError {
                    Text(addError).font(.caption).foregroundStyle(.red)
                }
            } header: {
                Text(L("Dodaj osobę", "Add person"))
            } footer: {
                Text(L("Pokazywane są otwarte PR-y (bez draftów) tych osób, w dowolnym repozytorium widocznym dla tokenu.", "Shows open PRs (no drafts) by these people in any repository visible to the token."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                DisclosureGroup(isExpanded: $form.showPeople) {
                    if model.config.users.count > 6 {
                        TextField(L("Filtruj", "Filter"), text: $form.filter, prompt: Text(L("Szukaj na liście", "Search the list")))
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
                                Text(model.config.users.isEmpty ? L("Nikogo jeszcze nie obserwujesz.", "You aren't watching anyone yet.") : L("Brak wyników.", "No results."))
                                    .foregroundStyle(.secondary)
                                    .padding(.vertical, 6)
                            }
                        }
                    }
                    .frame(height: min(CGFloat(max(filteredUsers.count, 1)) * 29, 190))
                } label: {
                    HStack {
                        Text(L("Obserwowane osoby", "Watched people"))
                        Spacer()
                        Text(String(model.config.users.count)).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
            }

            Section(L("Lista zespołu", "Team list")) {
                TextField(L("Organizacja", "Organization"), text: $form.organization, prompt: Text(L("np. acme", "e.g. acme")))
                TextField(L("Slug zespołu", "Team slug"), text: $form.team, prompt: Text(L("np. qa", "e.g. qa")))
                Button(form.importingTeam ? L("Pobieranie…", "Fetching…") : L("Importuj członków zespołu", "Import team members")) {
                    importTeam()
                }
                .disabled(form.importingTeam || form.organization.isEmpty || form.team.isEmpty)
                Text(L("Wymaga dostępu do członkostwa organizacji: read:org (classic) lub Members: read (fine-grained).", "Requires organization membership access: read:org (classic) or Members: read (fine-grained)."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section(L("Kopia listy", "List backup")) {
                HStack {
                    Button(L("Eksportuj", "Export")) { exportWatchlist() }
                    Button(L("Importuj", "Import")) { importWatchlist() }
                }
                Text(L("Plik zawiera wyłącznie loginy obserwowanych osób - nie zawiera tokenu ani ustawień.", "The file contains only watched logins - no token or settings."))
                    .font(.caption).foregroundStyle(.secondary)
                if let listError = form.listError {
                    Text(listError).font(.caption).foregroundStyle(.red)
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
            .help(L("Przestań obserwować \(login)", "Stop watching \(login)"))
        }
        .padding(.vertical, 5)
        .padding(.horizontal, 2)
    }

    private var searchTab: some View {
        Form {
            Section {
                Toggle(L("PR-y, w których ktoś mnie wspomniał (@)", "PRs where someone mentioned me (@)"), isOn: $model.config.includeMentions)
                Toggle(L("PR-y z prośbą o moje review", "PRs requesting my review"), isOn: $model.config.includeReviewRequests)
            } header: {
                Text(L("Dodatkowo pokazuj", "Also show"))
            } footer: {
                Text(L("Niezależnie od autora - wzmianki obejmują też Twoje PR-y. Prośby o review obejmują tylko te skierowane do Ciebie osobiście, nie do zespołu - mimo to mogą mocno wydłużyć listę, dlatego są domyślnie wyłączone.", "Regardless of author - mentions include your own PRs too. Review requests cover only those sent to you personally, not to a team - they can still make the list much longer, so they're off by default."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                TextField(L("Dodatkowe kwalifikatory", "Extra qualifiers"), text: $form.extraQualifiers, prompt: Text(L("Kwalifikatory, np. org:my-org", "Qualifiers, e.g. org:my-org")))
                    .labelsHidden()
                    .onSubmit { model.config.extraQualifiers = form.extraQualifiers }
            } header: {
                Text(L("Zakres", "Scope"))
            } footer: {
                Text(L("Puste = wszystkie repozytoria widoczne dla tokenu. Zatwierdź Enterem. Organizacje z SAML SSO wymagają autoryzacji tokenu - aplikacja pokaże ostrzeżenie, jeśli wyniki są ukryte.", "Empty = every repository visible to the token. Press Enter to apply. SAML SSO organizations require token authorization - the app warns when results are hidden."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section(L("Filtry wykluczające", "Exclusion filters")) {
                TextField(L("Repozytoria (oddziel przecinkami)", "Repositories (comma separated)"), text: $form.excludedRepositories,
                          prompt: Text(L("np. acme/archive, acme/infra", "e.g. acme/archive, acme/infra")))
                TextField(L("Etykiety (oddziel przecinkami)", "Labels (comma separated)"), text: $form.excludedLabels,
                          prompt: Text(L("np. do-not-review, wip", "e.g. do-not-review, wip")))
                Button(L("Zapisz filtry", "Save filters")) {
                    model.config.excludedRepositories = splitValues(form.excludedRepositories)
                    model.config.excludedLabels = splitValues(form.excludedLabels)
                }
            }
        }
        .formStyle(.grouped)
    }

    private var notificationsTab: some View {
        Form {
            Section {
                Toggle(L("Powiadamiaj o nowych PR-ach i nowej aktywności", "Notify about new PRs and new activity"), isOn: $model.config.notify)
                HStack {
                    Button(L("Wyślij testowe", "Send test")) {
                        Task { await model.testNotification() }
                    }
                    Spacer()
                    Link(L("Ustawienia systemowe", "System Settings"),
                         destination: URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension")!)
                }
                if !model.notificationStatus.isEmpty {
                    Text(model.notificationStatus).font(.caption)
                }
            }

            Section {
                ForEach(AlertEvent.allCases, id: \.self) { event in
                    Toggle(isOn: Binding(
                        get: { !model.config.notifications.mutedEvents.contains(event) },
                        set: { on in
                            if on { model.config.notifications.mutedEvents.remove(event) }
                            else { model.config.notifications.mutedEvents.insert(event) }
                        }
                    )) {
                        HStack(spacing: 8) {
                            Image(nsImage: NotificationIcon.image(for: event))
                                .resizable().frame(width: 18, height: 18)
                            Text(event.title)
                        }
                    }
                }
            } header: {
                Text(L("Zdarzenia", "Events"))
            } footer: {
                Text(L("Wyłączone zdarzenia nie wysyłają powiadomień; PR i tak jest widoczny na liście.", "Events that are off don't send notifications; the PR still shows in the list."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            .disabled(!model.config.notify)

            Section {
                Toggle(L("Ciche godziny", "Quiet hours"), isOn: $model.config.notifications.quietHoursEnabled)
                DatePicker(L("Od", "From"), selection: Binding(
                    get: { Self.date(for: model.config.notifications.quietFrom) },
                    set: { if let time = Self.time(from: $0) { model.config.notifications.quietFrom = time } }
                ), displayedComponents: .hourAndMinute)
                .disabled(!model.config.notifications.quietHoursEnabled)
                DatePicker(L("Do", "To"), selection: Binding(
                    get: { Self.date(for: model.config.notifications.quietTo) },
                    set: { if let time = Self.time(from: $0) { model.config.notifications.quietTo = time } }
                ), displayedComponents: .hourAndMinute)
                .disabled(!model.config.notifications.quietHoursEnabled)
            } footer: {
                Text(L("W tych godzinach GHNotif nie wysyła powiadomień, ale lista PR-ów dalej się odświeża. Zakres może przechodzić przez północ.", "GHNotif sends no notifications during these hours, but the PR list keeps refreshing. The range may span midnight."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            .disabled(!model.config.notify)
        }
        .formStyle(.grouped)
    }

    private static func time(from date: Date) -> ReminderTime? {
        let parts = Calendar.current.dateComponents([.hour, .minute], from: date)
        return ReminderTime(hour: parts.hour ?? 0, minute: parts.minute ?? 0)
    }

    private var generalTab: some View {
        Form {
            Section(L("Język", "Language")) {
                Picker(L("Język aplikacji", "App language"), selection: $model.config.language) {
                    Text(L("Systemowy", "System")).tag(AppLanguage.system)
                    Text("Polski").tag(AppLanguage.polish)
                    Text("English").tag(AppLanguage.english)
                }
            }

            Section(L("Odświeżanie", "Refresh")) {
                Stepper(L("Co \(model.config.refreshMinutes) min", "Every \(model.config.refreshMinutes) min"), value: $model.config.refreshMinutes, in: 1...60)
            }

            Section("Workflow") {
                Toggle(L("Oznaczaj jako załatwione po moim review bieżącego commita", "Mark as done after I review the current commit"),
                       isOn: $model.config.autoDismissReviewed)
                Picker(L("Sortowanie PR-ów", "PR sorting"), selection: $model.config.sortOrder) {
                    Text(L("Ostatnio aktualizowane", "Recently updated")).tag(AppConfig.SortOrder.recentlyUpdated)
                    Text(L("Najstarsze najpierw", "Oldest first")).tag(AppConfig.SortOrder.oldestFirst)
                    Text(L("Największe zmiany", "Largest changes")).tag(AppConfig.SortOrder.largestChanges)
                }
                Toggle(L("Uruchamiaj przy logowaniu", "Launch at login"), isOn: Binding(
                    get: { model.launchAtLogin },
                    set: { model.setLaunchAtLogin($0) }
                ))
            }

            Section("GHNotif " + model.installedVersion) {
                HStack {
                    Button(L("Sprawdź aktualizacje", "Check for updates")) {
                        Task { await model.checkForUpdates() }
                    }
                    .disabled(model.checkingUpdates)
                    Spacer()
                    if let release = model.availableRelease {
                        Link(L("Pobierz \(release.tagName)", "Download \(release.tagName)"), destination: release.htmlURL)
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
                LabeledContent(L("Aktualne źródło", "Current source"), value: model.tokenSource?.rawValue ?? L("brak", "none"))
            }
            Section {
                SecureField("Personal access token", text: $form.pat, prompt: Text(L("ghp_… (opcjonalnie)", "ghp_… (optional)")))
                HStack {
                    Button(L("Zapisz PAT", "Save PAT")) {
                        model.setPersonalAccessToken(form.pat)
                        form.pat = ""
                    }
                    .disabled(form.pat.trimmingCharacters(in: .whitespaces).isEmpty)
                    Button(L("Usuń PAT i użyj gh CLI", "Remove PAT and use gh CLI")) { model.setPersonalAccessToken(nil) }
                }
            } footer: {
                Text(L("Domyślnie używany jest token z `gh auth login`. PAT (classic: scope `repo`; fine-grained: Pull requests - read) ma pierwszeństwo i jest przechowywany w Keychain.", "By default the token from `gh auth login` is used. A PAT (classic: `repo` scope; fine-grained: Pull requests - read) takes precedence and is stored in Keychain."))
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

    private func splitValues(_ value: String) -> [String] {
        Array(Set(value.split(whereSeparator: { $0 == "," || $0.isNewline })
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty })).sorted()
    }

    private func exportWatchlist() {
        do {
            let panel = NSSavePanel()
            panel.nameFieldStringValue = "ghnotif-watchlist.json"
            panel.allowedContentTypes = [.json]
            guard panel.runModal() == .OK, let url = panel.url else { return }
            try model.watchlistData().write(to: url, options: .atomic)
            form.listError = nil
        } catch { form.listError = error.localizedDescription }
    }

    private func importWatchlist() {
        do {
            let panel = NSOpenPanel()
            panel.allowedContentTypes = [.json, .plainText]
            panel.allowsMultipleSelection = false
            guard panel.runModal() == .OK, let url = panel.url else { return }
            try model.importWatchlist(Data(contentsOf: url))
            form.listError = nil
        } catch { form.listError = error.localizedDescription }
    }

    private func importTeam() {
        guard !form.importingTeam else { return }
        form.importingTeam = true
        form.listError = nil
        Task {
            defer { form.importingTeam = false }
            do {
                try await model.importTeam(organization: form.organization, team: form.team)
            } catch { form.listError = error.localizedDescription }
        }
    }
}

enum SettingsPane: String, CaseIterable, Identifiable {
    case people, search, reminders, notifications, general, token

    var id: Self { self }

    var title: String {
        switch self {
        case .people: L("Osoby", "People")
        case .search: L("Wyszukiwanie", "Search")
        case .reminders: L("Przypomnienia", "Reminders")
        case .notifications: L("Powiadomienia", "Notifications")
        case .general: L("Ogólne", "General")
        case .token: "Token"
        }
    }

    var systemImage: String {
        switch self {
        case .people: "person.2.fill"
        case .search: "magnifyingglass"
        case .reminders: "alarm.fill"
        case .notifications: "bell.badge.fill"
        case .general: "gearshape.fill"
        case .token: "key.fill"
        }
    }

    var tint: Color {
        switch self {
        case .people: .blue
        case .search: .purple
        case .reminders: .red
        case .notifications: .pink
        case .general: .gray
        case .token: .orange
        }
    }
}

/// System Settings-style row: white symbol on a coloured rounded square.
struct SidebarLabel: View {
    let pane: SettingsPane
    let language: AppLanguage

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
    @Published var organization = ""
    @Published var team = ""
    @Published var importingTeam = false
    @Published var listError: String?
    @Published var excludedRepositories = ""
    @Published var excludedLabels = ""
}
