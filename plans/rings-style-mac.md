# Brief: rings-style-mac

## Decision
What: A third floating-gauge style, "Rings": three concentric Apple-Watch-style
rings on a frosted disc. Outer = Week (all models), middle = the per-model week
(`primaryModel`, Fable today), inner = Session (5 h). The centre stacks the three
percentages, largest at top, each tinted its ring's colour. Selectable from the
right-click menu and the gear picker alongside One line and Square.
Why: Bernard wants a compact single-glance gauge; the current layouts are cards.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else is read-only. windows/ is owned by another agent running now.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/rings-style-mac-visual-iterator.md. Then build.
- Tree may be dirty: yes (windows/Program.cs in parallel). Build on top.
- Never run any `claude auth` command. Do not touch HistoryStore, StatusExporter
  output, or the API layer. Do not change the popover.

## Intent
Existing: `FloatingView` (`@AppStorage("floatSquare")`, `wideLayout`, `squareLayout`,
`miniRing`, optional `forceSquare` preview override), `Sev.color`, the rim-arc
pattern used in `RingGauge` (dark under-arc width +2.5 then coloured arc),
`AppController.showContextMenu()` ("Square gauge layout" checkbox item),
`sizeFloatingPanel()` fallback sizes, the gear `Picker("Gauge style")` bound to
`floatSquare`, the UserDefaults observer comparing `lastSquare`, and the
`--preview-float-*` CLI modes built from the shared fake snapshot.

1. Preference: new key `floatStyle` (String: "line" | "square" | "rings").
   Read via a helper `FloatStyle.current` that migrates: if `floatStyle` is
   absent and `floatSquare` is true → "square", else "line". Writes go to
   `floatStyle` only. Replace `@AppStorage("floatSquare")` uses with the new
   key; `forceSquare` becomes `forceStyle: String?`.
2. `FloatingView` body: switch on style: "line" → wideLayout, "square" →
   squareLayout, "rings" → ringsLayout. For "rings" the background is a
   `Circle()` filled with `.regularMaterial` plus the same 0.12 strokeBorder,
   not the rounded rectangle; padding 10 all round.
3. `ringsLayout(snap)`: a ZStack, frame 108×108.
   - Ring geometry: lineWidth 9, gap 3. Diameters: outer 108, middle 84,
     inner 60 (each = previous − 24). With no `primaryModel`: two rings,
     outer 108 (week) and inner 84 (session).
   - Each ring, in this z-order: track `Circle().stroke(Color.primary.opacity(0.18), lineWidth: 9)`;
     rim `trim(0...pct).stroke(Color.black.opacity(0.32), StrokeStyle(lineWidth: 11.5, lineCap: .round))`;
     colour `trim(0...pct).stroke(Sev.color(pct), StrokeStyle(lineWidth: 9, lineCap: .round))`;
     all `.rotationEffect(.degrees(-90))`, min trim 0.003, `.animation(.easeOut(duration: 0.6), value: pct)`.
   - Centre: `VStack(spacing: -3)` of three `Text`s, `.monospacedDigit()`,
     `.font(.system(size: N, weight: .bold, design: .rounded))`,
     `.foregroundStyle(Sev.color(thatPct))`: week at 19, model at 15, session
     at 12, in that order top→bottom. Two-ring case: week 19 over session 13.
   - `.help(...)` tooltip on the ZStack: one line per limit,
     "Session 52% · resets in 32 m · 21:01" style using `resetText`, order
     session, week, model.
4. Right-click menu: replace the "Square gauge layout" checkbox with a
   submenu "Gauge style" holding three items "One line", "Square", "Rings",
   `.state = .on` for the current style; each sets `floatStyle`.
5. Gear menu: `Picker("Gauge style", selection:)` bound to `floatStyle`
   with tags "line", "square", "rings" and labels "One line", "Square", "Rings".
6. Observer: compare `lastStyle: String` instead of `lastSquare`; resize on change.
7. `sizeFloatingPanel` fallback: rings → 128×128 (108 + 2×10).
8. Preview modes: `--preview-float-rings <png>` and `--preview-float-rings-noscoped <png>`
   using the shared fake snapshot (session 52, week 26, Fable 39), rendered at
   natural size on an opaque window-background colour.
9. README: floating gauge bullet lists three layouts: one-line, square, or
   Apple-Watch-style concentric rings (week outside, model, session inside).

## Verification
Run (from claude-meter/macos): `./build.sh`; the two preview modes into the
scratch dir as rings-style-mac-01-rings.png and rings-style-mac-02-noscoped.png;
`--preview-float-wide` into rings-style-mac-03-wide.png (proves the other
layouts are unchanged); `--once` (Bernard is signed in now; if it reports
signed out, record it and continue); `./build.sh --install`; `pgrep -x "Claude Meter"`.
Reference: Apple Watch Activity rings. "Right" means: three rings clearly
separated by the 3 pt gap, caps rounded, the rim visible as a hairline edge on
the coloured arcs only, the three numbers legible and not touching the inner
ring, the disc background a true circle with the border hugging it, nothing
clipped at the panel edge. Iterate at least three times.
Checks JSON (`scratch/rings-style-mac-checks.json`), all must be true:
```json
{
  "build_clean": false,
  "rings_three_separated_numbers_legible": false,
  "rings_two_when_no_scoped": false,
  "disc_background_circle_not_clipped": false,
  "wide_layout_unchanged": false,
  "menu_and_picker_three_styles": false,
  "floatsquare_migration": false,
  "once_ran": false,
  "install_ok_app_running": false,
  "readme_updated": false,
  "popover_history_status_api_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.

## Amendment 1 (orchestrator, after spawn)
Add a second preference so Bernard can compare both centre orderings live:
- UserDefaults key `ringsCentre` (String): "week" (default, absent = "week") or
  "session".
- "week": as specified: week 19 pt top, model 15, session 12 bottom.
- "session": reversed: session 19 pt top, model 15, week 12 bottom. Two-ring
  case: session 19 over week 13.
- Gear menu: `Picker("Rings centre", selection:)` bound to `ringsCentre`, labels
  "Week largest" / "Session largest", placed directly after the Gauge style picker.
- Right-click menu: submenu "Rings centre" with the same two items, state on for
  the current value, placed directly after the Gauge style submenu.
- The observer that resizes on style change also repaints on `ringsCentre`
  change (no resize needed; SwiftUI @AppStorage will redraw, confirm it does).
- Preview: `--preview-float-rings-session <png>` renders the "session" ordering
  with the fake snapshot. Add it as rings-style-mac-04-session.png to the
  screenshots and a check `rings_centre_option_both_orderings` to the checks JSON.
