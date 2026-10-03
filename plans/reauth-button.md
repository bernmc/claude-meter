# Brief: reauth-button

## Decision
What: Add a "Sign in to Claude Code…" action to the Windows Claude Meter (`windows/Program.cs`) that recovers from a dead or revoked OAuth refresh token without the user reinstalling anything. The meter detects the auth-required state (no credentials file, no refresh token, or the token endpoint answering 400/401 — e.g. `{"error":"invalid_grant"}`), shows that state clearly with a clickable sign-in action in the flyout and the tray menu, and on click launches Claude Code's official `claude auth login` in a visible console window, then watches `%USERPROFILE%\.claude\.credentials.json` and refreshes as soon as it changes. If the `claude` CLI cannot be found, show a dialog with the install command.
Why: After a credential reset (security incident on another machine) the refresh token on this box was revoked server-side; the meter could only say "unable to auth" and the only fix was a terminal session. Launching the official signed CLI is the safest recovery path — the meter must not implement its own OAuth/PKCE flow. The macOS version's re-auth button is not in the repo, so this is a behavioural port, not a code port.

## Files you own
- windows/Program.cs
- README.md (only the Windows install section — add a short "Sign-in expired?" subsection; touch nothing else in the file)
Everything else is read-only.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to `scratch/reauth-button-builder.md`. Then build.
- Tree may be dirty: no.
- NEVER run `claude auth login`, `claude auth logout`, or anything that writes the real `%USERPROFILE%\.claude\.credentials.json`. The user is signed in right now and that credential must not be disturbed. Test the auth-required UI only via the env override below pointing at a throwaway file in `scratch/`.
- It is fine to `Stop-Process -Name "Claude Meter"` while testing (single-instance mutex) and to finish with `.\build.ps1 -Install` so the new build is the installed, running one.

## Intent

### 1. Auth-required detection (`UsageAPI.ValidToken` / `Creds`)
- Introduce `class AuthRequiredException : ApiException` (same message-passing constructor).
- Throw it instead of plain `ApiException` when: the credentials file is missing or has no `claudeAiOauth`; there is no refresh token; or the refresh POST to `https://platform.claude.com/v1/oauth/token` returns HTTP 400 or 401. Include the server's `error`/`error_description` in the message when present (e.g. `Refresh token not found or invalid`).
- Other failures (network down, 5xx, bad payload) keep their current behaviour and are NOT auth-required.
- Add an env override `CLAUDE_METER_CREDS_PATH`: when set and non-empty, it replaces the default credentials path (both read and write-back). Used only for testing. Keep the default exactly as today.

### 2. Model state
- On the app model that owns `ErrorText`, add `public bool AuthRequired { get; private set; }`. Set true when a refresh fails with `AuthRequiredException`; set false on any successful usage fetch.
- `ErrorText` for the auth case must be exactly: `Claude Code sign-in has expired or been revoked.`
- When `AuthRequired` flips from false to true, show one tray balloon (`ShowBalloonTip`, 10s) titled `Claude Meter can't sign in` with text `Click to sign in to Claude Code.`; `BalloonTipClicked` runs the sign-in action. Show it once per transition (re-arm when `AuthRequired` goes false).

### 3. Sign-in action (`static class ClaudeCli` or similar — one place)
- `FindClaude()` returns the first existing path from, in order:
  1. env `CLAUDE_METER_CLAUDE_EXE` (if set — test override; if set but the file does not exist, treat as not found, do not fall through)
  2. `where.exe claude` — first line ending in `.exe` or `.cmd`
  3. `%LOCALAPPDATA%\Microsoft\WinGet\Links\claude.exe`
  4. `%USERPROFILE%\.local\bin\claude.exe`
  5. `%APPDATA%\npm\claude.cmd`
  Return null if none.
- `SignIn()`:
  - If `FindClaude()` is null → show the not-found dialog (section 5) and return.
  - Else start the process with `UseShellExecute = true`, `FileName = <path>`, `Arguments = "auth login"` so it gets its own visible console window (the user has to see the URL / press Enter there). Do not redirect its streams. Do not wait synchronously on the UI thread.
  - Start a watcher: poll the credentials path's `LastWriteTimeUtc` every 2 s for up to 10 minutes; on change, or when the launched process exits with code 0, trigger the model's normal refresh. Guard against two concurrent sign-ins (ignore the click if one is in progress).
