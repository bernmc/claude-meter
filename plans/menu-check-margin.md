# Brief: menu-check-margin

## Decision
What: Fix the tray context menu in `windows/Program.cs` so checked items ("Desktop gauge", "Number in tray icon", "Launch at login" when on) show their check mark in its own left column and never overlap the first letter of the item text. Apply the same fix to any ContextMenuStrip the app creates (the gear menu in the flyout, if it is a separate strip, and the submenus "Gauge style", "Tray icon shows", "Warn at").
Why: Screenshot from the user: the check glyph and its highlight square are drawn over the "D"/"N" of the item label because the strip has `ShowImageMargin` and `ShowCheckMargin` both off (or the check is being painted into the text column at this machine's DPI scaling). Cosmetic but visible on every right-click.

## Files you own
- windows/Program.cs
Everything else is read-only.

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first (search for `ContextMenuStrip`, `ShowImageMargin`, `ShowCheckMargin`, `Renderer`, `RenderMode`). Write your exact-edit plan to `scratch/menu-check-margin-builder.md`. Then build.
- Tree may be dirty: yes — reauth-401 may be uncommitted in the same file; build on top of what is there and do not revert anything.
- Never touch the real `%USERPROFILE%\.claude\.credentials.json`; no env overrides are needed for this task.
- build.ps1 runs via `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`. Finish with `-Install`.

## Intent
- Preferred fix: on every ContextMenuStrip (and ToolStripDropDown submenu) set `ShowCheckMargin = true` and `ShowImageMargin = false`, so the check gets a dedicated column and text starts to its right. If the current custom renderer (if any) still paints the highlight square over text, switch to `ToolStripRenderMode.System` or adjust the renderer's check rendering — whichever gives a clean result at this machine's DPI scaling (it is >100%).
- Do not change menu item order, labels, or behaviour. Do not change the flyout layout.
- Expect ~5–15 lines.

## Verification
Run (from `windows/`):
1. build.ps1 — 0 errors.
2. build.ps1 -Install; right-click the tray icon; screenshot the open menu with at least two checked items visible; the check marks must sit fully left of the text with the first letters unobscured. Also open the "Gauge style" submenu and screenshot it (it has a checked radio-style item).
Screenshots: menu-check-margin-01-main-menu.png, menu-check-margin-02-submenu.png
Checks JSON (`scratch/menu-check-margin-checks.json`), all must be true:
```json
{
  "build_ok": false,
  "checks_do_not_overlap_text_main_menu": false,
  "checks_do_not_overlap_text_submenu": false,
  "installed_meter_running_normal": false
}
```

## Report
Use the fixed report format. Nothing else.
