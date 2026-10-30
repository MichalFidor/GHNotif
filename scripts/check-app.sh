#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
work="$(mktemp -d "${TMPDIR:-/tmp}/ghnotif-checks.XXXXXX")"
trap 'rm -rf "$work"' EXIT

if git grep -n '\.module' -- Sources/GHNotif | grep -v '^Sources/GHNotif/AppResources.swift:'; then
    echo "Load app resources through AppResources.bundle." >&2
    exit 1
fi

swiftc -swift-version 5 -emit-library -emit-module -module-name GHNotifCore \
    Sources/GHNotifCore/*.swift \
    -emit-module-path "$work/GHNotifCore.swiftmodule" -o "$work/libGHNotifCore.dylib"
swiftc -swift-version 5 -I "$work" -L "$work" -lGHNotifCore \
    -Xlinker -rpath -Xlinker "$work" \
    Sources/GHNotif/AppModel.swift Sources/GHNotif/MenuBarIcon.swift \
    Sources/GHNotif/NotificationIcon.swift Tests/AppChecks/main.swift -o "$work/AppChecks"
"$work/AppChecks" "$PWD/Sources/GHNotif/Resources"
