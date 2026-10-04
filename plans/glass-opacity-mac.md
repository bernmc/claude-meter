# Brief: glass-opacity-mac

## Decision
What: The floating gauge (all three styles) adopts Liquid Glass on macOS 26+
with a user-set opacity (0 = clear glass, 1 = solid), falling back to the
current frosted material on older macOS. Slider in the right-click menu,
stepped picker in the gear menu. Version 1.3.
Why: Bernard runs macOS 27; the frosted card looks dated next to glass, and
he wants to tune how much wallpaper shows through.

## Files you own
Same as plans/mac-parity-oct4.md, plus
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/Info.plist
Execute AFTER mac-parity-oct4 on the same tree.

## Hard rules
As in plans/mac-parity-oct4.md. The build must still succeed with
`-target …-apple-macos14.0` (build.sh unchanged): gate every glass API with
`if #available(macOS 26.0, *)`.

## Intent
1. Preference `gaugeOpacity` (Double 0…1, absent = 0.6). `@AppStorage` in
   `FloatingView`; a `GaugeOpacity.current` helper for AppKit.
2. Background of the floating content (card shape for one-line/square, disc
   for rings), replacing the current `.background(.regularMaterial, in:)`:
   - macOS 26+: `.glassEffect(.regular.tint(Color(nsColor: .windowBackgroundColor).opacity(opacity)), in: shape)`
     wrapped in a `GlassEffectContainer` if the API requires one. Opacity 0
     → untinted clear glass; 1 → effectively solid window colour.
   - Older: `.background(.regularMaterial, in: shape)` plus, between
     material and content, `shape.fill(Color(nsColor: .windowBackgroundColor).opacity(opacity))`.
   Keep the 0.12 border stroke in both paths.
   If the glass modifier does not compile against this SDK, keep the fallback
   path only and mark `TODO(orchestrator): glassEffect unavailable: <error>`.
3. Right-click menu: a view item labelled "Gauge opacity" containing an
   `NSSlider` (continuous, 0…1, width 160) that writes `gaugeOpacity` live
   on change, placed after the "Rings centre" submenu. Gear menu:
   `Picker("Gauge opacity")` with tags 0.0, 0.25, 0.5, 0.75, 1.0 labelled
   "Clear", "25 %", "50 %", "75 %", "Solid", same placement.
4. Info.plist versions → "1.3". README: Configurable bullet mentions gauge
   opacity (slider on right-click, steps in the gear menu); Requirements
   notes Liquid Glass on macOS 26+, frosted on 14–15.
5. Preview: `--preview-float-rings-opacity <png> <0..1>` and
   `--preview-float-wide-opacity <png> <0..1>` rendering over a busy
   background (a diagonal two-colour gradient behind the panel in the preview
   only) so the effect is visible.

## Verification
`./build.sh`; previews into scratch: glass-opacity-mac-01-rings-0.png (0),
glass-opacity-mac-02-rings-06.png (0.6), glass-opacity-mac-03-rings-1.png
(1), glass-opacity-mac-04-wide-03.png (0.3); `--check-update` prints
"current 1.3"; `./build.sh --install`; `pgrep`; `--selftest-float-menu` lists
the "Gauge opacity" item. Iterate at least three times. "Right": at 0 the
gradient shows through clearly with the glass refraction edge visible; at 1
the panel is opaque; at 0.6 it matches today's look closely; the border still
hugs the shape; rings/labels/numbers unchanged.
Checks JSON (`scratch/glass-opacity-mac-checks.json`), all true: build_clean_macos14_target,
glass_path_compiles_or_todo, opacity_0_clear, opacity_1_solid,
slider_in_rightclick_menu, picker_in_gear_menu, version_1_3,
install_ok_app_running, readme_updated.

## Report
Use the fixed report format. Nothing else.
