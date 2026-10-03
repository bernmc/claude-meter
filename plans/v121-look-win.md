# Brief: v121-look-win

## Decision
What: Run the Windows runtime look-check the Mac session left open (STATUS items `v1.2.1-look` and `rings-and-update-look`) on this PC at v1.2.1 (`e7d10ef`), screenshot every item, and fix in place any defect found in `windows/Program.cs`. The specs are the existing briefs: `plans/three-gauges-win.md`, `plans/rings-style-win.md`, `plans/rings-outline.md`, `plans/rings-select.md`, `plans/update-check-win.md`. Where a brief and the Mac behaviour disagree, the brief wins; mark anything you can't test as believed, not verified.
Why: These features were built on the Mac and only compiled for Windows; nobody has looked at them on a real Windows screen (250% DPI). The Mac's checklist names the suspected problems.

## Files you own
- windows/Program.cs
Everything else is read-only (do not edit the Mac code, csproj, build.ps1, README, or any plan).

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the five briefs and the relevant code first. Write your exact-edit plan (or "no edits needed" with reasons) to `scratch/v121-look-win-builder.md`. Then build.
- Tree may be dirty: no.
- Never run `claude auth login`/`logout` or write the real `%USERPROFILE%\.claude\.credentials.json`.
- Settings live in `%APPDATA%\Claude Meter\settings.json`: back it up to `scratch/settings.backup.json` before changing gauge style/selection via the menu, and restore it at the end so the user's layout is unchanged.
- build.ps1 runs via `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`. If you change code, finish with `-Install`; if not, leave the installed v1.2.1 running.

## Checklist (each gets a screenshot and a line in the checks JSON)
Flyout
1. Three rings: Session, Week (all), per-model week — labels, reset countdowns, plan badge legible; nothing clipped at 250%.
2. Gauge selection (Gauges submenu / rings-select): 1-gauge and 2-gauge layouts in the flyout — spacing stays centred, no empty slot; the submenu refuses to uncheck the last gauge ("lock").
Floating gauge — all styles (one line, square, Rings)
3. Rings style: concentric arcs render with the outlined arc edge and outlined numbers (rings-outline); look for jagged disc edge from the Region clip and the 1-px glyph drift the Mac flagged. Zoom screenshots.
4. Rings centre option (rings-style amendment): toggles and persists across a restart.
5. Drag: the float moves and the position is saved after restart (all three styles). Hover tooltip appears. Right-click on the float does nothing; right-click on the tray icon still opens the menu.
6. 1- and 2-gauge layouts in each float style.
Update check
7. Tray menu "Check for updates…" and the auto-check toggle exist and persist. `& ".\build\Claude Meter.exe" --check-update` (or the installed exe) prints a sane result for the current version against GitHub Releases (1.2.1 is the latest tag, so expect "up to date"). The "Update available" flyout row must be hidden now.
8. Update-available path: only if the code has a version/feed override for testing (read update-check-win.md). If it does, exercise balloon → click routing → "Update to v…" row once, without actually installing. If it does not, say so under Believed; do NOT add an override or hack.
Tray
9. Tray icon number and ring at 250%; red "!" not showing in the normal state.

## Verification
Run: build (if edited), the checklist above.
Screenshots (in `scratch/`): v121-look-win-01-flyout-3rings.png, -02-flyout-1and2.png, -03-float-rings-zoom.png, -04-float-oneline.png, -05-float-square.png, -06-update-menu.png, -07-tray-zoom.png, plus any "before/after" pair for each fix you make.
Checks JSON (`scratch/v121-look-win-checks.json`): one boolean per checklist line (`c1_three_rings_ok` … `c9_tray_ok`), plus `build_ok` (true if no edits were needed), `settings_restored`, `real_credentials_file_untouched`. All must be true, except items you report as untestable — set those to null and explain.

## Report
Use the fixed report format. Nothing else. List every defect found with: what, where (symbol name), fixed or not, screenshot name.

## Amendment 1 — one-line float width (from Bernard, 2026-10-04)
Screenshot: one-line float with 3 rings shows a large empty grey band to the right of the text block
("Claude" / "resets in 4 h 33 m · 12:59 pm"). The panel width must be derived from content:
left padding + rings block + gap + measured width of the widest text line + right padding, where
right padding == left padding (the gap before the first ring). It must re-measure when the gauge
selection changes (1/2/3 rings) and when the reset string changes length. Apply the same rule to
the square style if it has the same slack. Add checklist item 10 (`c10_float_width_fits_content`)
with before/after screenshots v121-look-win-10-oneline-before.png / -10-oneline-after.png.

## Amendment 2 — stack the reset time (from Bernard, 2026-10-04)
In the one-line float's text block, split the reset line into two: line 2 `resets in 4 h 33 m`,
line 3 `12:59 pm` (the time, without the ` · ` separator), under line 1 `Claude`. The widest
line is now the "resets in …" line, so the content-derived width from Amendment 1 shrinks
accordingly. Keep the three lines vertically centred against the rings; if the panel height must
grow to fit three lines at the current font sizes, grow it minimally rather than shrinking the
fonts. Checklist item 11 (`c11_reset_time_stacked`), screenshot v121-look-win-11-oneline-stacked.png
(this can be the same image as -10-oneline-after.png if both land together).
