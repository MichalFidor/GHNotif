# GHNotif

<img src="assets/GHNotif.png" alt="GHNotif logo" width="96">

**English** | [Polski](README.pl.md)

A macOS menu bar / Windows system tray app that shows open (non-draft) pull requests from the people you follow, across every repository your GitHub token can see. Each platform has its own native UI.

## Installing on macOS

**Homebrew (recommended):**

```bash
brew tap MichalFidor/ghnotif
brew install --cask ghnotif
# update:
brew upgrade --cask ghnotif
```

Prebuilt packages for Apple Silicon and Intel are also available in [GitHub Releases](https://github.com/MichalFidor/GHNotif/releases/latest).
Unzip and move `GHNotif.app` to Applications. Swift is not required.
The Homebrew packages are not notarized or signed with a developer certificate either, so macOS may block
the first launch. After verifying the source and the SHA-256 checksum, use
System Settings → Privacy & Security → Open Anyway.

Building from source:

```bash
./scripts/build-app.sh            # builds build/GHNotif.app
./scripts/build-app.sh --install  # copies it to ~/Applications and launches it
```

Requires macOS 15+ and Swift 6 (Command Line Tools are enough). Launch at login can be enabled in GHNotif → Settings → General.

## Installing on Windows

Requires Windows 10/11. The portable build bundles .NET, so nothing else needs to be installed.

1. Download `GHNotif-win-x64.zip` (typical PC) or `GHNotif-win-arm64.zip` from [GitHub Releases](https://github.com/MichalFidor/GHNotif/releases/latest).
2. Extract the whole ZIP to a permanent folder and run `GHNotif.Windows.exe`; keep `GHNotif.ico` next to it.
3. Click the icon next to the clock (it may be hidden under the arrow). Right-click for settings, refresh or quit.
4. Add people in the settings and either run `gh auth login` or save your own PAT.

The app is not signed with a publisher certificate, so Windows SmartScreen may show a warning. Only run builds from a trusted source.
Closing the panel hides it to the tray. Launch at login can be enabled in the app settings.

Building on Windows (requires the .NET 10 SDK):

```powershell
./scripts/build-windows.ps1                    # build/GHNotif-win-x64.zip
./scripts/build-windows.ps1 -Runtime win-arm64 # build/GHNotif-win-arm64.zip
```

The Windows version has a light panel grouped by repository, with avatars, review and CI state, tooltips, local dismissing and tray notifications (their visibility depends on Windows settings). The PR count is shown in the panel and in the tray icon tooltip. The macOS UI follows the system appearance.

## Authentication

- By default the token from `gh auth login` is used (the app runs `gh auth token`).
- Optionally, save a PAT in the settings (stored in the Keychain). It takes precedence over `gh`.
- On Windows the PAT is encrypted with DPAPI for the current user and never written to the JSON config. `gh` must be on `PATH`; restart GHNotif after installing it.
- Organizations with SAML SSO: if the token is not authorized for them, GitHub hides their PRs. The app then shows a warning with a link to authorize the token.

## Configuration

Settings (the gear icon in the panel header) have a sidebar on the left (like System Settings) and show the selected section on the right:

- **People** - add people to follow (logins are verified on GitHub); the list is collapsed by default, with a counter and a filter. You can import/export the list as JSON or import members of a GitHub team (requires organization membership access: `read:org` for classic tokens or `Members: read` for fine-grained ones).
- **Search** - mentions and review requests (see below), extra qualifiers (e.g. `org:my-org`) and excluded repositories and labels.
- **Reminders** - days and times of the review reminder window (see below).
- **Notifications** - notifications on/off, which events notify, quiet hours (see below).
- **General** - language, refresh interval, sorting, auto-dismiss after your own review, launch at login, version and updates.
- **Token** - token source and an optional PAT.

A new installation starts with an empty list of people.

## Mentions and review requests

PRs from the people you follow come first, but the list can also include:

- **mentions** (`mentions:@me`, on by default) - open PRs by any author where someone mentioned you; marked with a purple `@`,
- **review requests** (`user-review-requested:@me`, off by default) - only requests addressed to you personally, not to your team; marked with a person icon with a question mark.

Mentions include PRs by any author (including your own). Extra scope qualifiers apply to both searches. When you turn an option on, existing PRs don't trigger notifications - only new ones do.

The configuration is stored in `~/Library/Application Support/GHNotif/config.json`.
On Windows: `%LOCALAPPDATA%/GHNotif/config.json`; dismissed, snoozed and seen PRs and collapsed repositories are kept in `state.json`, and the encrypted PAT in `token.dat`. Don't add these files to a repository and don't share your token.
Settings are independent per platform and are not synchronized.

## Dismissed PRs

The check mark button on the right of a PR marks it as done. The PR disappears from the needs-attention list and the counter. It doesn't come back after a refresh or restart, but it **returns automatically (with a notification) when there is new activity**:

- a new commit (HEAD change, including force-pushes),
- a new comment or review from someone other than you (bot comments are ignored).

Changes to the title, labels or the CI status alone don't bring a PR back.

A notification also appears for new activity on a PR that is already on the list and says who did what, e.g. `✓ anna - approved`, `piotr - changes requested`, `bob - comment` or `dave - pushed commits`. The same event is not repeated on later refreshes. For commits the author of the latest commit is shown (the GitHub API doesn't say who pushed). New PRs are described as "New PR by …", "Your review was requested" or "You were mentioned".

Optionally a PR is dismissed automatically after you submit a review of its current commit, as long as nobody else has been active on it since. A new commit or another person's comment/review brings it back. Later refreshes without changes don't repeat the notification about the same comment. This can be turned off in the settings.

## Snoozing and sorting

The clock next to a PR snoozes it for 2 hours or until tomorrow morning. Snoozed PRs are listed separately and come back when the time is up (with a notification if notifications are enabled). Repositories can be excluded as `owner/repo`, labels by name. Sorting: recently updated, oldest first or largest changes. Each PR also shows added/removed lines, CI status and the review decision.

Importing/exporting the watch list transfers logins only - never tokens or private settings. Importing team members uses the GitHub endpoint for reading organization members.

The check mark button in the header switches to the list of dismissed, still open PRs. The arrow next to a PR restores it to the main list. This is purely local app state, not a review or closing the PR on GitHub. IDs together with the dismissal time and the latest commit are kept in macOS preferences or the Windows state file, outside the repository.

## Repository groups

Clicking a repository header collapses or expands its PRs. The number on the right is the PR count in that group (on the main list: those needing attention). The collapsed state is remembered across launches. When collapsed, the panel shrinks and stays attached to the menu bar on macOS or to the tray on Windows.

## Review reminders

In the **Reminders** settings section you can schedule a window that appears in the center of the screen at fixed times, e.g. every weekday at 9:00. It shows the current list of PRs needing attention (grouped by repository) and appears even when the list is empty.

- Choose the weekdays and any number of times per day.
- Each time has its own switch, like an iOS alarm: turn it off without deleting it. A newly added time is switched on.
- **Remind me in 15 min** snoozes the window; **Done** closes it until the next time.
- If the computer was asleep or the app wasn't running, a missed reminder from the same day appears after wake or launch (several missed ones are merged into one).
- Changing the schedule doesn't immediately trigger times that have already passed.

## Language

The UI is available in English and Polish. In **Settings → General → Language** choose *System* (Polish when Polish is the first preferred system language, English otherwise), *Polski* or *English*. New installations follow the system language; existing configurations without this setting stay in Polish.

## Icons and tooltips

Hover over an icon to see what it means. A purple `@` marks a mention, a person icon with a question mark a review request for you. A green check mark means an approved review, a speech bubble/exclamation mark requested changes, an eye a required review. CI dot: green = success, red = failure, yellow = in progress. The separate check mark at the end of a row marks the PR as locally done.

The logo shows a PR branch with a notification dot. The colored app icons (`.icns`/`.ico`), the menu bar logo and its monochrome variant are generated from a shared source:

```bash
swift scripts/generate-icons.swift # macOS
```

## Notifications

Each line says who did what, with a simple text symbol (the GitHub icon of the most important event is shown as the notification badge): ✓ approved, ✗ changes requested, • comment,
↑ new commits, ◉ review requested, @ mention, ⊘ review dismissed, + new PR, ◷ snooze ended.
Notifications also show a GitHub timeline badge for the most important event: on macOS as an attachment
(notifications are grouped per PR), on Windows as a toast icon (clicking the toast opens the PR; the tray balloon
is a fallback). Badges use [Octicons](https://github.com/primer/octicons) (MIT, © GitHub Inc.,
see `Sources/GHNotif/Resources/Octicons-LICENSE.txt`); regenerate them with
`swift scripts/generate-notification-badges.swift`.

In Settings → Notifications you can turn off single events (e.g. comments) and set **quiet hours**
(e.g. 18:00-08:00, may span midnight). Muted events and anything during quiet hours are not sent - not even
later - but the PR list keeps updating.

## macOS notifications

In Settings → Notifications choose **Send test**. The app asks for permission and shows the result and the alert state.
The API accepting a notification doesn't guarantee a banner: in System Settings → Notifications → GHNotif
enable banners/sound and check Focus mode. Automated tests verify that a new PR and a returning dismissed PR
trigger a notification; the look of the system banner has to be checked on a logged-in Mac.

## Releases and updates

The version number comes from `VERSION`. Both apps check for the latest stable release
at launch and every 6 hours; you can also use **Check for updates** in the settings.
When a newer version exists, the app shows a panel banner and sends one system notification
per version (macOS requires GHNotif notification permission). Choose **Update now** or **Later**;
the panel also has an **Update now** button.
The check uses the public API, without a token and without sending your watch list.
Update check errors are shown in the settings and don't interrupt fetching PRs.

macOS Homebrew installations run `brew update` and `brew upgrade --cask ghnotif` in Terminal.
Other macOS installations download the matching ZIP, verify its SHA-256 checksum, replace the
app bundle and relaunch. Windows downloads and verifies the matching ZIP, replaces the app files
after exit, and restarts GHNotif. Installations in protected folders may still need a manual
update with administrator permissions. Settings, tokens and local state stay outside the package.

Publishing: update `VERSION` and `RELEASE-NOTES.md`, push the commit and a `vX.Y.Z` tag
matching `VERSION`. The **Release** workflow runs the macOS/Windows tests, builds four
packages (macOS arm64/x86_64, Windows x64/arm64) and only then publishes the release
with `SHA256SUMS` checksums. The macOS build itself is ad-hoc signed, without notarization.
The workflow also attaches the `ghnotif.rb` cask to the release. Syncing the tap automatically
on later releases requires the repository variable `HOMEBREW_TAP_ENABLED=true` and the secret
`HOMEBREW_TAP_TOKEN` with write access to `MichalFidor/homebrew-ghnotif`.

Publishing runs in the `release` environment, which requires approval from the repository owner
(Actions → waiting run → **Review deployments**). The `main` branch and `v*` tags are
protected by rulesets. Only a repository administrator can bypass them.

## How it works

Instead of one query joined with `OR` (GitHub search allows at most 5 logical operators), authors are split into batches of 10; repeated `author:` qualifiers act as OR. Each batch is a GraphQL query `is:pr is:open draft:false author:… author:…`, and the results are merged and deduplicated. Mentions and review requests are separate queries; a PR found by several of them carries all their markers.

In the macOS menu bar the logo and the counter are drawn as a single image, because a `MenuBarExtra` label made of several views only shows some of them.

## Tests

```bash
swift run CoreChecks
bash scripts/check-app.sh
```

The tests are a plain executable, because Command Line Tools include neither XCTest nor the Swift Testing macros.
The app tests cover dismissing, restoring, PRs returning after new activity, migrating old state, collapsing repositories, state persistence and notifications. They run with an isolated configuration and fake GitHub and notification services.

Windows:

```powershell
dotnet run --project windows/GHNotif.Checks -c Release
dotnet run --project windows/GHNotif.Windows.Checks -c Release # Windows only
```

The core tests also run on macOS/Linux: queries, pagination, SSO, API errors, configuration, dismissal persistence, returning after new activity, state migration, restoring and notifications. The Windows tests additionally check the WPF layout, dismiss/restore/collapse clicks, settings and DPAPI encryption, without real tokens or accounts.
The Windows workflow builds both portable variants and runs these tests on a Windows runner. A cross-build on a Mac doesn't replace testing the UI on Windows.
