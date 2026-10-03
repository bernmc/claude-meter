# Brief: rings-select

## Decision
What: Which of the three limits are drawn as gauges is user-selectable:
Session, Week (all), Model week, each on/off, default all on, the last one on
cannot be switched off. Applies to the popover ring row, all three floating
styles (one line, square, rings) and their Windows equivalents. The menu-bar /
tray metric picker, the status file export, scoped bars for extra models and
the sparkline are NOT affected.
Why: some users don't care about a per-model limit, or the week.

## Files you own
Mac agent: macos/main.swift and README.md. Windows agent: windows/Program.cs.
(Full paths as in plans/rings-outline.md.) Execute this brief AFTER
rings-outline, in the same working tree.

## Hard rules
As in plans/rings-outline.md. Tree is dirty with your own rings-outline edits;
build on top of them.

## Intent (both platforms)
1. Preferences: `showSession`, `showWeek`, `showModel` (Bool, absent = true).
   Mac: UserDefaults via @AppStorage in views and a `GaugeSelection.current`
   helper for AppKit code. Windows: `S.ShowSession/ShowWeek/ShowModel`.
2. Visible list, in this canonical order: week, model, session, filtered by
   the three flags and by availability (model only when `primaryModel` exists).
   If filtering leaves nothing (all three off, which the UI prevents, or the
   only enabled one is an unavailable model) → fall back to session.
3. Menus. Mac gear menu: a `Menu("Gauges")` holding three Toggles "Session (5 h)",
   "Week (all models)", "Model week", placed directly before "Gauge style".
   A toggle is `.disabled(true)` when it is the only one currently on.
   Mac right-click: a submenu "Gauges" with the same three check items,
   same placement and same disable rule. Windows: a "Gauges" submenu in
   `BuildMenu` before "Gauge style", check states and enabled state set in
   `menu.Opening`.
4. Popover ring row: draw only the visible entries, in display order
   session, week, model (left→right, i.e. the existing order). Sizes: 3 → 72,
   2 → 84, 1 → 96; spacing 14; row centred. Labels as today.
5. Floating one-line and square: only the visible mini rings, same order as
   the popover; widths shrink accordingly (Mac: natural SwiftUI sizing; the
   fallback sizes in `sizeFloatingPanel` stay as the 3-ring values. Windows:
   `Relayout` width uses the visible count).
6. Floating rings: visible entries from outside in, in canonical order
   (week, model, session). Diameters: outer always 108, then 84, then 60 for
   however many are visible. Centre numbers: visible ones top→bottom in the
   same outer→inner order (or reversed when `ringsCentre == "session"`), font
   sizes: 3 → 19/15/12, 2 → 19/13, 1 → 22. Tooltip unchanged (all limits).
7. Changing a flag redraws live: Mac @AppStorage handles the views; the
   AppKit observer also calls `sizeFloatingPanel()` when any of the three
   keys changes (track them like `lastStyle`). Windows: `OnSettingsChanged`
   relayouts the float and repaints the flyout.
8. README (Mac agent): one sentence under the Configurable bullet: choose
   which limits are drawn as gauges (Gauges in the gear menu).
9. Previews (Mac): `--preview-popover-sel <png> <flags>` and
   `--preview-float-rings-sel <png> <flags>` where `<flags>` is a string of
   letters from `swm` (session, week, model) naming the ON set, rendered with
   the fake snapshot and without touching the user's defaults (pass the
   selection into the views the same way `forceStyle` is passed).

## Verification
Mac: `./build.sh`; renders into scratch: rings-select-01-popover-sw.png
(flags "sw"), rings-select-02-popover-s.png ("s"), rings-select-03-rings-wm.png
("wm"), rings-select-04-rings-s.png ("s"); `--preview-popover` (all on)
byte-identical to the rings-outline-era render of the full popover is NOT
expected (sizes unchanged there, but verify visually it is unchanged);
`--preview-float-wide` unchanged; `./build.sh --install`; `pgrep`. Iterate
at least three times. "Right": fewer rings grow to fill the row, stay
centred and evenly spaced; the rings disc with one ring shows a single 108
ring with a 22 pt number; nothing clipped.
Windows: build clean, zero new warnings; grep for the three settings, the
"Gauges" submenu, the visible-count width math; compile-checked only.
Checks JSON (`scratch/rings-select-<agent>-checks.json`), all true:
Mac: build_clean, popover_two_and_one_ring_sizes, rings_disc_subsets,
line_square_subsets, menus_gauges_submenu_last_one_locked,
all_on_unchanged, install_ok_app_running, readme_updated.
Windows: build_clean_no_new_warnings, settings_and_menu, popover_and_float_subsets,
last_one_locked.

## Report
Use the fixed report format. Nothing else.
