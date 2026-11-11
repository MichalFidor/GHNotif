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
            detail(for: form.pane ?? .start)
                .navigationTitle((form.pane ?? .start).title)
        }
        .frame(width: 680, height: 480)
        .environment(\.locale, Localization.language.locale)
        .onAppear {
            if model.consumeFirstLaunch() || !model.config.hasSearches { form.pane = .start }
            form.host = model.config.host
            form.extraQualifiers = model.config.extraQualifiers
            form.excludedRepositories = model.config.excludedRepositories.joined(separator: ", ")
            form.excludedLabels = model.config.excludedLabels.joined(separator: ", ")
        }
    }

    @ViewBuilder
    private func detail(for pane: SettingsPane) -> some View {
        switch pane {
        case .start: startTab
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
                Text(L("Pokazywane są otwarte PR-y tych osób (drafty - zależnie od ustawienia w Wyszukiwaniu), w dowolnym repozytorium widocznym dla tokenu.", "Shows open PRs by these people (drafts - depending on the Search setting) in any repository visible to the token."))
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
            Link(login, destination: model.config.gitHubHost.webURL("/\(login)"))
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
                Toggle(L("…także prośby do moich zespołów", "…also requests to my teams"), isOn: $model.config.includeTeamReviewRequests)
                    .padding(.leading, 20)
                    .disabled(!model.config.includeReviewRequests)
            } header: {
                Text(L("Dodatkowo pokazuj", "Also show"))
            } footer: {
                Text(L("Niezależnie od autora - wzmianki obejmują też Twoje PR-y. Prośby o review domyślnie obejmują tylko te skierowane do Ciebie osobiście. Prośby do zespołów (np. gdy ktoś doda cały zespół QA jako reviewera) są oznaczone ikoną zespołu - mogą mocno wydłużyć listę, dlatego obie opcje są domyślnie wyłączone.", "Regardless of author - mentions include your own PRs too. Review requests cover only those sent to you personally by default. Team requests (e.g. when someone adds the whole QA team as reviewer) are marked with a team icon - they can make the list much longer, so both options are off by default."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                Toggle(L("Pokazuj drafty", "Show drafts"), isOn: $model.config.includeDrafts)
                Toggle(L("Ukrywaj zatwierdzone PR-y", "Hide approved PRs"), isOn: $model.config.hideApproved)
            } header: {
                Text(L("Widoczność", "Visibility"))
            } footer: {
                Text(L("Drafty są oznaczone etykietą Draft. Zatwierdzone = decyzja review „Approved” na GitHubie - takie PR-y zwykle nie potrzebują już Twojej uwagi.", "Drafts are marked with a Draft label. Approved = the \"Approved\" review decision on GitHub - such PRs usually don't need your attention anymore."))
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
                Stepper(value: $model.config.staleAfterDays, in: 0...60) {
                    Text(model.config.staleAfterDays == 0
                         ? L("Wyróżnianie starych PR-ów: wyłączone", "Highlighting old PRs: off")
                         : L("Wyróżniaj PR-y otwarte od \(model.config.staleAfterDays) dni", "Highlight PRs open for \(model.config.staleAfterDays) days"))
                }
                .help(L("Data utworzenia takich PR-ów jest na liście pomarańczowa, z ikoną zegara. 0 wyłącza wyróżnianie.", "Such PRs show their creation date in orange with a clock icon. 0 turns it off."))
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
                        Button(L("Otwórz wydanie \(release.tagName)", "View release \(release.tagName)")) {
                            model.openRelease()
                        }
                    }
                }
                if !model.updateStatus.isEmpty { Text(model.updateStatus).font(.caption) }
                Text(L("Samoinstalacja jest wyłączona. Zbuduj aktualizację z przejrzanych źródeł.",
                       "Self-installation is disabled. Build the update from reviewed source.")).font(.caption)
            }
        }
        .formStyle(.grouped)
    }

    private var tokenTab: some View {
        Form {
            Section {
                Text(L("Do dostępu firmowego używaj wyłącznie fine-grained PAT tylko do odczytu z datą wygaśnięcia, nie tokenu gh ani classic PAT.",
                       "For company access, use only an expiring read-only fine-grained PAT, not a gh token or classic PAT."))
            }
            Section {
                LabeledContent(L("Aktualne źródło", "Current source"), value: model.tokenSource?.rawValue ?? L("brak", "none"))
                connectionCheck
            } footer: {
                Text(LocalizedStringKey(L("GHNotif widzi tylko PR-y z repozytoriów dostępnych dla tokenu. Brak PR-ów z prywatnych repozytoriów = brak scope'u `repo`. Widać tylko PR-y z własnych repozytoriów = token nie jest autoryzowany przez SSO dla organizacji albo organizacja blokuje classic PAT lub nie zatwierdziła GitHub CLI (wtedy GitHub ukrywa wyniki bez ostrzeżenia).", "GHNotif only sees PRs from repositories the token can access. PRs from private repositories missing = no `repo` scope. Only PRs from your own repositories = the token is not SSO-authorized for the organization, or the organization blocks classic PATs or hasn't approved GitHub CLI (GitHub then hides results without a warning).")))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Text(LocalizedStringKey(L("1. Zainstaluj GitHub CLI: `brew install gh`.\n2. W terminalu uruchom `gh auth login` (GitHub.com, logowanie przez przeglądarkę) i autoryzuj organizacje z SSO, jeśli GitHub o to poprosi (później: `gh auth refresh`).\n3. Jeśli poniżej zapisany jest PAT, usuń go - PAT ma pierwszeństwo przed `gh`.", "1. Install GitHub CLI: `brew install gh`.\n2. In a terminal run `gh auth login` (GitHub.com, sign in with the browser) and authorize SSO organizations if GitHub asks (later: `gh auth refresh`).\n3. If a PAT is saved below, remove it - a PAT takes precedence over `gh`.")))
                    .font(.callout)
                    .textSelection(.enabled)
            } header: {
                Text(L("Opcja 1: GitHub CLI (najprostsza)", "Option 1: GitHub CLI (simplest)"))
            } footer: {
                Text(LocalizedStringKey(L("Nie trzeba tworzyć ani wklejać tokenu - GHNotif pobiera go przez `gh auth token` (szuka `gh` w /opt/homebrew/bin, /usr/local/bin i /usr/bin). Uwaga: token `gh` ma szerokie uprawnienia (domyślnie `repo`, `read:org`, `gist`, także zapis). Jeśli zależy Ci na minimalnym dostępie, użyj fine-grained PAT tylko do odczytu.", "No token to create or paste - GHNotif gets it with `gh auth token` (looking for `gh` in /opt/homebrew/bin, /usr/local/bin and /usr/bin). Note: the `gh` token is broad (by default `repo`, `read:org`, `gist`, including write access). For minimal access use a read-only fine-grained PAT.")))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Text(LocalizedStringKey(L("Fine-grained PAT tylko do odczytu (najbezpieczniejszy):\n• Resource owner - organizacja z PR-ami,\n• Repository access - All repositories,\n• Permissions: Pull requests - read i Commit statuses - read (link je ustawia), Members - read (opcjonalnie, do importu zespołów - dodaj ręcznie).\nJeden token obejmuje jednego właściciela - PR-y z innych organizacji nie będą widoczne. Organizacja może wymagać zatwierdzenia tokenu przez admina.", "Read-only fine-grained PAT (most secure):\n• Resource owner - the organization with the PRs,\n• Repository access - All repositories,\n• Permissions: Pull requests - read and Commit statuses - read (the link sets them), Members - read (optional, for team import - add it by hand).\nOne token covers one owner - PRs from other organizations won't show. The organization may require an admin to approve the token.")))
                    .font(.callout)
                    .textSelection(.enabled)
                Link(L("Utwórz fine-grained PAT tylko do odczytu", "Create a read-only fine-grained PAT"),
                     destination: model.config.gitHubHost.newTokenURL)
                Text(LocalizedStringKey(L("Classic PAT (gdy potrzebujesz kilku organizacji): scope `repo` - daje też zapis, bo GitHub nie ma scope'u tylko do odczytu dla prywatnych repozytoriów. `read:org` jest opcjonalny (import zespołów). Organizacje z SAML SSO: przy tokenie kliknij Configure SSO → Authorize.", "Classic PAT (when you need several organizations): the `repo` scope - it also grants write access, because GitHub has no read-only scope for private repositories. `read:org` is optional (team import). SAML SSO organizations: next to the token click Configure SSO → Authorize.")))
                    .font(.callout)
                    .textSelection(.enabled)
                Link(L("Utwórz classic PAT (tylko repo)", "Create a classic PAT (repo only)"),
                     destination: model.config.gitHubHost.newClassicTokenURL)
                SecureField("Personal access token", text: $form.pat, prompt: Text(L("github_pat_… lub ghp_… (opcjonalnie)", "github_pat_… or ghp_… (optional)")))
                HStack {
                    Button(L("Zapisz PAT", "Save PAT")) {
                        model.setPersonalAccessToken(form.pat)
                        form.pat = ""
                    }
                    .disabled(form.pat.trimmingCharacters(in: .whitespaces).isEmpty)
                    Button(L("Usuń PAT i użyj gh CLI", "Remove PAT and use gh CLI")) { model.setPersonalAccessToken(nil) }
                }
            } header: {
                Text(L("Opcja 2: personal access token", "Option 2: personal access token"))
            } footer: {
                Text(LocalizedStringKey(L("PAT ma pierwszeństwo przed `gh`, jest przechowywany w Keychain i przypisany do serwera z sekcji poniżej - po zmianie serwera zapisz PAT utworzony na nowym serwerze. Ustaw datę wygaśnięcia tokenu.", "A PAT takes precedence over `gh`, is stored in Keychain and bound to the server below - after changing the server save a PAT created on the new one. Set an expiration date on the token.")))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                HStack {
                    TextField(L("Host", "Host"), text: $form.host, prompt: Text("github.com"))
                        .labelsHidden()
                        .onSubmit(applyHost)
                    Button(L("Zastosuj", "Apply"), action: applyHost)
                        .disabled(!GitHubHost.isValid(form.host) || GitHubHost.normalize(form.host) == model.config.host)
                }
                if !GitHubHost.isValid(form.host) {
                    Text(L("Wpisz samą nazwę hosta, np. github.firma.pl (bez portu, loginu i znaków specjalnych).",
                           "Enter just the host name, e.g. github.company.com (no port, login or special characters)."))
                        .font(.caption).foregroundStyle(.red)
                }
            } header: {
                Text(L("Serwer GitHub", "GitHub server"))
            } footer: {
                Text(LocalizedStringKey(L("Zostaw `github.com`, chyba że firma używa GitHub Enterprise Server lub GHE.com - wtedy wpisz jego host, np. `github.firma.pl`. Token jest wysyłany tylko do tego serwera, więc wpisuj wyłącznie zaufany adres firmowy. Dla GitHub CLI zaloguj się poleceniem `gh auth login --hostname HOST`; PAT utwórz na tym serwerze.", "Keep `github.com` unless your company uses GitHub Enterprise Server or GHE.com - then enter its host, e.g. `github.company.com`. The token is sent only to this server, so enter a trusted company address only. For GitHub CLI sign in with `gh auth login --hostname HOST`; create the PAT on that server.")))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .task { await model.loadTokenSource() }
    }

    @ViewBuilder
    private var connectionCheck: some View {
        HStack {
            Button(L("Sprawdź połączenie", "Check connection")) {
                Task { await model.checkConnection() }
            }
            .disabled(model.checkingConnection)
            if model.checkingConnection { ProgressView().controlSize(.small) }
            Spacer()
            if let url = model.connectionSSOURL {
                Link(L("Autoryzuj SSO", "Authorize SSO"), destination: url)
            }
        }
        if !model.connectionReport.isEmpty {
            VStack(alignment: .leading, spacing: 3) {
                ForEach(Array(model.connectionReport.enumerated()), id: \.offset) { _, line in
                    Text(LocalizedStringKey(line))
                        .font(.caption)
                        .foregroundStyle(line.hasPrefix("✗") ? .red : line.hasPrefix("?") ? .orange : .secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .textSelection(.enabled)
        }
    }

    private func applyHost() {
        guard GitHubHost.isValid(form.host) else { return }
        model.config.host = GitHubHost.normalize(form.host)
        form.host = model.config.host
    }

    private var startTab: some View {
        Form {
            Section {
                Text(L("GHNotif pokazuje w pasku menu otwarte PR-y wybranych osób. Do działania potrzebuje tokenu GitHub i listy obserwowanych osób - reszta jest opcjonalna.", "GHNotif shows open PRs by chosen people in the menu bar. It needs a GitHub token and a list of watched people - everything else is optional."))
                    .font(.callout)
            }
            Section {
                setupStep(done: model.tokenSource != nil, title: L("1. Token GitHub", "1. GitHub token"),
                          detail: model.tokenSource.map { L("Źródło: \($0.rawValue). Sprawdź, czy widzi Twoje organizacje.", "Source: \($0.rawValue). Check that it can see your organizations.") }
                            ?? L("Brak tokenu - zaloguj się przez GitHub CLI (`gh auth login`) albo zapisz PAT.", "No token - sign in with GitHub CLI (`gh auth login`) or save a PAT."),
                          action: L("Ustawienia tokenu", "Token settings"), pane: .token)
                connectionCheck
            }
            Section {
                setupStep(done: !model.config.users.isEmpty, title: L("2. Obserwowane osoby", "2. Watched people"),
                          detail: model.config.users.isEmpty
                            ? L("Dodaj loginy pojedynczo albo zaimportuj cały zespół z organizacji.", "Add logins one by one or import a whole team from an organization.")
                            : L("Obserwowane osoby: \(model.config.users.count).", "Watched people: \(model.config.users.count)."),
                          action: L("Dodaj osoby", "Add people"), pane: .people)
            }
            Section {
                setupStep(done: nil, title: L("3. Co pokazywać (opcjonalnie)", "3. What to show (optional)"),
                          detail: searchSummary, action: L("Wyszukiwanie", "Search"), pane: .search)
                setupStep(done: nil, title: L("4. Powiadomienia i przypomnienia (opcjonalnie)", "4. Notifications and reminders (optional)"),
                          detail: model.config.notify
                            ? L("Powiadomienia włączone - możesz wysłać testowe i ustawić ciszę nocną.", "Notifications on - you can send a test one and set quiet hours.")
                            : L("Powiadomienia wyłączone.", "Notifications off."),
                          action: L("Powiadomienia", "Notifications"), pane: .notifications)
            }
        }
        .formStyle(.grouped)
        .task { await model.loadTokenSource() }
    }

    private var searchSummary: String {
        var parts: [String] = []
        if model.config.includeMentions { parts.append(L("wzmianki", "mentions")) }
        if model.config.includeReviewRequests { parts.append(L("prośby o review", "review requests")) }
        if model.config.includeReviewRequests && model.config.includeTeamReviewRequests { parts.append(L("review zespołów", "team reviews")) }
        if model.config.includeDrafts { parts.append(L("drafty", "drafts")) }
        if model.config.hideApproved { parts.append(L("bez zatwierdzonych", "no approved")) }
        let list = parts.isEmpty ? L("tylko PR-y obserwowanych osób", "only PRs by watched people") : parts.joined(separator: ", ")
        return L("Teraz: \(list). Możesz też ograniczyć zakres do organizacji i wykluczyć repozytoria lub etykiety.", "Now: \(list). You can also limit the scope to an organization and exclude repositories or labels.")
    }

    private func setupStep(done: Bool?, title: String, detail: String, action: String, pane: SettingsPane) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: done == true ? "checkmark.circle.fill" : done == false ? "exclamationmark.circle.fill" : "circle.dashed")
                .foregroundStyle(done == true ? .green : done == false ? .orange : .secondary)
                .font(.title3)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(LocalizedStringKey(detail)).font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button(action) { form.pane = pane }
        }
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
    case start, people, search, reminders, notifications, general, token

    var id: Self { self }

    var title: String {
        switch self {
        case .start: L("Pierwsze kroki", "Get started")
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
        case .start: "flag.checkered"
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
        case .start: .green
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
    @Published var host = ""
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
