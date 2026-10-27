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
            .map { ($0.key, $0.value.sorted { $0.updatedAt > $1.updatedAt }) }
            .sorted { $0.repository.localizedCaseInsensitiveCompare($1.repository) == .orderedAscending }
    }

    private var displayedPullRequests: [PullRequest] {
        layout.showDismissed ? model.dismissedPullRequests : model.pullRequests
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
    }

    private var header: some View {
        HStack {
            Text(layout.showDismissed ? "Załatwione PR-y" : "PR-y do review").font(.headline)
            Text("\(displayedPullRequests.count)").foregroundStyle(.secondary)
            Spacer()
            Button {
                layout.showDismissed.toggle()
            } label: {
                Image(systemName: layout.showDismissed ? "tray" : "checkmark.circle")
            }
            .buttonStyle(.borderless)
            .help(layout.showDismissed ? "Pokaż wymagające uwagi" : "Pokaż załatwione")
            if model.isLoading { ProgressView().controlSize(.small) }
            Button {
                Task { await model.refresh() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Odśwież")
            .disabled(model.isLoading)
        }
        .padding(10)
    }

    @ViewBuilder
    private var banners: some View {
        if let error = model.lastError {
            Banner(systemImage: "exclamationmark.triangle.fill", tint: .red, text: error)
        }
        if model.ssoBlockedOrgCount > 0 {
            Banner(
                systemImage: "lock.fill", tint: .orange,
                text: "Wyniki z \(model.ssoBlockedOrgCount) organizacji są ukryte – token nie jest autoryzowany przez SSO.",
                actionTitle: "Autoryzuj"
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
                        Text(group.repository)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 4)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(.quaternary.opacity(0.5))
                        ForEach(group.prs) { pr in
                            PullRequestRow(pr: pr, isDismissed: layout.showDismissed) {
                                if layout.showDismissed {
                                    model.restore(pr)
                                } else {
                                    model.dismiss(pr)
                                }
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
        if model.config.users.isEmpty { return "Nikogo nie obserwujesz – dodaj osoby w Ustawieniach." }
        if model.isLoading && model.lastRefresh == nil { return "Ładowanie…" }
        if layout.showDismissed { return "Brak załatwionych PR-ów." }
        if !model.dismissedPullRequests.isEmpty { return "Wszystko załatwione 🎉" }
        return "Brak otwartych PR-ów 🎉"
    }

    private var footer: some View {
        HStack {
            if let lastRefresh = model.lastRefresh {
                Text("Odświeżono \(lastRefresh.formatted(date: .omitted, time: .shortened))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button("Ustawienia…") {
                NSApp.activate()
                openWindow(id: SettingsView.windowID)
            }
            Button("Zakończ") { NSApp.terminate(nil) }
        }
        .buttonStyle(.borderless)
        .padding(10)
    }
}

private final class ListLayout: ObservableObject {
    @Published var contentHeight: CGFloat = 0
    @Published var showDismissed = false
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

private struct PullRequestRow: View {
    let pr: PullRequest
    let isDismissed: Bool
    let toggleDismissed: () -> Void
    @StateObject private var hover = HoverState()

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
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

                    VStack(alignment: .leading, spacing: 2) {
                        Text(pr.title).lineLimit(2)
                        Text("#" + String(pr.number) + " · " + "\(pr.author) · \(pr.createdAt.formatted(.relative(presentation: .named)))")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 4)
                    ReviewBadge(decision: pr.reviewDecision)
                    ChecksIndicator(state: pr.checks)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(pr.url.absoluteString)
            Button(action: toggleDismissed) {
                Image(systemName: isDismissed ? "arrow.uturn.backward.circle" : "checkmark.circle")
                    .foregroundStyle(isDismissed ? Color.accentColor : .secondary)
            }
            .buttonStyle(.borderless)
            .help(isDismissed ? "Przywróć na listę wymagających uwagi" : "Oznacz jako załatwione")
            .accessibilityLabel(isDismissed ? "Przywróć PR" : "Oznacz PR jako załatwiony")
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

private struct ReviewBadge: View {
    let decision: ReviewDecision?

    var body: some View {
        switch decision {
        case .approved:
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).help("Approved")
        case .changesRequested:
            Image(systemName: "exclamationmark.bubble.fill").foregroundStyle(.orange).help("Changes requested")
        case .reviewRequired:
            Image(systemName: "eye").foregroundStyle(.secondary).help("Review required")
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
            Circle().fill(.green).frame(width: 8, height: 8).help("CI: success").padding(.top, 5)
        case .failure, .error:
            Circle().fill(.red).frame(width: 8, height: 8).help("CI: failed").padding(.top, 5)
        case .pending, .expected:
            Circle().fill(.yellow).frame(width: 8, height: 8).help("CI: pending").padding(.top, 5)
        case nil:
            EmptyView()
        }
    }
}
