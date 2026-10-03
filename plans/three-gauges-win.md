# Brief: three-gauges-win

## Decision
What: The Windows flyout and floating gauge show THREE ring gauges: Session (5 h),
Week (all models), and the first per-model weekly limit (today "Fable"), which
replaces that model's capsule bar. The tray metric menu gains "Model week".
Why: parity with the macOS change (plans/three-gauges-mac.md) being built in parallel.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/Program.cs
Everything else is read-only. macos/ and README.md are owned by another agent.

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/three-gauges-win-builder.md. Then build.
- Tree may be dirty: yes. Build on top of what is there (recent Windows commits
  changed sign-in and menu drawing; keep all of that).
- Never run any `claude` auth command.

## Intent
Existing pieces: `UsageSnapshot` (Session, WeeklyAll, Scoped), `FlyoutForm`
(`Relayout` height math, `OnPaint` with two `RingGauge` calls at cxL/cxR and the
scoped bars loop, width `L(300)`), `FloatForm` (`Relayout` sizes, `OnPaint` wide and
square branches with `MiniRing`), `App.ChosenLimit()` and `BuildMenu` metric items.

1. `UsageSnapshot.PrimaryModel => Scoped.FirstOrDefault()`; a static
   `ModelName(LimitEntry)` that strips a leading "Week — ". Reuse any existing
   stripping helper if one exists.
2. Flyout: when PrimaryModel exists, three rings of `L(72)` at x centres
   w/2 − L(104), w/2, w/2 + L(104), labels "Session", "Week (all)", "Week (<model>)";
   width `L(300)` → `L(336)`. Keep `Relayout` height math consistent with the new
   ring size (72 instead of 84 in the three-ring case; two rings of 84 when no
   scoped entry). Scoped bars loop over `Scoped.Skip(1)`; the section's height
   contribution follows the same count.
3. FloatForm: three MiniRings in both layouts when PrimaryModel exists, tags
   "5 h", "week", lowercase model name; sizes in `Relayout` widen accordingly
   (wide: add 34 + 14 per extra ring; square: top row of three rings, min width
   from three rings + spacing). Two rings when no scoped entry.
4. `ChosenLimit`: `"model" => Snap?.PrimaryModel ?? worst`. `BuildMenu`: add
   "Model week" item after "Week (all models)" setting `S.TrayMetric = "model"`,
   with its checked state in `menu.Opening`.
5. No change to StatusExporter-equivalents (none exist on Windows), history, API.

## Verification
Run (from claude-meter/windows): `~/.dotnet/dotnet build -c Release --no-incremental`
— zero errors, zero new warnings. Grep your edits for the three ring centres, the
Skip(1), the "model" case and the menu item. State that runtime is compile-checked
only.
Checks JSON (`scratch/three-gauges-win-checks.json`), all must be true:
```json
{
  "build_clean_no_new_warnings": false,
  "flyout_three_rings_and_width": false,
  "relayout_height_consistent": false,
  "scoped_bars_skip_first": false,
  "float_both_layouts_three_rings": false,
  "tray_metric_model_option": false
}
```

## Report
Use the fixed report format. Nothing else.
