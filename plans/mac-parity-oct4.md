# Brief: mac-parity-oct4

## Decision
What: Bring the macOS floating gauge level with the Windows round of 03/10
(plans/rings-polish-win.md A/B/D, plans/rings-tweaks-win.md, v121-look-win):
curved band labels on the Rings disc, centre numbers sized to the inner hole,
dark-grey number outline, centred stacked text column in the one-line style,
right-click on the float opens the context menu.
Why: Bernard approved these on Windows; both machines should match.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else read-only. windows/ is owned by another agent running now.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/mac-parity-oct4-visual-iterator.md. Then build.
- Tree may be dirty: yes (windows/ in parallel). Build on top.
- Never run `claude auth`, `git pull`, or the update/sign-in scripts.
- Popover, menu bar, status export, history, API: unchanged.

## Intent
Existing: `FloatingView` (wideLayout, squareLayout, ringsLayout with
`ringArc`, `OutlinedNumber` two-layer NSTextField, `GaugeSelection`,
`ringsCentre`), `DragOverlayView` (mouseDown → performDrag),
`AppController.showContextMenu()` (builds an NSMenu, pops it from the status
item), `sizeFloatingPanel`, preview CLI modes rendering via `renderHosted`.

A. Centre numbers fit the hole. Hole diameter = innermost drawn ring's
   diameter − 9. The full stack (3, 2 or 1 numbers, current font ratios
   19:15:12 / 19:13 / 22) must fit inside a square of 0.8 × hole on both
   axes; scale all sizes by one factor when it doesn't (never scale up above
   the current sizes). Keep colours, outline, and the Rings centre reversal.
B. Curved band labels on the Rings disc. One label per drawn ring, along the
   band's centreline, starting at 12 o'clock going clockwise, glyph by glyph
   rotated to the tangent. Text: week → "total", model → model name lower-
   cased ("fable"; "model" if unknown), session → "session". Weight thin
   (`.light`), size so cap height ≈ 60 % of the 9 pt band (about 7 pt; shrink
   if the label would exceed a quarter turn). Colour `Color.primary.opacity(0.8)`
   (black-ish in light, white-ish in dark; Windows uses black but its disc is
   opaque). Drawn above both track and coloured arc, no outline. Implement
   with a `Canvas` that draws each character with a rotation transform, or
   per-character Text views positioned on the circle; your call, say which.
C. Number outline colour: replace the black stroke layer colour with
   `#3A3A3A` (`Color(red: 0.227, green: 0.227, blue: 0.227)`), both modes.
D. One-line style text column: three stacked lines, each centred in the
   column, column width = widest line: "Claude" (10 pt bold, secondary),
   "resets in 4 h 24 m" (9 pt secondary), the clock "1:00 pm" (9 pt
   secondary). Derive the two reset lines by splitting `resetText(...)` on
   " · " (if there's no separator, show it on one line and leave the third
   line out). No trailing slack: the panel width follows content.
E. Right-click anywhere on the float (all styles) opens the same menu as the
   status item right-click, at the cursor. Refactor: `AppController.buildContextMenu() -> NSMenu`
   used by both `showContextMenu()` and a new `DragOverlayView.rightMouseDown`
   (`menu.popUp(positioning: nil, at: event.locationInWindow, in: overlay)`).
   Left-drag unchanged.
F. README: floating gauge bullet mentions the band labels and right-click menu.
G. Preview modes: existing rings previews must show the labels; add
   `--preview-float-wide` output to the named screenshots to show D.

## Verification
From claude-meter/macos: `./build.sh`; renders into scratch:
mac-parity-oct4-01-rings-light.png (`--preview-float-rings`),
mac-parity-oct4-02-rings-dark.png (`--preview-float-rings-dark`),
mac-parity-oct4-03-rings-s.png (`--preview-float-rings-sel … s`, one ring),
mac-parity-oct4-04-wide.png (`--preview-float-wide`); `--once`;
`./build.sh --install`; `pgrep`. Right-click: add `--selftest-float-menu`
that builds the overlay's menu and prints its item titles (no UI), exit 0.
Iterate at least three times. "Right": labels legible, evenly curved, start
at 12 o'clock, don't collide with the arc caps; numbers inside the hole with
visible margin; one-line column centred with no slack.
Checks JSON (`scratch/mac-parity-oct4-checks.json`), all true: build_clean,
numbers_fit_hole_all_counts, band_labels_curved_both_modes,
outline_3a3a3a, oneline_centred_column_no_slack, float_rightclick_menu,
once_ran, install_ok_app_running, readme_updated.

## Report
Use the fixed report format. Nothing else.
