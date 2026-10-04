# Brief: opacity-win

## Decision
What: Windows floating gauge gets a user-set opacity (0.2…1.0, default 0.94 =
today's `Opacity`), driven by one setting, with a slider in the tray/float
context menu. Version 1.3.0.
Why: parity with plans/glass-opacity-mac.md.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/Program.cs
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/ClaudeMeter.csproj

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/opacity-win-builder.md. Then build.
- Tree may be dirty: yes. Build on top. Never run any `claude` auth command.

## Intent
1. `S.GaugeOpacity` (double, key "gaugeOpacity", default 0.94, clamp 0.2…1.0).
2. One-line and square styles: `Form.Opacity = S.GaugeOpacity`. Rings
   (layered window via UpdateLayeredWindow): set `BLENDFUNCTION.SourceConstantAlpha`
   to `(byte)Math.Round(255 * S.GaugeOpacity)` so the whole disc scales
   uniformly (same semantics as Form.Opacity).
3. Menu: "Gauge opacity" item hosting a `TrackBar` (`ToolStripControlHost`,
   Minimum 20, Maximum 100, TickFrequency 20, width ~160) placed after the
   "Rings centre" submenu; `ValueChanged` writes `S.GaugeOpacity = value/100.0`
   and re-applies live. Because the tray menu is built once, set the
   TrackBar value from the setting in `menu.Opening`.
4. csproj `<Version>` → 1.3.0.

## Verification
`~/.dotnet/dotnet build -c Release --no-incremental` zero errors, zero new
warnings; grep for the setting, the TrackBar host, SourceConstantAlpha and the
version. Compile-checked only; say so.
Checks JSON (`scratch/opacity-win-checks.json`), all true:
build_clean_no_new_warnings, setting_and_clamp, applied_all_three_styles,
trackbar_in_menu_live, version_1_3_0.

## Report
Use the fixed report format. Nothing else.