- Add a hidden CLI flag `--find-claude` to the exe: prints the resolved path (or `not found`) to stdout and exits 0 without starting the UI or taking the single-instance mutex. Used for verification.

### 4. Where the action appears
- Tray context menu: a new item `Sign in to Claude Code…` placed directly after `Refresh now`. Always present (users may want to re-sign-in pre-emptively).
- Flyout/popover: when `AuthRequired` is true, under the error text render a clickable button labelled `Sign in to Claude Code…` (a real WinForms `Button`/`LinkLabel` shown/hidden with the state, or a hit-tested painted button — your choice, but it must be clickable with the mouse and must not appear when `AuthRequired` is false). Match the existing Theme colours/fonts.
- Floating desktop gauge: no new control; it already shows `ErrorText`/`Claude Meter…` — leave it.

### 5. Not-found dialog
- Title: `Claude Code not found`
- Body (exact):
  ```
  Claude Meter reads Claude Code's sign-in, but the claude command isn't installed on this PC.

  Install it from PowerShell:
  winget install Anthropic.ClaudeCode

  Then click "Sign in to Claude Code…" again.
  ```
- Buttons: `Copy command` (puts `winget install Anthropic.ClaudeCode` on the clipboard), `Open install page` (opens https://code.claude.com/docs/en/setup in the default browser), `Close`.

### 6. README (Windows install section only)
Add a subsection `#### Sign-in expired?` after the Windows install commands, 3–5 lines: the meter uses Claude Code's sign-in; if it shows "sign-in has expired or been revoked", click **Sign in to Claude Code…** (tray menu or the flyout) — it runs `claude auth login` for you; if Claude Code isn't installed, run `winget install Anthropic.ClaudeCode` first. No other README edits.

### Non-goals
- No OAuth/PKCE implementation inside the meter. No new HTTP endpoints beyond what exists.
- No changes to macOS code, build.ps1, csproj, or the release workflow.
- No new settings.

## Verification
Run (from `windows/`, PowerShell):
1. `.\build.ps1` — must compile with 0 errors (warnings acceptable; list any new ones).
2. `& ".\build\Claude Meter.exe" --find-claude` — must print a path containing `claude` (this machine has `%LOCALAPPDATA%\Microsoft\WinGet\Links\claude.exe`).
3. `$env:CLAUDE_METER_CLAUDE_EXE='C:\nonexistent\claude.exe'; & ".\build\Claude Meter.exe" --find-claude` — must print `not found`.
4. Auth-required UI: create `scratch/fake-creds.json` with `{"claudeAiOauth":{"accessToken":"x","refreshToken":"dead","expiresAt":0,"subscriptionType":"max"}}`; `Stop-Process -Name "Claude Meter" -ErrorAction SilentlyContinue`; launch the built exe with `CLAUDE_METER_CREDS_PATH` pointing at that file and `CLAUDE_METER_CLAUDE_EXE='C:\nonexistent\claude.exe'`. The real token endpoint will answer 400 invalid_grant for `dead`. Open the flyout (click the tray icon or whatever the code exposes), screenshot the error text + Sign in button; click it; screenshot the not-found dialog. (Capture with a PowerShell `System.Drawing` screen grab of the window bounds, or the whole screen if simpler.) Then close it.
5. Normal state: stop the test instance, run `.\build.ps1 -Install` (installs and relaunches with the REAL credentials, no env overrides). Confirm the running process exists and the flyout shows usage, not an error; screenshot.
Measure: build exit code; `--find-claude` outputs; that `AuthRequired` UI appears only with the dead-token file.
Screenshots (named, in `scratch/`): reauth-button-01-auth-required-flyout.png, reauth-button-02-not-found-dialog.png, reauth-button-03-normal-after-install.png
Checks JSON (`scratch/reauth-button-checks.json`), all must be true:
```json
{
  "build_ok": false,
  "find_claude_resolves_real_path": false,
  "find_claude_override_not_found": false,
  "auth_required_shows_signin_button": false,
  "not_found_dialog_shows": false,
  "normal_state_no_error_after_install": false,
  "real_credentials_file_untouched": false,
  "readme_windows_subsection_added": false
}
```
`real_credentials_file_untouched`: record `(Get-Item "$env:USERPROFILE\.claude\.credentials.json").LastWriteTimeUtc` before you start and after you finish; true only if identical.

## Report
Use the fixed report format. Nothing else.
