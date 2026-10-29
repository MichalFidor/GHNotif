# GHNotif 0.6.0

- Update notifications appear once per release on macOS and Windows, with **Update now** and **Later** actions.
- macOS installs updates through Homebrew in Terminal or downloads and verifies the matching app ZIP before replacing the bundle.
- Windows downloads and verifies the matching ZIP, replaces the app files after exit, and restarts GHNotif.
- Added an **Update now** button to the PR panel and settings.

# GHNotif 0.5.4

- New Notifications settings section: turn off single events (e.g. comments, new commits) and set quiet hours (e.g. 18:00-08:00) when no notifications are sent.
- Windows: modern toast notifications with the GitHub badge of the event; clicking a toast opens the PR. The tray balloon remains as a fallback.
- Windows: the tray icon and badges are shipped next to the exe (the icon was previously bundled inside the single-file exe and could not be loaded).
- Short dashes ("-") everywhere in the UI.

# GHNotif 0.5.3

- Notifications use GitHub-style emoji per line (✅ approved, 🔴 changes requested, 💬 comment, ⬆️ new commits, 👀 review requested, …).
- macOS: notifications show a GitHub timeline badge (Octicons) for the most important event and are grouped per PR.
- Short dashes ("-") instead of long ones in notifications and UI texts.

# GHNotif 0.5.2

- Review reminders work like iOS alarms: each time has its own on/off switch, and a newly added time is switched on. The separate "show reminders" switch is gone, so adding a time no longer leaves reminders off by mistake. Existing schedules are migrated automatically.
- The README and release notes are now in English by default; the Polish README is available as `README.pl.md`.

# GHNotif 0.5.1

- macOS: settings section names switch immediately when the language changes.

# GHNotif 0.5.0

- Review reminders: a window in the center of the screen with the PR list at chosen times on chosen days (macOS and Windows).
- Reminders can be snoozed for 15 minutes; a missed reminder appears after wake or app launch.
- UI language choice: Polish, English or system (Settings → General). Existing installations stay in Polish.
- Settings open from the gear icon in the panel header; opening settings closes the PR list.

# GHNotif 0.4.1

- Fixed repeated notifications about the same activity when PRs are auto-dismissed after your own review.

# GHNotif 0.4.0

- Notifications show the specific activity on a PR: comments, approvals, requested changes and new commits.
- New comments/reviews/commits on PRs that are already listed also trigger a notification; the same event is not repeated on refresh.
- Grouped notifications show a short summary of PRs and changes.

# GHNotif 0.3.1

- Improved PR row layout: the snooze and dismiss actions now sit next to the PR instead of being stretched to the right edge of the window.

# GHNotif 0.3.0

- Automatically mark a PR as done after you review its current commit; a new commit brings it back.
- Snooze a PR for 2 hours or until tomorrow morning, with a separate snoozed list and a notification when it returns.
- Filters to exclude repositories and labels; sort by last update, age or size of changes.
- PR rows show added/removed lines (CI status and the review decision were already shown).
- Import/export of the watch list and import of GitHub team members (the token needs organization membership access).
- Launch at login switch in the macOS and Windows settings.
- Public Homebrew tap: `brew tap MichalFidor/ghnotif && brew install --cask ghnotif`. Updates: `brew upgrade --cask ghnotif`.
- The release workflow generates a cask with package checksums and can sync it to the tap; this requires the repository secret and variable described in the README.

## Installation

- **Apple Silicon Mac:** `GHNotif-macos-arm64.zip`.
- **Intel Mac:** `GHNotif-macos-x86_64.zip`.
- **Windows:** `GHNotif-win-x64.zip` or `GHNotif-win-arm64.zip`.

Unzip the package. On macOS move `GHNotif.app` to Applications; on Windows run
`GHNotif.Windows.exe` from the extracted folder. Windows doesn't need .NET installed.
Requires macOS 15+ or Windows 10/11.

The packages are not signed with a publisher certificate or notarized. The system may warn
on first launch. Only allow it after verifying the source and the SHA-256 checksum
(`SHA256SUMS` file). On macOS you can use
System Settings → Privacy & Security → Open Anyway.

Sign in with `gh auth login` or enter a PAT in the app settings, then
add the people you want to follow. The packages contain no accounts, tokens or private data.
Settings of an existing installation are kept when the app is replaced.
