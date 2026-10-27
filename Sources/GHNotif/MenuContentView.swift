import AppKit
import GHNotifCore
import SwiftUI

struct MenuContentView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.openWindow) private var openWindow
    // `@State` is a macro whose plugin ships only with full Xcode; `@StateObject` builds with Command Line Tools.
    @StateObject private var layout = ListLayout()

    static let maxListHeight: CGFloat = 520

    private var groupedByRepository: [(repository: String, prs: [PullRequest])] {
        Dictionary(grouping: displayedPullRequests, by: \.repository)
            .map { ($0.key, sorted($0.value)) }
            .sorted { $0.repository.localizedCaseInsensitiveCompare($1.repository) == .orderedAscending }
    }

    private var displayedPullRequests: [PullRequest] {
        switch layout.list {
        case .active: model.pullRequests
        case .done: model.dismissedPullRequests
        case .snoozed: model.snoozedRequests
        }
    }

    private func sorted(_ prs: [PullRequest]) -> [PullRequest] {
        switch model.config.sortOrder {
        case .recentlyUpdated: prs.sorted { $0.updatedAt > $1.updatedAt }
        case .oldestFirst: prs.sorted { $0.createdAt < $1.createdAt }
        case .largestChanges:
            prs.sorted { Int64($0.additions) + Int64($0.deletions) > Int64($1.additions) + Int64($1.deletions) }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            banners
            content
            Divider()
            footer
        }
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { layout.panelHeight = $0 }
        // The MenuBarExtra window doesn't always shrink when groups collapse; keep content at the top
        // and resize the window ourselves so nothing floats in the middle.
        .frame(maxHeight: .infinity, alignment: .top)
        .background(WindowHeightSync(height: layout.panelHeight) { layout.window = $0 })
    }

    private var header: some View {
        HStack {
            Image("TrayLogo", bundle: .module)
                .renderingMode(.original)
                .resizable()
                .scaledToFit()
                .frame(width: 22, height: 22)
            Text(layout.list.title).font(.headline)
            Text("\(displayedPullRequests.count)").foregroundStyle(.secondary)
            Spacer()
            Menu {
                ForEach(PullRequestList.allCases) { list in
                    Button {
                        layout.list = list
                    } label: {
                        if layout.list == list { Label(list.title, systemImage: "checkmark") }
                        else { Text(list.title) }
                    }
                }
            } label: {
                Image(systemName: layout.list.icon)
            }
            .buttonStyle(.borderless)
            .help(L("Wybierz listę PR-ów", "Choose PR list"))
            if model.isLoading { ProgressView().controlSize(.small) }
            Button {
                Task { await model.refresh() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help(L("Odśwież", "Refresh"))
            .disabled(model.isLoading)
            Button {
                openSettings()
            } label: {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.borderless)
            .help(L("Ustawienia", "Settings"))
        }
        .padding(10)
    }

    @ViewBuilder
    private var banners: some View {
        if let release = model.availableRelease {
            Banner(systemImage: "arrow.down.circle.fill", tint: .blue,
                   text: L("Dostępna nowa wersja \(release.tagName).", "New version \(release.tagName) is available."),
                   actionTitle: L("Pobierz", "Download")) {
                NSWorkspace.shared.open(release.htmlURL)
            }
        }
        if let error = model.lastError {
            Banner(systemImage: "exclamationmark.triangle.fill", tint: .red, text: error)
        }
        if model.ssoBlockedOrgCount > 0 {
            Banner(
                systemImage: "lock.fill", tint: .orange,
                text: L("Wyniki z \(model.ssoBlockedOrgCount) organizacji są ukryte – token nie jest autoryzowany przez SSO.",
                         "Results from \(model.ssoBlockedOrgCount) organizations are hidden – the token is not SSO-authorized."),
                actionTitle: L("Autoryzuj", "Authorize")
            ) {
                NSWorkspace.shared.open(model.ssoSettingsURL)
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        if displayedPullRequests.isEmpty {
            Text(emptyStateText)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, minHeight: 80)
        } else {
            ScrollView {
                // Eager VStack so the full content height can be measured.
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(groupedByRepository, id: \.repository) { group in
                        let collapsed = model.isCollapsed(group.repository)
                        RepositoryHeader(name: group.repository, count: group.prs.count, isCollapsed: collapsed) {
                            model.toggleCollapsed(group.repository)
                        }
                        if !collapsed {
                            ForEach(group.prs) { pr in
                                PullRequestRow(pr: pr, list: layout.list, toggleDismissed: {
                                    if layout.list == .done {
                                        model.restore(pr)
                                    } else if layout.list == .snoozed {
                                        model.unsnooze(pr)
                                    } else {
                                        model.dismiss(pr)
                                    }
                                }, snooze: {
                                    model.snooze(pr, until: Date().addingTimeInterval(2 * 60 * 60))
                                }, snoozeTomorrow: {
                                    let tomorrow = Calendar.current.date(byAdding: .day, value: 1,
                                                                         to: Calendar.current.startOfDay(for: Date()))!
                                    model.snooze(pr, until: Calendar.current.date(bySettingHour: 9, minute: 0,
                                        second: 0, of: tomorrow) ?? tomorrow)
                                })
                            }
                        }
                    }
                }
                .padding(.vertical, 4)
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { layout.contentHeight = $0 }
            }
            // A MenuBarExtra window sizes itself from the ideal size, where a ScrollView collapses to ~0;
            // an explicit height from the measured content prevents the empty list.
            .frame(height: min(max(layout.contentHeight, 60), Self.maxListHeight))
        }
    }

    private var emptyStateText: String {
        if !model.config.hasSearches { return L("Nikogo nie obserwujesz – dodaj osoby w Ustawieniach.", "You aren't watching anyone – add people in Settings.") }
        if model.isLoading && model.lastRefresh == nil { return L("Ładowanie…", "Loading…") }
        if layout.list == .done { return L("Brak załatwionych PR-ów.", "No done PRs.") }
        if layout.list == .snoozed { return L("Brak odłożonych PR-ów.", "No snoozed PRs.") }
        if !model.snoozedRequests.isEmpty { return L("PR-y odłożone na później ⏰", "PRs snoozed for later ⏰") }
        if !model.dismissedPullRequests.isEmpty { return L("Wszystko załatwione 🎉", "All done 🎉") }
        return L("Brak otwartych PR-ów 🎉", "No open PRs 🎉")
    }

    private func openSettings() {
        // MenuBarExtra has no API to close its panel; closing the hosting window does the same as clicking away.
        layout.window?.close()
        NSApp.activate()
        openWindow(id: SettingsView.windowID)
    }

    private var footer: some View {
        HStack {
            if let lastRefresh = model.lastRefresh {
                Text(L("Odświeżono", "Refreshed") + " " + lastRefresh.formatted(Date.FormatStyle(date: .omitted, time: .shortened).locale(Localization.language.locale)))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button(L("Zakończ", "Quit")) { NSApp.terminate(nil) }
        }
        .buttonStyle(.borderless)
        .padding(10)
    }
}

private final class ListLayout: ObservableObject {
    @Published var contentHeight: CGFloat = 0
    @Published var panelHeight: CGFloat = 0
    @Published var list: PullRequestList = .active
    weak var window: NSWindow?
}

enum PullRequestList: String, CaseIterable, Identifiable {
    case active, done, snoozed
    var id: Self { self }
    var title: String {
        switch self {
        case .active: L("PR-y do review", "PRs to review")
        case .done: L("Załatwione", "Done")
        case .snoozed: L("Odłożone", "Snoozed")
        }
    }
    var icon: String {
        switch self {
        case .active: "tray"
        case .done: "checkmark.circle"
        case .snoozed: "clock"
        }
    }
}

/// Shrinks or grows the hosting window to `height`, keeping its top edge fixed under the menu bar.
struct WindowHeightSync: NSViewRepresentable {
    let height: CGFloat
    var onWindow: (NSWindow?) -> Void = { _ in }

    func makeNSView(context: Context) -> NSView { NSView() }

    func updateNSView(_ view: NSView, context: Context) {
        let target = height
        let onWindow = onWindow
        DispatchQueue.main.async {
            onWindow(view.window)
            Self.apply(target, to: view.window)
        }
    }

    static func apply(_ height: CGFloat, to window: NSWindow?) {
        guard height > 0, let window, let content = window.contentView else { return }
        let delta = content.frame.height - height.rounded(.up)
        guard abs(delta) >= 1 else { return }
        var frame = window.frame
        frame.origin.y += delta
        frame.size.height -= delta
        window.setFrame(frame, display: true, animate: false)
    }
}

private struct RepositoryHeader: View {
    let name: String
    let count: Int
    let isCollapsed: Bool
    let toggle: () -> Void

    var body: some View {
        Button(action: toggle) {
            HStack(spacing: 6) {
                Image(systemName: "chevron.right")
                    .font(.caption2.weight(.bold))
                    .rotationEffect(.degrees(isCollapsed ? 0 : 90))
                    .frame(width: 10)
                Text(name).font(.caption.weight(.semibold))
                Spacer()
                Text(String(count))
                    .font(.caption2.weight(.semibold).monospacedDigit())
                    .padding(.horizontal, 6)
                    .padding(.vertical, 1)
                    .background(Capsule().fill(.secondary.opacity(0.2)))
            }
            .foregroundStyle(.secondary)
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.5))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(isCollapsed ? L("Rozwiń \(name)", "Expand \(name)") : L("Zwiń \(name)", "Collapse \(name)"))
    }
}

private struct Banner: View {
    let systemImage: String
    let tint: Color
    let text: String
    var actionTitle: String?
    var action: (() -> Void)?

    var body: some View {
        HStack(alignment: .top) {
            Image(systemName: systemImage).foregroundStyle(tint)
            Text(text).font(.caption).fixedSize(horizontal: false, vertical: true)
            Spacer()
            if let actionTitle, let action {
                Button(actionTitle, action: action).controlSize(.small)
            }
        }
        .padding(8)
        .background(tint.opacity(0.1))
    }
}

struct PullRequestRow: View {
    let pr: PullRequest
    let list: PullRequestList
    let toggleDismissed: () -> Void
    let snooze: () -> Void
    let snoozeTomorrow: () -> Void
    @StateObject private var hover = HoverState()

    var body: some View {
        HStack(alignment: .top, spacing: 5) {
            Button {
                NSWorkspace.shared.open(pr.url)
            } label: {
                HStack(alignment: .top, spacing: 8) {
                    AsyncImage(url: pr.authorAvatarURL) { image in
                        image.resizable()
                    } placeholder: {
                        Circle().fill(.quaternary)
                    }
                    .frame(width: 22, height: 22)
                    .clipShape(Circle())
                    .help(L("Autor: \(pr.author)", "Author: \(pr.author)"))

                    VStack(alignment: .leading, spacing: 2) {
                        Text(pr.title).lineLimit(2)
                        Text("#" + String(pr.number) + " · " + "\(pr.author) · \(pr.createdAt.formatted(.relative(presentation: .named).locale(Localization.language.locale))) · +\(pr.additions)/−\(pr.deletions)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: 250, alignment: .leading)
                    ReasonBadges(reasons: pr.reasons)
                    ReviewBadge(decision: pr.reviewDecision)
                    ChecksIndicator(state: pr.checks)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .fixedSize(horizontal: true, vertical: false)
            .help(L("Otwórz PR w przeglądarce", "Open PR in browser") + "\n" + pr.url.absoluteString)
            if list == .active {
                Menu {
                    Button(L("Za 2 godziny", "In 2 hours"), systemImage: "clock") { snooze() }
                    Button(L("Jutro rano", "Tomorrow morning"), systemImage: "sunrise") { snoozeTomorrow() }
                } label: {
                    Image(systemName: "clock").foregroundStyle(.secondary)
                }
                .menuStyle(.borderlessButton)
                .help(L("Przypomnij później", "Remind me later"))
            }
            Button(action: toggleDismissed) {
                Image(systemName: list == .active ? "checkmark.circle" : "arrow.uturn.backward.circle")
                    .foregroundStyle(list == .active ? Color.secondary : Color.accentColor)
            }
            .buttonStyle(.borderless)
            .help(list == .active ? L("Oznacz jako załatwione", "Mark as done") : L("Przywróć na listę wymagających uwagi", "Move back to needs attention"))
            .accessibilityLabel(list == .active ? L("Oznacz PR jako załatwiony", "Mark PR as done") : L("Przywróć PR", "Restore PR"))
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(hover.isHovered ? Color.accentColor.opacity(0.15) : .clear)
        .onHover { hover.isHovered = $0 }
    }
}

private final class HoverState: ObservableObject {
    @Published var isHovered = false
}

private struct ReasonBadges: View {
    let reasons: Set<MatchReason>

    var body: some View {
        if reasons.contains(.mention) {
            Text("@")
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .frame(width: 16, height: 16)
                .background(Circle().fill(Color.purple))
                .help(L("Ktoś wspomniał Cię w tym PR-ze", "Someone mentioned you in this PR"))
        }
        if reasons.contains(.reviewRequested) {
            Image(systemName: "person.crop.circle.badge.questionmark")
                .foregroundStyle(.purple)
                .help(L("Poproszono Cię o review", "Your review was requested"))
        }
    }
}

private struct ReviewBadge: View {
    let decision: ReviewDecision?

    var body: some View {
        switch decision {
        case .approved:
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).help(L("Review: PR zatwierdzony", "Review: approved"))
        case .changesRequested:
            Image(systemName: "exclamationmark.bubble.fill").foregroundStyle(.orange).help(L("Review: poproszono o zmiany", "Review: changes requested"))
        case .reviewRequired:
            Image(systemName: "eye").foregroundStyle(.secondary).help(L("Review: wymagane zatwierdzenie", "Review: approval required"))
        case nil:
            EmptyView()
        }
    }
}

private struct ChecksIndicator: View {
    let state: CheckState?

    var body: some View {
        switch state {
        case .success:
            Circle().fill(.green).frame(width: 8, height: 8).help(L("CI: wszystkie sprawdzenia zakończone powodzeniem", "CI: all checks passed")).padding(.top, 5)
        case .failure, .error:
            Circle().fill(.red).frame(width: 8, height: 8).help(L("CI: błąd lub nieudane sprawdzenia", "CI: error or failed checks")).padding(.top, 5)
        case .pending, .expected:
            Circle().fill(.yellow).frame(width: 8, height: 8).help(L("CI: oczekuje lub jest w toku", "CI: pending or in progress")).padding(.top, 5)
        case nil:
            EmptyView()
        }
    }
}
