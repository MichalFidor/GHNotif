#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 4 ]]; then
    echo "Usage: $0 VERSION ARM64_SHA256 X86_64_SHA256 OUTPUT" >&2
    exit 2
fi

version="$1"
arm_sha="$2"
intel_sha="$3"
output="$4"

for checksum in "$arm_sha" "$intel_sha"; do
    [[ "$checksum" =~ ^[0-9a-f]{64}$ ]] || {
        echo "Invalid SHA-256 checksum" >&2
        exit 2
    }
done

cat > "$output" <<CASK
cask "ghnotif" do
  version "$version"
  arch arm: "arm64", intel: "x86_64"
  sha256 arm: "$arm_sha", intel: "$intel_sha"

  url "https://github.com/MichalFidor/GHNotif/releases/download/v#{version}/GHNotif-macos-#{arch}.zip"
  name "GHNotif"
  desc "GitHub pull request review notifications"
  homepage "https://github.com/MichalFidor/GHNotif"

  livecheck do
    url :url
    strategy :github_latest
  end

  auto_updates false
  depends_on macos: ">= :sequoia"

  app "GHNotif.app"
  uninstall quit: "com.michalfidor.ghnotif"

  zap trash: [
    "~/Library/Application Support/GHNotif",
    "~/Library/Preferences/com.michalfidor.ghnotif.plist",
  ]
end
CASK
