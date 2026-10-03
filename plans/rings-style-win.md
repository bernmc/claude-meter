# Brief: rings-style-win

## Decision
What: Windows gets the same third floating-gauge style "Rings" as macOS
(plans/rings-style-mac.md): concentric rings, outer Week (all), middle per-model
week, inner Session, centre numbers stacked largest-first in ring colours, on a
circular form. Selectable in the Gauge style submenu.
Why: parity; both machines run the meter.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/Program.cs
Everything else is read-only.

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/rings-style-win-builder.md. Then build.
- Tree may be dirty: yes. Build on top. Never run any `claude` auth command.

## Intent
Existing: `S.FloatSquare`, `FloatForm.Relayout/OnPaint` (wide and square
branches, `MiniRing`), `Draw.Ring` (track + halo + colour arc), `BuildMenu`
style submenu (One line / Square) with checked states in `menu.Opening`,
`Win32.RoundCorners`.

1. Settings: `S.FloatStyle` string property, key "floatStyle", values
   "line" | "square" | "rings". Getter migrates: if the key is absent and
   `FloatSquare` is true → "square", else "line". Setter writes `floatStyle`
   only. Replace all `S.FloatSquare` reads with `S.FloatStyle` comparisons.
2. `FloatForm.Relayout`: "rings" → `Size = new Size(L(128), L(128))` and set
   `Region = new Region(new GraphicsPath with AddEllipse(0,0,Width,Height))`
   so the form is a disc; for the other styles set `Region = null`.
3. `OnPaint` rings branch: clear to `Theme.Bg`, draw the border as an ellipse
   inset 0.5 px. Rings centred, lineWidth `L(9)`, gap `L(3)`: diameters
   `L(108)`, `L(84)`, `L(60)` (outer week, middle model, inner session); two
   rings `L(108)`/`L(84)` when no `PrimaryModel`. Use `Draw.Ring` for each.
   Centre numbers via `Draw.Centered`: week at `Fnt(19, Bold)` y = cy − L(14),
   model at `Fnt(15, Bold)` y = cy + L(1), session at `Fnt(12, Bold)`
   y = cy + L(14), colours `Sev.Of(thatPct)`. Two-ring case: week 19 at
   cy − L(7), session 13 at cy + L(8).
4. Tooltip: a `System.Windows.Forms.ToolTip` on the form, text set in
   `Relayout` to one line per limit ("Session 52% · resets in 32 m · 21:01",
   then Week, then model) using `Fmt.ResetText`; applies to all styles.
5. Menu: style submenu gains "Rings"; `menu.Opening` checks the three items
   from `S.FloatStyle`.
6. `App.OnSettingsChanged`: compare a `lastStyle` string, relayout on change.

## Verification
`~/.dotnet/dotnet build -c Release --no-incremental` from claude-meter/windows:
zero errors, zero new warnings. Grep for the three diameters, the Region
ellipse, the migration, the "Rings" menu item. Runtime is compile-checked only;
say so.
Checks JSON (`scratch/rings-style-win-checks.json`), all must be true:
```json
{
  "build_clean_no_new_warnings": false,
  "floatstyle_with_migration": false,
  "rings_geometry_and_region": false,
  "centre_numbers_three_and_two": false,
  "tooltip_all_styles": false,
  "menu_rings_item_checked_state": false
}
```

## Report
Use the fixed report format. Nothing else.
