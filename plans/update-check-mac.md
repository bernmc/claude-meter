# Brief: update-check-mac

## Decision
What: Claude Meter (macOS) checks GitHub Releases for a newer version: on
startup, every 24 h (gear toggle, default on) and on demand ("Check for
updates…" in the gear and right-click menus). A newer release produces one
notification per version, a line in the popover with an "Update…" button that
either runs `git pull` + build in Terminal (when a repo checkout path is
configured) or opens the release page. Version bumped to 1.1.
Why: Bernard runs the app on two machines and wants to know when either is
behind without watching the repo.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/Info.plist
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else is read-only. windows/ is owned by another agent.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/update-check-mac-builder.md. Then build.
- Tree may be dirty: yes (the Rings style was just built in main.swift and is
  uncommitted). Build on top; do not alter the Rings work.
- Never run `claude auth`, never run `git pull`/build on Bernard's repo from the
  app during testing, never open the update script for real.

## Intent
Existing: `SignInLauncher` (writes a .command and opens it with NSWorkspace;
copy this pattern), `Notifier.post`, `UsageModel` (@Published state, 60 s
timer in `start()`), `PopoverView` footer and gear `Menu`,
`AppController.showContextMenu()`, `UsageAPI.request` (URLSession with UA),
`--once`/`--status`/preview CLI blocks.

1. Version: Info.plist CFBundleShortVersionString and CFBundleVersion → "1.1".
   `AppVersion.current` reads CFBundleShortVersionString (fallback "0").
2. `enum UpdateChecker`:
   - `struct Release { tag: String; version: String; url: URL; notes: String }`
   - `static func latest() async throws -> Release?`: GET
     `https://api.github.com/repos/bernmc/claude-meter/releases/latest` with
     headers `Accept: application/vnd.github+json`, `User-Agent: ClaudeMeter/<version>`,
     timeout 15 s. 200 → parse tag_name, html_url, body. 404 → nil (no releases
     yet = up to date). Other → throw.
   - `static func isNewer(_ a: String, than b: String) -> Bool`: strip leading
     "v", split on ".", compare numerically component by component, missing
     components = 0.
3. `UsageModel` additions: `@Published var availableUpdate: Release?`,
   `@Published var updateStatus: String?` (transient text for manual checks),
   `func checkForUpdates(manual: Bool) async`:
   - calls `UpdateChecker.latest()`; if newer than `AppVersion.current` →
     `availableUpdate = r`; if `manual` or
     `UserDefaults "lastNotifiedUpdate" != r.tag` → `Notifier.post(title: "Claude Meter \(r.version) is available", body: "You have \(AppVersion.current). Open the gauge menu to update.")`
     and store `lastNotifiedUpdate = r.tag`.
   - not newer: `availableUpdate = nil`; if manual, `updateStatus = "Up to date (v\(AppVersion.current))"`.
   - error: if manual, `updateStatus = "Couldn't check: <localizedDescription>"`; silent otherwise.
   - `updateStatus` clears itself after 6 s (Task.sleep).
   - Scheduling in `start()`: if `UserDefaults "autoUpdateCheck"` (absent =
     true): a one-shot check 10 s after launch, then a Timer every 24 h.
     Toggling the preference on/off starts/stops the timer (observe via the
     existing UserDefaults observer or a didSet; your choice, state which).
4. `enum UpdateLauncher`: `UserDefaults "updateRepoPath"` (String). If set and
   `<path>/macos/build.sh` exists → `launch()` writes
   `~/Library/Application Support/Claude Meter/update.command`:
   ```
   #!/bin/zsh -l
   cd "<path>" || exit 1
   echo "Updating Claude Meter from GitHub…"
   git pull --ff-only
   cd macos && ./build.sh --install
   echo
   echo "Done. You can close this window."
   ```
   (path substituted, quoted), chmod 0755, open with NSWorkspace. Otherwise
   `launch()` opens `availableUpdate.url` in the default browser.
   At first launch, if `updateRepoPath` is absent and the directory
   `~/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter`
   exists, set `updateRepoPath` to it (Bernard's machines; harmless elsewhere).
5. PopoverView: when `availableUpdate != nil`, a row above the footer Divider:
   Text "Update available: v<version>" (11 pt, semibold) and a small
   `.borderedProminent` Button "Update…" (label "Open release page…" when no
   repo path is usable) calling `UpdateLauncher.launch()`. When
   `updateStatus != nil`, show it in the footer in place of the "updated
   HH:MM" text, 9.5 pt tertiary.
   Gear menu: Toggle "Check for updates automatically" bound to
   `@AppStorage("autoUpdateCheck") = true`, then Button "Check for updates…"
   calling `checkForUpdates(manual: true)`, both placed just above the Launch
   at login toggle. Right-click menu: "Check for updates…" item just above
   "Quit", and when an update is available, "Update to v<version>…" above it.
6. CLI: `--check-update` prints "current <v>, latest <tag or none>, newer: yes/no"
   and exits 0 (exit 2 on network error). `--preview-update <png>` renders
   PopoverView with the fake snapshot plus a fake `availableUpdate`
   (version "9.9", url https://github.com/bernmc/claude-meter/releases) using
   the existing static-preview path.
7. README: new short section "Updates": how the check works (GitHub
   Releases, startup + daily, toggle), what Update… does on each platform,
   the `updateRepoPath`/`autoUpdateCheck` defaults keys.

## Verification
From claude-meter/macos: `./build.sh`; `--check-update` (expect "latest none"
or a tag, exit 0); `--preview-update <scratch>/update-check-mac-01-popover.png`;
`--preview-float-rings <scratch>/update-check-mac-02-rings.png` (proves Rings
untouched); `--once`; `./build.sh --install`; `pgrep`. Confirm
`defaults read au.bernard.claude-meter updateRepoPath` shows the Synology
checkout after launch (the app sets it), and `defaults read au.bernard.claude-meter`
has no `lastNotifiedUpdate` unless a newer release really exists.
Checks JSON (`scratch/update-check-mac-checks.json`), all must be true:
```json
{
  "build_clean": false,
  "version_1_1": false,
  "check_update_cli_ok": false,
  "popover_update_row_renders": false,
  "rings_untouched": false,
  "menus_have_check_and_toggle": false,
  "repo_path_autoset": false,
  "once_ran": false,
  "install_ok_app_running": false,
  "readme_updated": false,
  "no_update_or_auth_scripts_executed": false
}
```

## Report
Use the fixed report format. Nothing else.
