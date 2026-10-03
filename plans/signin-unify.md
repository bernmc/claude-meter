# Brief: signin-unify

## Decision
What: After merging `origin/main`, `windows/Program.cs` contains two independent sign-in mechanisms. Keep the one built in reauth-button/reauth-401 (`AuthRequiredException`, `App.AuthRequired`, the `ClaudeCli` locator + `SignIn()` + credentials-file watcher, the always-visible tray item, the flyout button, the not-found dialog, the 401 retry, env overrides, `--find-claude`). Remove the remote's duplicate (`ApiException.NeedsSignIn` field/ctor param, `App.NeedsSignIn`, `notifiedSignIn`, `StartSignIn()`, `signInTimer`, and the hidden `signInItem`/`signInSep` added at the top of `BuildMenu` with their `Opening` visibility lines). Keep exactly one thing from the remote's version: the red "!" tray icon on error (`TrayIconRenderer.Make(..., error:)` and its call in `UpdateTray`). Also reconcile README so the sign-out paragraph covers both platforms.
Why: Two sessions (macOS and Windows) implemented the same feature in parallel. The Windows-side one is the more complete and was verified on this machine today; the remote one shells `cmd /k claude auth login` (PATH-dependent, leaves a cmd window) and duplicates UI. One mechanism, no duplicate menu items or balloons.

## Files you own
- windows/Program.cs
- README.md (only the "How it works"/auth paragraph that begins "On macOS, if Claude Code gets signed out" and the Windows "#### Sign-in expired?" subsection; nothing else)
Everything else is read-only. Do not touch macos/main.swift.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to `scratch/signin-unify-builder.md`. Then build.
- Tree state: a `git merge origin/main` is IN PROGRESS. `windows/Program.cs` has six conflict hunks (`<<<<<<< HEAD` = the version to keep; `>>>>>>> origin/main` = the remote duplicate). Resolve every hunk by hand according to the Intent below (ours wins, except take the remote red "!" tray-icon pieces). Remove all markers. README.md merged cleanly and is staged; edit it per Intent. Do NOT run `git commit`, `git merge --abort`, `git checkout`, `git stash` or `git reset` — the orchestrator commits the merge after review. `git add` is also not yours to run.
- NEVER run `claude auth login`/`logout` or write the real `%USERPROFILE%\.claude\.credentials.json`; test via `CLAUDE_METER_CREDS_PATH` + `CLAUDE_METER_CLAUDE_EXE` against throwaway files in `scratch/` only. Record the real file's LastWriteTimeUtc before and after.
- build.ps1 runs via `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`. Finish with `-Install`.

## Intent
Program.cs:
1. `ApiException`: back to the single-arg constructor; delete `NeedsSignIn`. Any remote call sites passing `needsSignIn: true` must throw `AuthRequiredException` instead (they are in `ValidToken`; our version already throws it there — remove the duplicates so each failure point throws once). Keep our fixed `ErrorText` string `Claude Code sign-in has expired or been revoked.` as the flyout text; the remote's longer "Use "Sign in to Claude Code…" in the tray menu…" strings may be used as the exception *message* if you like, but the flyout must show our fixed string via `AuthRequired`.
2. `App`: delete `NeedsSignIn`, `notifiedSignIn`, `signInTimer`, `StartSignIn()`. Our `AuthRequired` + one-shot balloon stays. Make sure only ONE balloon fires on the auth-required transition.
3. `BuildMenu`: delete the remote's `signInItem`/`signInSep` and their two `Opening` lines. Our `Sign in to Claude Code…` item (after `Refresh now`, always visible) stays. Result: exactly one "Sign in to Claude Code…" in the menu.
4. Keep `TrayIconRenderer.Make(pct, showNumber, error)` and the `UpdateTray` call `error: Snap == null && ErrorText != null`. Verify the red "!" shows in the tray icon in the auth-required test.
5. Remove any duplicate `using System.Diagnostics;`.
6. Keep `CheckMarginRenderer` and the menu margin work intact.

README:
- Replace the sentence `On macOS, if Claude Code gets signed out, the meter shows a red "!" and a "Sign in to Claude Code…" button that opens Terminal running `claude auth login`.` with one covering both: on macOS it opens Terminal running `claude auth login`; on Windows the same button (tray menu or flyout) runs `claude auth login` in a console and offers the `winget install Anthropic.ClaudeCode` command if Claude Code isn't installed. Keep the Windows `#### Sign-in expired?` subsection but trim it to avoid repeating the same sentence twice; 2–3 lines is fine.

## Verification
Run (from `windows/`):
1. build.ps1 — 0 errors, 0 warnings.
2. `grep -c "Sign in to Claude Code" Program.cs` style check: the menu-item label must be constructed exactly once in `BuildMenu` (the flyout button label is separate and allowed). `NeedsSignIn`, `StartSignIn`, `signInTimer`, `notifiedSignIn` must not appear in the file.
3. Auth-required test with `scratch/fake-creds.json` (dead refresh token, see earlier runs) + `CLAUDE_METER_CLAUDE_EXE=C:\nonexistent\claude.exe`: flyout shows the fixed error string and the Sign in button; right-click tray → exactly one "Sign in to Claude Code…" item; the tray icon shows the red "!". Screenshots of the menu and of the tray icon region (zoom in).
4. build.ps1 -Install; normal usage shows; tray icon shows the number, no "!".
Screenshots: signin-unify-01-menu-single-item.png, signin-unify-02-tray-error-icon.png, signin-unify-03-normal-after-install.png
Checks JSON (`scratch/signin-unify-checks.json`), all must be true:
```json
{
  "build_ok": false,
  "single_signin_menu_item": false,
  "remote_duplicate_symbols_removed": false,
  "tray_error_icon_shows_on_auth_required": false,
  "flyout_signin_button_still_works": false,
  "normal_state_after_install": false,
  "real_credentials_file_untouched": false,
  "readme_paragraph_covers_both_platforms": false
}
```

## Report
Use the fixed report format. Nothing else.
