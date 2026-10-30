import AppKit
import GHNotifCore
import SwiftUI

/// Centered, always-on-top window shown at the scheduled review time.
@MainActor
final class ReviewReminderPresenter {
    private var window: NSWindow?

    func show(model: AppModel) {
        if window == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 520, height: 420),
                styleMask: [.titled, .closable, .fullSizeContentView],
                backing: .buffered, defer: false
            )
            window.title = "GHNotif - czas na review"
            window.titlebarAppearsTransparent = true
            window.isReleasedWhenClosed = false
            window.level = .floating
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            let hosting = NSHostingController(rootView: AnyView(ReviewReminderView(close: { [weak window] in
                window?.close()
            }).environmentObject(model)))
            hosting.sizingOptions = [.preferredContentSize]
            window.contentViewController = hosting
            self.window = window
        }
        guard let window else { return }
        window.center()
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
        NSSound(named: "Glass")?.play()
    }
}

private struct ReviewReminderView: View {
    @EnvironmentObject private var model: AppModel
    @StateObject private var layout = ReminderLayout()
    let close: () -> Void

    private var groups: [(repository: String, prs: [PullRequest])] {
        Dictionary(grouping: model.pullRequests, by: \.repository)
            .map { (repository: $0.key, prs: $0.value.sorted { $0.createdAt < $1.createdAt }) }
            .sorted { $0.repository.localizedStandardCompare($1.repository) == .orderedAscending }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                Image("TrayLogo", bundle: AppResources.bundle)
                    .renderingMode(.original)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("Czas na review PR-ów", "Time to review PRs")).font(.title2.weight(.semibold))
                    Text(summary).foregroundStyle(.secondary)
                }
                Spacer()
                if model.isLoading { ProgressView().controlSize(.small) }
            }
            .padding(.horizontal, 20)
            .padding(.top, 28)
            .padding(.bottom, 14)
            Divider()
            if model.pullRequests.isEmpty {
                Text(model.isLoading ? L("Sprawdzam listę…", "Checking the list…") : L("Nic nie czeka na Twoje review 🎉", "Nothing is waiting for your review 🎉"))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(groups, id: \.repository) { group in
                            Text("\(group.repository) · \(group.prs.count)")
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.secondary)
                                .padding(.horizontal, 10)
                                .padding(.top, 8)
                            ForEach(group.prs) { pr in
                                PullRequestRow(pr: pr, list: .active, toggleDismissed: {
                                    model.dismiss(pr)
                                }, snooze: {
                                    model.snooze(pr, until: Date().addingTimeInterval(2 * 60 * 60))
                                }, snoozeTomorrow: {
                                    model.snooze(pr, until: tomorrowMorning())
                                })
                            }
                        }
                    }
                    .padding(.vertical, 4)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { layout.contentHeight = $0 }
                }
                // A ScrollView's ideal height collapses, so size it from the measured content.
                .frame(height: min(max(layout.contentHeight, 60), 420))
            }
            Divider()
            HStack {
                Text(L("Kliknij PR, aby otworzyć go w przeglądarce.", "Click a PR to open it in the browser."))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button(L("Przypomnij za 15 min", "Remind me in 15 min")) {
                    model.snoozeReviewReminder(minutes: 15)
                    close()
                }
                Button(L("Gotowe", "Done"), action: close)
                    .keyboardShortcut(.defaultAction)
            }
            .padding(14)
        }
        .frame(width: 520)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var summary: String {
        let count = model.pullRequests.count
        if count == 0 { return L("Lista jest pusta", "The list is empty") }
        return L(Localization.polishPlural(count, one: "1 PR czeka na Twoją uwagę",
                                           few: "\(count) PR-y czekają na Twoją uwagę",
                                           many: "\(count) PR-ów czeka na Twoją uwagę"),
                 count == 1 ? "1 PR needs your attention" : "\(count) PRs need your attention")
    }

    private func tomorrowMorning() -> Date {
        let calendar = Calendar.current
        let tomorrow = calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: Date()))!
        return calendar.date(bySettingHour: 9, minute: 0, second: 0, of: tomorrow) ?? tomorrow
    }
}

private final class ReminderLayout: ObservableObject {
    @Published var contentHeight: CGFloat = 0
}
