import AppKit
import GHNotifCore

/// MenuBarExtra renders only part of a composite SwiftUI label (an HStack loses its image), so the logo,
/// status marker, and count are drawn into a single non-template image.
enum MenuBarIcon {
    private static let logo: NSImage? = Bundle.module.image(forResource: "TrayLogo")
    private static let approval = NotificationIcon.image(for: .approved)
    private static let logoSize = NSSize(width: 18, height: 18)
    private static let approvalSize = NSSize(width: 16, height: 16)
    private static let height: CGFloat = 22
    private static let spacing: CGFloat = 4

    static func image(count: Int, hasError: Bool, isReady: Bool) -> NSImage {
        let showApproval = count == 0 && !hasError && isReady
        let font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .medium)
        let text = count > 0 ? String(count) : nil
        let textWidth = text.map { ($0 as NSString).size(withAttributes: [.font: font]).width.rounded(.up) } ?? 0
        let warning = hasError
            ? NSImage(systemSymbolName: "exclamationmark.triangle.fill", accessibilityDescription: L("Błąd", "Error"))?
                .withSymbolConfiguration(.init(pointSize: 11, weight: .bold).applying(.init(paletteColors: [.systemOrange])))
            : nil
        var width = logoSize.width
        if let warning { width += spacing + warning.size.width }
        if showApproval { width += spacing + approvalSize.width }
        if text != nil { width += spacing + textWidth }

        let image = NSImage(size: NSSize(width: width, height: height), flipped: false) { _ in
            var x: CGFloat = 0
            logo?.draw(in: NSRect(x: x, y: (height - logoSize.height) / 2, width: logoSize.width, height: logoSize.height))
            x += logoSize.width
            if let warning {
                x += spacing
                warning.draw(in: NSRect(x: x, y: (height - warning.size.height) / 2,
                                        width: warning.size.width, height: warning.size.height))
                x += warning.size.width
            }
            if let text {
                x += spacing
                // Resolved at draw time, so the count follows the light/dark menu bar appearance.
                let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.labelColor]
                let size = (text as NSString).size(withAttributes: attributes)
                (text as NSString).draw(at: NSPoint(x: x, y: (height - size.height) / 2), withAttributes: attributes)
            }
            if showApproval {
                x += spacing
                approval.draw(in: NSRect(x: x, y: (height - approvalSize.height) / 2,
                                        width: approvalSize.width, height: approvalSize.height))
            }
            return true
        }
        image.isTemplate = false
        image.accessibilityDescription = hasError
            ? L("GHNotif - błąd pobierania PR-ów", "GHNotif - failed to fetch PRs")
            : showApproval ? L("GHNotif - brak PR-ów do review", "GHNotif - no PRs to review")
            : count > 0 ? L("GHNotif: \(count) PR-ów wymaga uwagi", "GHNotif: \(count) PRs need attention") : "GHNotif"
        return image
    }
}
