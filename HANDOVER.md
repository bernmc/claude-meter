# Handover — claude-meter, two machines, two platforms

Read this first on the Mac, then `STATUS.md`, then `PLAN.md`. Both are kept current by the
orchestrating session on whichever machine last worked; they are the authority for what is open,
agreed, and done. Written 2026-10-03 on the Windows box (Bernard's Intel N150 laptop).

## How to resume on either machine

1. `git pull` before doing anything. The other machine may have pushed.
2. Read `STATUS.md` (open / agreed / done), then `PLAN.md` (decisions table, one row per item,
   brief linked under `plans/<slug>.md`).
3. Work through `/orchestrate:orchestrate`: brief → builder → review screenshots + checks →
   commit → `(agreed <hash>)` in STATUS → push → move to Done.
4. **Before pushing, `git fetch` and look at `origin/main`.** On 2026-10-03 both machines
   implemented the same feature in parallel and it cost a manual merge (see below).

## Parity rule (the thing to remember)

**Every feature or fix ships for both macOS (`macos/main.swift`) and Windows
(`windows/Program.cs`) in the same item.** If one side is deferred, open a STATUS item for it
immediately with the slug suffixed `-mac` or `-win` so it cannot be forgotten. Current parity gaps
are listed under Open in `STATUS.md`.

## What happened on Windows, 2026-10-03

Context: after the hack attempt on the Mac mini, all Claude credentials were reset. The Windows
box was holding a refresh token that the server had revoked (`invalid_grant`), so the meter was
stuck on "unable to auth" with no in-app recovery, and Claude Code CLI wasn't installed here.

Done on this machine (all committed, see `git log`):
- Installed .NET 8 SDK and the meter (`build.ps1 -Install`), added a Startup-folder shortcut.
- Installed Claude Code CLI via `winget install Anthropic.ClaudeCode` (signature verified,
  "Anthropic, PBC") and signed in fresh. `%USERPROFILE%\.claude\.credentials.json` is live.
- **reauth-button** (4a06a3c): "Sign in to Claude Code…" in the tray menu (always visible) and as a
  button in the flyout's error state; launches `claude auth login` in a console via a locator
  (env `CLAUDE_METER_CLAUDE_EXE` → `where.exe` → WinGet Links → `~/.local/bin` → npm), watches the
  credentials file and refreshes; "Claude Code not found" dialog with the winget command if the
  CLI is missing. Test hooks: env `CLAUDE_METER_CREDS_PATH`, flag `--find-claude`.
- **reauth-401** (ac32500): a usage-endpoint 401 forces one token refresh and retries; a second
  401 or a 400/401 refresh raises `AuthRequiredException` so the sign-in UI appears.
- **menu-check-margin** (2377a63): context menus get a real check column with a DPI-scaled tick
  (`CheckMarginRenderer`); check marks no longer overlap item text at 250% scaling.
- **signin-unify** (in progress at time of writing; see STATUS): on merging `origin/main`, the
  Mac session's commit 5211f12 had *also* added a Windows sign-in path (`NeedsSignIn`,
  `StartSignIn()` via `cmd /k claude auth login`, hidden tray item). Decision: keep the Windows
  implementation built here, delete the remote duplicate, keep the remote's red "!" tray icon on
  error. README reconciled to describe both platforms.

Verified end-to-end on this machine: dead token → Sign in button → `claude auth login` → browser
→ fresh credentials → meter shows usage.

## Machine notes — Windows (this box)

- Windows 11 Pro, Intel N150, x64, display scaling 250% (DPI 240). The tick sizing uses *system*
  DPI, not per-monitor; a second monitor at a different scale would not rescale (known, cosmetic).
- PowerShell script execution is Restricted: run
  `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`.
- Meter installs to `%LOCALAPPDATA%\Programs\Claude Meter`; single-instance mutex, so stop it
  before running a test build from `windows\build\`.
- Claude Code CLI is the WinGet package (`...\WinGet\Packages\Anthropic.ClaudeCode_...\claude.exe`,
  on PATH); `claude auth login|logout|status` exist.
- Claude desktop app auto-updater and winget both reported "current" while the app was several
  versions behind; the fix was the official installer from
  `https://claude.ai/api/desktop/win32/x64/setup/latest/redirect` run over the top.

## Machine notes — macOS (fill in from the Mac session)

- TODO(mac): confirm the Mac re-auth button (5211f12) still works after the credential reset, and
  that the macOS status-file export path (`~/SynologyDrive/AI_Context/.../status/current.json`)
  is what Windows should mirror.

## Open parity items (also in STATUS.md)

- **status-file-win**: the macOS-only usage export to `status/current.json` (3ca0bb0) has no
  Windows equivalent. Decide the Windows path (SynologyDrive is present on this box? check) and
  port the JSON shape exactly.
- **reauth-mac-verify**: the Mac side's sign-in flow was written before the Windows one and not
  cross-checked against it; make the two behave the same (always-visible menu item, flyout
  button, not-found guidance).
