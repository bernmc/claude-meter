# Brief: signin-button-mac

## Decision
What: When Claude Meter (macOS) can't fetch because Claude Code is signed out, the
popover shows a "Sign in to Claude Code…" button (and the right-click menu shows the
same item). Clicking it opens a Terminal window running `claude auth login`. The
error text is corrected to name `claude auth login` (plain `claude` no longer
prompts for sign-in). After launching sign-in, the app polls fast until it recovers.
Why: Bernard hit the sign-in error twice and plain `claude` did nothing. One click
beats remembering a command. Terminal (not a hidden process) because the login flow
may ask the user to paste a code.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else is read-only. windows/ is owned by another agent running now: do not touch it.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/signin-button-mac-visual-iterator.md. Then build.
- Tree may be dirty: yes (windows/Program.cs is being edited in parallel). Build on top.
- NEVER run `claude auth login`, `claude auth logout`, or anything that changes Claude
  Code's sign-in or the keychain. Never open the sign-in script for real during testing.

## Intent
Existing pieces in main.swift: `APIError` (cases noCredentials, reauthNeeded, ... with
`needsSignIn` and `errorDescription`), `UsageModel.refresh()` (catch block sets
`errorText`, posts a one-time notification when `needsSignIn`), `PopoverView` (red
error Text), `AppController.updateStatusButton()` (red "!"), `showContextMenu()`
(right-click NSMenu), the 60 s poll Timer in `UsageModel.start()`, and the
`--once` / `--status` CLI blocks at the entry point.

1. Error text (exact strings):
   - `.noCredentials`: "Claude Code isn't signed in on this Mac. Click Sign in below, or run `claude auth login` in Terminal."
   - `.reauthNeeded`: "Claude Code is signed out. Click Sign in below, or run `claude auth login` in Terminal."
2. `UsageModel` gains `@Published var needsSignIn = false`: set true in the catch
   block when the error is an `APIError` with `needsSignIn`; set false on any
   successful fetch.
3. New `enum SignInLauncher`:
   - `static func scriptURL() -> URL`: `~/Library/Application Support/Claude Meter/sign-in.command`.
   - `static func writeScript() throws -> URL`: writes exactly
     ```
     #!/bin/zsh -l
     echo "Signing in to Claude Code for Claude Meter…"
     claude auth login
     echo
     echo "Done. You can close this window."
     ```
     then sets POSIX permissions 0o755. Returns the URL.
   - `static func launch()`: `writeScript()` then `NSWorkspace.shared.open(url)`
     (Terminal runs .command files; no Apple Events permission needed). Swallow errors.
4. `UsageModel.startSignIn()`: calls `SignInLauncher.launch()`, sets
   `@Published var signInLaunchedAt: Date?` = now, and starts a 5 s repeating fast
   poll that calls `refresh()` until `needsSignIn` becomes false or 3 minutes pass;
   then invalidates it. The normal 60 s timer is unchanged.
5. PopoverView, under the red error Text, only when `model.needsSignIn`:
   a `Button` styled `.borderedProminent`, `.controlSize(.small)`:
   - label "Sign in to Claude Code…" normally;
   - label "Waiting for sign-in…" and disabled while `signInLaunchedAt` is within
     the last 3 minutes.
   Action: `model.startSignIn()`.
6. Right-click menu (`showContextMenu`): when `model.needsSignIn`, insert
   "Sign in to Claude Code…" as the FIRST item, then a separator; action calls
   `model.startSignIn()`.
7. CLI mode `--preview-signin <out.png>` (for agent verification only; add beside
   `--once`): construct a UsageModel with `errorText` = the `.reauthNeeded` string
   and `needsSignIn = true`, render `PopoverView` for it with `ImageRenderer`
   (scale 2, opaque window-background colour behind it) to the given PNG path,
   exit 0. Must not touch the network or keychain. A second flag
   `--preview-signin-waiting <out.png>` renders the same with `signInLaunchedAt = now`.
8. CLI mode `--signin-script-dryrun`: calls `SignInLauncher.writeScript()`, prints
   the path and the file contents to stdout, does NOT open it, exit 0.
9. README.md: in the macOS part of "How it works" or near the Uninstall section,
   add one sentence: if Claude Code gets signed out, the meter shows a red "!" and a
   "Sign in to Claude Code…" button that opens Terminal running `claude auth login`.
   Also change any README text telling users to run plain `claude` to sign in so it
   says `claude auth login`.

## Verification
Run (from claude-meter/macos):
1. `./build.sh` compiles clean.
2. `"./build/Claude Meter.app/Contents/MacOS/Claude Meter" --preview-signin <scratch>/signin-button-mac-01-button.png`
3. `... --preview-signin-waiting <scratch>/signin-button-mac-02-waiting.png`
4. `... --signin-script-dryrun`: output shows the path ending `sign-in.command` and
   the exact script; `stat -f %Lp` on it prints 755.
5. `... --once` still prints live usage (proves normal path unaffected; Claude Code
   is currently signed in).
6. `./build.sh --install`; `pgrep -x "Claude Meter"` shows it running.
Iterate on the PNGs at least three times against this reference: the existing
popover look (title "Claude usage", red error text, divider, footer icons) with the
button sitting left-aligned directly under the red text, 8 pt gap above, same 14 pt
side padding as the text, not stretched full width. Fix anything clipped, misaligned
or crowding the divider.
Screenshots (named, in the scratch dir): signin-button-mac-01-button.png,
signin-button-mac-02-waiting.png
Checks JSON (`scratch/signin-button-mac-checks.json`), all must be true:
```json
{
  "build_clean": false,
  "error_strings_exact": false,
  "button_only_when_needs_signin": false,
  "waiting_state_disabled": false,
  "context_menu_item_first_when_needed": false,
  "script_exact_and_0755": false,
  "fast_poll_stops_on_success_or_3min": false,
  "once_still_works": false,
  "install_ok_app_running": false,
  "readme_updated": false,
  "no_auth_commands_run": false
}
```

## Report
Use the fixed report format. Nothing else.
