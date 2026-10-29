import AppKit

// Run from the repository root: swift scripts/generate-notification-badges.swift
// Renders GitHub timeline-style badges (Octicon on a coloured circle) used by notifications on macOS and Windows.
// Names match AlertEvent raw values (Swift) and camelCase AlertEvent names (C#).
let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let octicons = root.appendingPathComponent("assets/octicons")
let resources = root.appendingPathComponent("Sources/GHNotif/Resources")

let badges: [(event: String, octicon: String, color: Int)] = [
    ("changesRequested", "file-diff", 0xCF222E),
    ("approved", "check", 0x1A7F37),
    ("reviewRequested", "eye", 0x9A6700),
    ("mention", "mention", 0x0969DA),
    ("reviewDismissed", "x", 0x59636E),
    ("comment", "comment", 0x59636E),
    ("commits", "git-commit", 0x59636E),
    ("newPullRequest", "git-pull-request", 0x1A7F37),
    ("snoozeEnded", "clock", 0x8250DF),
    ("activity", "bell", 0x0969DA),
]

func color(_ hex: Int) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

func render(_ glyph: NSImage, background: NSColor, pixels: Int) -> Data {
    let size = CGFloat(pixels)
    let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
        hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    let bounds = NSRect(x: 0, y: 0, width: size, height: size)
    background.setFill()
    NSBezierPath(ovalIn: bounds).fill()
    let inset = size * 0.25
    let white = NSImage(size: NSSize(width: size - 2 * inset, height: size - 2 * inset), flipped: false) { rect in
        glyph.draw(in: rect)
        NSColor.white.set()
        rect.fill(using: .sourceIn)
        return true
    }
    white.draw(in: bounds.insetBy(dx: inset, dy: inset))
    NSGraphicsContext.restoreGraphicsState()
    return bitmap.representation(using: .png, properties: [:])!
}

for badge in badges {
    let svg = octicons.appendingPathComponent("\(badge.octicon).svg")
    guard let glyph = NSImage(contentsOf: svg) else { fatalError("Missing \(svg.path)") }
    let output = resources.appendingPathComponent("badge-\(badge.event).png")
    try render(glyph, background: color(badge.color), pixels: 128).write(to: output)
    print("Wrote \(output.lastPathComponent)")
}
