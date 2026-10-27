#!/usr/bin/env bash
# Builds GHNotif.app into ./build. With --install, copies it to ~/Applications and launches it.
set -euo pipefail

cd "$(dirname "$0")/.."
APP="build/GHNotif.app"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/GHNotif"
VERSION="$(tr -d '\r\n' < VERSION)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/GHNotif"
cp assets/GHNotif.icns "$APP/Contents/Resources/GHNotif.icns"
# SwiftPM resolves Bundle.module from the app's resource directory.
cp -R "$(dirname "$BIN")/GHNotif_GHNotif.bundle" "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>com.michalfidor.ghnotif</string>
    <key>CFBundleName</key><string>GHNotif</string>
    <key>CFBundleDisplayName</key><string>GHNotif</string>
    <key>CFBundleExecutable</key><string>GHNotif</string>
    <key>CFBundleIconFile</key><string>GHNotif.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"

codesign --force --sign - "$APP/Contents/Resources/GHNotif_GHNotif.bundle"
codesign --force --sign - "$APP"
echo "Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    mkdir -p "$HOME/Applications"
    osascript -e 'quit app "GHNotif"' 2>/dev/null || true
    rm -rf "$HOME/Applications/GHNotif.app"
    cp -R "$APP" "$HOME/Applications/"
    open "$HOME/Applications/GHNotif.app"
    echo "Installed to ~/Applications/GHNotif.app"
fi
