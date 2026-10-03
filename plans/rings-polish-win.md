# Brief: rings-polish-win

## Decision
What: Five Windows changes in `windows/Program.cs`, decided by Bernard on 2026-10-04 from the v1.2.1 look-check:
A. Rings float centre numbers: reduce the font so the stacked numbers fit inside the innermost ring's hole.
B. Rings float curved band labels (nice-to-have, do after A–E are solid): fine black text along each band, starting at 12 o'clock and reading clockwise: outer = `total`, middle = the model name in lower case (`fable` today), inner = `session`.
C. Rings float becomes a per-pixel-alpha layered window so the disc edge is antialiased; remove the Region clip and the `Win32.DiscFrame` workaround.
D. One-line (rectangle) float: the three text lines are centre-justified within the text column.
E. Tray menu "Sign in to Claude Code…" is shown only when `App.AuthRequired` is true (matches macOS); the flyout button is already conditional.
Why: A — the 51/77/7 stack is taller than the inner hole (Bernard's screenshot, "numbers are a bit bigger than the inner white circle"). B — Bernard's ask, Apple-Watch-like. C — the Region clip stair-steps the rim; a layered window is the future-proof fix and Bernard chose it. D — Bernard's ask. E — Bernard's decision, parity with the Mac.

## Reference
`scratch/v121-look-win-03-float-rings-zoom.png` (current state) and Bernard's screenshot in this session: outer gold ring 51, middle orange 77, inner green 7, numbers overlapping the inner ring. "Right" = numbers wholly inside the inner hole with visible clearance; labels sit on the bands, thin, black, legible, never overflowing the band; the disc rim is smooth at 8× zoom.

## Files you own
- windows/Program.cs
Everything else is read-only (no csproj, build.ps1, README, Mac code, plans).

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read `plans/rings-style-win.md`, `plans/rings-outline.md`, `plans/rings-select.md` and the current `FloatForm`/Rings drawing code first. Write your exact-edit plan to `scratch/rings-polish-win-builder.md`. Then build.
- Tree may be dirty: no.
- Never run `claude auth login`/`logout` or write the real `%USERPROFILE%\.claude\.credentials.json`. For item E's auth-required state use `CLAUDE_METER_CREDS_PATH=scratch\fake-creds.json` + `CLAUDE_METER_CLAUDE_EXE=C:\nonexistent\claude.exe` on a test build.
- Back up `%APPDATA%\Claude Meter\settings.json` (it now exists and persists) to `scratch/settings.backup.json` before changing style/selection; restore it at the end.
- build.ps1 via `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`; finish with `-Install`.
- Reuse the UI-automation helpers in `scratch/v121-*.ps1` for the tray menu and screenshots.

## Intent
A. Choose the centre font size from the inner hole diameter: the whole stack (3, 2 or 1 lines depending on gauge selection) must fit inside the hole with ≥ 1/10 of the hole diameter as margin on every side. Keep the existing per-ring colours and the dark outline. Keep the "Rings centre" option (which ring is largest) working.
B. Each label is drawn along the centreline of its band, glyph by glyph rotated to the tangent, starting at 12 o'clock and proceeding clockwise. Thin weight (Segoe UI Light or Regular at small size), black, no outline, drawn above both the grey track and the coloured arc. Size: cap height ≈ 55–65% of the band width; if the label still wouldn't fit in a quarter turn, shrink it. With 1 or 2 gauges, only the shown rings get labels. The model label comes from the snapshot's model name, lower-cased; fall back to `model` if absent.
C. `WS_EX_LAYERED` + `UpdateLayeredWindow` with a 32-bpp ARGB bitmap rendered with anti-aliasing; disc background alpha 255 inside, 0 outside, antialiased edge. Keep: drag (with WM_NCHITTEST or mouse handling that still works on a layered window), saved position via settings, hover tooltip (verify — tooltips can fail on layered windows; if so, attach the tooltip to a transparent child or fall back to a custom hover popup), right-click no-op, always-on-top, no taskbar entry. Delete `Win32.DiscFrame` and the Region code once the layered path works. Leave one-line and square styles as ordinary forms.
D. Text column width = widest of the three lines; each line centred within that column; column position unchanged.
E. In `BuildMenu`, create the item once and set `Visible = AuthRequired` in the `Opening` handler (and hide the separator if one belongs to it). Nothing else about sign-in changes.

## Verification
Screenshots (`scratch/`): rings-polish-win-01-rings3-zoom.png, -02-rings2-zoom.png, -03-rings1-zoom.png, -04-rim-8x.png (before/after pair ok), -05-oneline-centred.png, -06-menu-signed-in.png (no Sign in item), -07-menu-auth-required.png (item present).
Checks JSON (`scratch/rings-polish-win-checks.json`), all true:
```json
{
  "build_ok": false,
  "a_numbers_inside_inner_hole_3_2_1": false,
  "b_labels_on_bands_12oclock_clockwise": false,
  "c_layered_rim_smooth_drag_tooltip_position_ok": false,
  "d_oneline_text_centred": false,
  "e_signin_item_only_when_auth_required": false,
  "settings_restored": false,
  "real_credentials_file_untouched": false
}
```
Iterate on A–D against the reference at least three times (screenshot → compare → adjust) before running the final verification.

## Report
Use the fixed report format. Nothing else.
