# Brief: rings-tweaks-win

## Decision
What: Two small Windows changes in `windows/Program.cs`, from Bernard 2026-10-04:
A. Rings float: the outline around the coloured centre numbers changes from black to a dark grey (target ≈ #3A3A3A at full alpha; keep the same stroke width). The band outlines and the curved labels are unchanged.
B. Right-clicking the floating gauge (all three styles: one line, square, Rings) opens the same context menu the tray icon shows, at the cursor. Left-drag keeps working; a right-click must not start a drag or move the window.
Why: A — the black outline reads harsh. B — Bernard wants the menu reachable from the gauge, not only the tray. (The Mac side had made float right-click a no-op; Bernard has reversed that for both platforms — see STATUS `rings-polish-mac`.)

## Files you own
- windows/Program.cs
Everything else is read-only.

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read `Draw.FitStack`/the Rings number drawing and `FloatForm` mouse handling first. Write your exact-edit plan to `scratch/rings-tweaks-win-builder.md`. Then build.
- Tree may be dirty: no.
- Never run `claude auth login`/`logout` or write the real `%USERPROFILE%\.claude\.credentials.json`.
- Back up `%APPDATA%\Claude Meter\settings.json` to `scratch/settings.backup.json` before changing style; restore it at the end.
- build.ps1 via `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`; finish with `-Install`. Reuse `scratch/rings-polish-*.ps1` / `v121-*.ps1` helpers.

## Intent
A. One colour constant for the number outline, used wherever the centre numbers are stroked. Do not touch the band/arc outline colour or the label colour.
B. The Rings float is a layered window: make sure right-click is received there too (WM_RBUTTONUP or MouseUp with `MouseButtons.Right`). Show `app.BuildMenu(includeRefresh: true)` (or the existing tray menu instance) with `menu.Show(Cursor.Position)`. Because the float is a no-activate, always-on-top window, verify the menu actually appears above it and closes on outside click; if focus is a problem, `SetForegroundWindow` the float before `Show`. Apply to all three styles.

## Verification
Screenshots (`scratch/`): rings-tweaks-win-01-rings-grey-outline-zoom.png, -02-menu-from-rings-float.png, -03-menu-from-oneline-float.png, -04-menu-from-square-float.png.
Checks JSON (`scratch/rings-tweaks-win-checks.json`), all true:
```json
{
  "build_ok": false,
  "a_number_outline_dark_grey": false,
  "b_right_click_menu_all_three_styles": false,
  "b_left_drag_still_works": false,
  "settings_restored": false,
  "real_credentials_file_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.
