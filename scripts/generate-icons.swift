import AppKit

// Run from the repository root: swift scripts/generate-icons.swift
let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let assets = root.appendingPathComponent("assets")
let resources = root.appendingPathComponent("Sources/GHNotif/Resources")
let iconset = root.appendingPathComponent("build/GHNotif.iconset")
for directory in [assets, resources, iconset] {
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
}

func render(_ pixels: Int, template: Bool = false) -> Data {
    let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
        samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    let context = NSGraphicsContext.current!.cgContext
    context.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)
    if !template {
        NSColor(srgbRed: 0.25, green: 0.29, blue: 0.77, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: 32, y: 32, width: 960, height: 960),
                     xRadius: 220, yRadius: 220).fill()
    }
    let ink = template ? NSColor.black : NSColor.white
    ink.setStroke()
    let branch = NSBezierPath()
    branch.lineWidth = template ? 85 : 65
    branch.lineCapStyle = .round
    branch.move(to: NSPoint(x: 345, y: 275))
    branch.line(to: NSPoint(x: 345, y: 730))
    branch.move(to: NSPoint(x: 345, y: 360))
    branch.curve(to: NSPoint(x: 680, y: 620),
                 controlPoint1: NSPoint(x: 345, y: 560), controlPoint2: NSPoint(x: 680, y: 380))
    branch.line(to: NSPoint(x: 680, y: 700))
    branch.stroke()
    for (x, y) in [(345.0, 250.0), (345.0, 750.0), (680.0, 720.0)] {
        let circle = NSBezierPath(ovalIn: NSRect(x: x - 80, y: y - 80, width: 160, height: 160))
        if template { context.setBlendMode(.clear) }
        NSColor(srgbRed: 0.25, green: 0.29, blue: 0.77, alpha: 1).setFill()
        circle.fill()
        context.setBlendMode(.normal)
        ink.setStroke()
        circle.lineWidth = template ? 65 : 48
        circle.stroke()
    }
    (template ? NSColor.black : NSColor(srgbRed: 1, green: 0.68, blue: 0.22, alpha: 1)).setFill()
    NSBezierPath(ovalIn: NSRect(x: 755, y: 765, width: 150, height: 150)).fill()
    NSGraphicsContext.restoreGraphicsState()
    return bitmap.representation(using: .png, properties: [:])!
}

try render(1024).write(to: assets.appendingPathComponent("GHNotif.png"))
for size in [16, 32, 128, 256, 512] {
    try render(size).write(to: iconset.appendingPathComponent("icon_\(size)x\(size).png"))
    try render(size * 2).write(to: iconset.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
for (size, filename) in [(18, "TrayMark.png"), (36, "TrayMark@2x.png")] {
    try render(size, template: true).write(to: resources.appendingPathComponent(filename))
}
for (size, filename) in [(20, "TrayLogo.png"), (40, "TrayLogo@2x.png")] {
    try render(size).write(to: resources.appendingPathComponent(filename))
}

func littleEndian<T: FixedWidthInteger>(_ value: T) -> Data {
    var value = value.littleEndian
    return withUnsafeBytes(of: &value) { Data($0) }
}
let sizes = [16, 32, 48, 256]
let images = sizes.map { render($0) }
var ico = littleEndian(UInt16(0)) + littleEndian(UInt16(1)) + littleEndian(UInt16(sizes.count))
var offset = UInt32(6 + sizes.count * 16)
for (size, image) in zip(sizes, images) {
    ico.append(contentsOf: [size == 256 ? 0 : UInt8(size), size == 256 ? 0 : UInt8(size), 0, 0])
    ico.append(littleEndian(UInt16(1)))
    ico.append(littleEndian(UInt16(32)))
    ico.append(littleEndian(UInt32(image.count)))
    ico.append(littleEndian(offset))
    offset += UInt32(image.count)
}
images.forEach { ico.append($0) }
try ico.write(to: assets.appendingPathComponent("GHNotif.ico"))

let process = Process()
process.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
process.arguments = ["-c", "icns", iconset.path, "-o", assets.appendingPathComponent("GHNotif.icns").path]
try process.run()
process.waitUntilExit()
guard process.terminationStatus == 0 else { fatalError("iconutil failed") }
print("Generated macOS/Windows app icons and menu bar template.")
