import CryptoKit
import Darwin
import Foundation
import GHNotifCore

enum UpdateInstaller {
    static func prepare(release: GitHubRelease, installedBundle: URL) async throws {
        let brew = brewExecutable()
        if let brew, try isHomebrewCask(brew) {
            try launchUpdater(target: installedBundle, staged: nil, brew: brew)
            return
        }

        let parent = installedBundle.deletingLastPathComponent()
        try verifyWritable(parent)

        let architecture = isAppleSilicon ? "arm64" : "x86_64"
        let zipName = "GHNotif-macos-\(architecture).zip"
        let zipURL = try release.downloadURL(named: zipName)
        let checksumURL = try release.downloadURL(named: "SHA256SUMS")
        let (archive, _) = try await URLSession.shared.download(from: zipURL)
        defer { try? FileManager.default.removeItem(at: archive) }
        let (checksumData, _) = try await URLSession.shared.data(from: checksumURL)
        let expected = try release.checksum(for: zipName, in: String(decoding: checksumData, as: UTF8.self))
        let actual = SHA256.hash(data: try Data(contentsOf: archive, options: .mappedIfSafe))
            .map { String(format: "%02x", $0) }.joined()
        guard actual == expected else {
            throw updateError("Suma kontrolna aktualizacji jest nieprawidłowa.", "The update checksum does not match.")
        }

        let staging = FileManager.default.temporaryDirectory
            .appendingPathComponent("GHNotif-update-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        do {
            let extraction = Process()
            extraction.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            extraction.arguments = ["-x", "-k", archive.path, staging.path]
            try extraction.run()
            extraction.waitUntilExit()
            guard extraction.terminationStatus == 0 else {
                throw updateError("Nie udało się rozpakować aktualizacji GHNotif.", "Could not extract the GHNotif update.")
            }

            let stagedBundle = staging.appendingPathComponent("GHNotif.app", isDirectory: true)
            guard let bundle = Bundle(url: stagedBundle),
                  bundle.bundleIdentifier == "com.michalfidor.ghnotif",
                  bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == release.tagName.trimmingCharacters(in: CharacterSet(charactersIn: "v")) else {
                throw updateError("Pobrany pakiet nie jest prawidłową aktualizacją GHNotif.", "The downloaded package is not a valid GHNotif update.")
            }
            try launchUpdater(target: installedBundle, staged: stagedBundle, brew: nil)
        } catch {
            try? FileManager.default.removeItem(at: staging)
            throw error
        }
    }

    private static var isAppleSilicon: Bool {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        return sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0 && value == 1
    }

    private static func brewExecutable() -> String? {
        ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"].first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    private static func isHomebrewCask(_ brew: String) throws -> Bool {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: brew)
        process.arguments = ["list", "--cask", "ghnotif"]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        return process.terminationStatus == 0
    }

    private static func launchUpdater(target: URL, staged: URL?, brew: String?) throws {
        let processID = String(ProcessInfo.processInfo.processIdentifier)
        if let brew {
            let command = """
            while /bin/kill -0 \(shellQuote(processID)) 2>/dev/null; do /bin/sleep 0.25; done
            \(shellQuote(brew)) update && \(shellQuote(brew)) upgrade --cask ghnotif && /usr/bin/open \(shellQuote(target.path))
            """
            let oneLineCommand = command.replacingOccurrences(of: "\n", with: "; ")
            let escaped = oneLineCommand.replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "\"", with: "\\\"")
            let terminalScript = "tell application \"Terminal\" to do script \"\(escaped)\""
            let terminal = Process()
            terminal.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            terminal.arguments = ["-e", terminalScript]
            terminal.standardOutput = FileHandle.nullDevice
            terminal.standardError = FileHandle.nullDevice
            try terminal.run()
            terminal.waitUntilExit()
            guard terminal.terminationStatus == 0 else {
                throw updateError("Nie udało się otworzyć Terminala do aktualizacji Homebrew.",
                                  "Could not open Terminal for the Homebrew update.")
            }
            return
        }

        let script = """
        pid="$1"
        target="$2"
        staged="$3"
        while kill -0 "$pid" 2>/dev/null; do sleep 0.25; done
        backup="${staged}.previous"
        if ! mv "$target" "$backup"; then
            /usr/bin/osascript -e 'display dialog "GHNotif could not replace the app bundle. Check folder permissions." buttons {"OK"}'
            exit 1
        fi
        if ! /usr/bin/ditto "$staged" "$target"; then
            rm -rf "$target"
            mv "$backup" "$target"
            /usr/bin/osascript -e 'display dialog "GHNotif could not install the update. The previous version was restored." buttons {"OK"}'
            exit 1
        fi
        rm -rf "$backup"
        rm -rf "$(dirname "$staged")"
        /usr/bin/open "$target"
        """
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: "/bin/sh")
        helper.arguments = ["-c", script, "GHNotifUpdate", processID, target.path, staged?.path ?? ""]
        helper.standardOutput = FileHandle.nullDevice
        helper.standardError = FileHandle.nullDevice
        try helper.run()
    }

    private static func verifyWritable(_ directory: URL) throws {
        let probe = directory.appendingPathComponent(".ghnotif-update-\(UUID().uuidString)")
        do {
            try Data().write(to: probe, options: .atomic)
            try FileManager.default.removeItem(at: probe)
        } catch {
            throw updateError(
                "Nie można zapisać w folderze aplikacji. Pobierz ZIP ze strony wydań i zainstaluj aktualizację ręcznie. \(error.localizedDescription)",
                "The app folder is not writable. Download the release ZIP and install the update manually. \(error.localizedDescription)"
            )
        }
    }

    private static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func updateError(_ polish: String, _ english: String) -> NSError {
        NSError(domain: "GHNotif.Update", code: 1,
                userInfo: [NSLocalizedDescriptionKey: L(polish, english)])
    }
}
