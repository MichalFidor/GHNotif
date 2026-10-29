import AppKit
import GHNotifCore
import UserNotifications

/// GitHub timeline-style badge (Octicon on a coloured circle) attached to notifications.
enum NotificationIcon {
    private static let size: CGFloat = 128

    static func attachment(for event: AlertEvent) -> UNNotificationAttachment? {
        guard let png = png(for: event) else { return nil }
        // The system moves attachment files into its own store, so each notification gets a fresh copy.
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ghnotif-\(UUID().uuidString).png")
        do {
            try png.write(to: url)
            return try UNNotificationAttachment(identifier: event.rawValue, url: url)
        } catch {
            NSLog("GHNotif: nie udało się dołączyć ikony: %@", error.localizedDescription)
            return nil
        }
    }

    static func png(for event: AlertEvent) -> Data? {
        guard let url = Bundle.module.url(forResource: "octicon-\(style(for: event).octicon)", withExtension: "svg"),
              let glyph = NSImage(contentsOf: url) else { return nil }
        let (_, color) = style(for: event)
        let pixels = Int(size)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ), let context = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        let bounds = NSRect(x: 0, y: 0, width: size, height: size)
        color.setFill()
        NSBezierPath(ovalIn: bounds).fill()
        let inset = size * 0.25
        tinted(glyph, .white, size: size - 2 * inset).draw(in: bounds.insetBy(dx: inset, dy: inset))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }

    /// Octicon name and badge colour matching GitHub's PR timeline.
    static func style(for event: AlertEvent) -> (octicon: String, color: NSColor) {
        switch event {
        case .changesRequested: ("file-diff", .github(0xCF222E))
        case .approved: ("check", .github(0x1A7F37))
        case .reviewRequested: ("eye", .github(0x9A6700))
        case .mention: ("mention", .github(0x0969DA))
        case .reviewDismissed: ("x", .github(0x59636E))
        case .comment: ("comment", .github(0x59636E))
        case .commits: ("git-commit", .github(0x59636E))
        case .newPullRequest: ("git-pull-request", .github(0x1A7F37))
        case .snoozeEnded: ("clock", .github(0x8250DF))
        case .activity: ("bell", .github(0x0969DA))
        }
    }

    private static func tinted(_ image: NSImage, _ color: NSColor, size: CGFloat) -> NSImage {
        NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
            image.draw(in: rect)
            color.set()
            rect.fill(using: .sourceIn)
            return true
        }
    }
}

private extension NSColor {
    static func github(_ hex: Int) -> NSColor {
        NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}
