# Brief: three-gauges-mac

## Decision
What: The macOS popover and the floating desktop gauge show THREE ring gauges:
Session (5 h), Week (all models), and the per-model weekly limit (today "Fable").
The per-model ring replaces that model's slim capsule bar. The menu-bar metric
picker gains a "Model week" option.
Why: Bernard will run most work through the Fable model and wants its weekly limit
as prominent as the other two, not a thin bar under them.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else is read-only. windows/ is owned by another agent running now.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/three-gauges-mac-visual-iterator.md. Then build.
- Tree may be dirty: yes (windows/Program.cs edited in parallel). Build on top.
- Never run any `claude auth` command. Do not touch HistoryStore, history.json,
  StatusExporter output shape, or the API layer.

## Intent
Existing pieces: `UsageSnapshot` (session, weeklyAll, scoped: [LimitEntry]),
`LimitEntry.label` for scoped entries looks like "Week — Fable", `RingGauge` view
(size param, default 84), `ScopedBar`, `PopoverView` (HStack of two RingGauges, then
scoped bars, sparkline, footer; `.frame(width: 292)`), `FloatingView` (wideLayout /
squareLayout with `miniRing`), the gear `Picker("Menu bar shows")`, `updateStatusButton`
metric switch, `sizeFloatingPanel` fallback sizes, and preview CLI modes
(`--preview-signin*`) that render PopoverView via ImageRenderer with `staticPreview`.

1. Add to `UsageSnapshot`: `var primaryModel: LimitEntry? { scoped.first }` and a
   helper `modelName(_ e: LimitEntry) -> String` = label with leading "Week — "
   stripped (reuse any existing stripping logic from StatusExporter if present;
   do not duplicate).
2. PopoverView rings row: three `RingGauge`s when `primaryModel` exists, size 72
   each, spacing 14, centred; labels "Session", "Week (all)", "Week (<model>)"
   e.g. "Week (Fable)"; sublabel = resetText as now. When no scoped entry exists,
   keep today's two rings at size 84 (unchanged look).
3. Popover width: 292 → 336 (so three 72 pt rings plus two-line sublabels fit
   without truncation). Everything else in the popover stretches to the new width.
4. Scoped bars: list `scoped.dropFirst()` only (the first model is now a ring).
   If that leaves nothing, the bars section disappears.
5. FloatingView: both layouts show three `miniRing`s when `primaryModel` exists:
   tags "5 h", "week", and the lowercase model name (e.g. "fable"). Wide layout:
   rings then the text column as now. Square layout: three rings in the top row,
   countdown below. Two rings when no scoped entry.
6. `sizeFloatingPanel` fallback sizes: wide 290×64 → 340×64, square 190×100 → 240×100
   (only used if fittingSize comes back degenerate).
7. Menu bar: `Picker("Menu bar shows")` gains `Text("Model week").tag("model")`
   after "Week (all models)"; `updateStatusButton` maps "model" → `snap.primaryModel`
   (falls back to the worst limit when nil).
8. Preview CLI modes for verification, beside the existing `--preview-signin` ones,
   each taking an output PNG path, no network/keychain, built from one shared fake
   snapshot (session 52 % resets +33 min, weekly_all 26 % resets +117 h, scoped
   "Week — Fable" 39 % resets +117 h, plan "max", fetchedAt now):
   `--preview-popover`, `--preview-float-wide`, `--preview-float-square`.
   The float previews render `FloatingView` at its natural size on an opaque
   window-background colour.
9. README: in Features, the popover bullet says three ring gauges (session, weekly,
   and the per-model weekly limit); the floating gauge bullet says three mini gauges.
   The "Menu bar shows" option mention, if any, lists Model week.

## Verification
Run (from claude-meter/macos): `./build.sh`; then the three preview modes into the
scratch dir as three-gauges-mac-01-popover.png, three-gauges-mac-02-float-wide.png,
three-gauges-mac-03-float-square.png; then `--once` (live fetch still works); then
`./build.sh --install` and `pgrep -x "Claude Meter"`.
Reference: docs/screenshot.png (the current two-ring popover) and the existing
floating gauge. "Right" means: three rings of equal size on one row, evenly spaced,
numbers centred in rings, labels on one line, sublabels not clipped or ellipsised,
the row not crowding the sparkline; in the float, rings equal, tags aligned, the
countdown fully visible. Iterate at least three times.
Checks JSON (`scratch/three-gauges-mac-checks.json`), all must be true:
```json
{
  "build_clean": false,
  "popover_three_rings_no_clipping": false,
  "popover_two_rings_when_no_scoped": false,
  "scoped_bars_exclude_primary": false,
  "float_wide_three_rings": false,
  "float_square_three_rings": false,
  "menu_metric_model_option": false,
  "once_still_works": false,
  "install_ok_app_running": false,
  "readme_updated": false,
  "history_status_api_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.
