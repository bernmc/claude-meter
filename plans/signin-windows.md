# Brief: signin-windows

## Decision
What: Windows port gets the corrected sign-in error text (`claude auth login`, not
plain `claude`) and a "Sign in to Claude Code…" tray menu item, shown only while
sign-in is needed, that opens a console running `claude auth login`.
Why: parity with the macOS change being built in parallel. Plain `claude` no longer
prompts for sign-in. A menu item (not a custom-drawn flyout button) keeps the change
low-risk, since this machine can only compile-check the Windows code.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/Program.cs
Everything else is read-only. macos/ and README.md are owned by another agent running now.

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/signin-windows-builder.md. Then build.
- Tree may be dirty: yes (macos/ and README.md are being edited in parallel). Build on top.
- Never run any `claude` auth command.

## Intent
Existing pieces in Program.cs: `ApiException` with `NeedsSignIn`, `UsageAPI.ValidToken()`
throwing sign-in exceptions, `App.RefreshNow()` catch block (sets `ErrorText`,
one-time balloon via `notifiedSignIn`), `App.BuildMenu(includeRefresh)` shared by
tray right-click and the flyout gear, with a `menu.Opening` handler that updates
checked states.

1. Exact message strings:
   - missing credentials file: "Claude Code isn't signed in on this PC. Use \"Sign in to Claude Code…\" in the tray menu, or run `claude auth login` in a terminal."
   - no refresh token AND the HTTP 400/401 case: "Claude Code is signed out. Use \"Sign in to Claude Code…\" in the tray menu, or run `claude auth login` in a terminal."
2. `App` gains `public bool NeedsSignIn`: true when a caught `ApiException` has
   `NeedsSignIn`; false after any successful fetch.
3. `BuildMenu`: add item "Sign in to Claude Code…" as the FIRST item followed by a
   separator (keep a reference to both); in `menu.Opening` set both `.Visible =
   NeedsSignIn`. Click handler calls `StartSignIn()`.
4. `App.StartSignIn()`: `Process.Start(new ProcessStartInfo("cmd.exe", "/k claude auth login") { UseShellExecute = true })`
   inside try/catch (swallow). Then start a 5 s WinForms Timer calling `RefreshNow()`
   that stops itself when `NeedsSignIn` becomes false or after 3 minutes. The 60 s
   poll is unchanged.
5. No flyout drawing changes.

## Verification
Run (from claude-meter/windows): `~/.dotnet/dotnet build -c Release` — must succeed
with zero errors and zero new warnings.
Also: grep your edits to confirm the two exact strings and that the menu item is
first and visibility-gated.
Screenshots: none (cannot run WinForms on macOS). State in the report that runtime
behaviour is compile-checked only.
Checks JSON (`scratch/signin-windows-checks.json`), all must be true:
```json
{
  "build_clean_no_new_warnings": false,
  "strings_exact": false,
  "menu_item_first_and_gated": false,
  "fast_poll_stops": false,
  "flyout_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.
