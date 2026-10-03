# Brief: rings-outline

## Decision
What: In the Rings floating style only, every coloured arc gets a crisp
near-black hairline outline on both edges and its end caps, and the three
centre numbers keep their ring colour but gain a fine black outline.
Why: Bernard's dark-mode screenshot shows amber/orange arcs and numbers
melting into the grey disc. A soft translucent rim was tried earlier and
rejected (muddy); this is a hard, thin, opaque edge.

## Files you own
Mac agent (visual-iterator):
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
Windows agent (builder):
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/Program.cs
Each agent touches only its own file. README unchanged.

## Hard rules
- Touch only your file. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  `scratch/rings-outline-<agent>.md` under
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/. Then build.
- Tree may be dirty: yes (the other platform is edited in parallel). Build on top.
- Never run any `claude auth`, `git pull` or update script.
- Wide and square layouts, the popover, the menu bar icon: unchanged.

## Intent (both platforms)
Arc outline: under each coloured arc (same trim/sweep, same round caps,
rotation −90), draw the same arc in outline colour at width ring + 1.6
(so 0.8 on each side), i.e. 10.6 for the 9-wide rings. Outline colour:
black at 85 % opacity (`Color.black.opacity(0.85)` / `Color.FromArgb(217,0,0,0)`).
The track stays as it is. Order per ring: track, outline arc, coloured arc.

Number outline: each centre number is drawn as filled text in its ring colour
with a black stroke of about 1 pt total (0.5 each side) at 19 pt, scaling with
font size. Mac: implement with an `NSViewRepresentable` wrapping `NSTextField`
(non-editable, no background, no border) whose `attributedStringValue` sets
`.foregroundColor` = ring colour, `.strokeColor` = black, `.strokeWidth` = −6
(negative = fill + stroke; −6 is 6 % of the point size), font = system rounded
bold at the given size, monospaced digits; size the view to the text's
intrinsic size so the existing stack metrics (0.70 × font height, spacing 1)
still hold. Windows: build a `GraphicsPath` with `AddString` (Segoe UI Bold,
pixel size as today, `StringFormat` centred), `FillPath` with the ring colour
then `DrawPath` with a 1 px (scaled by DPI: `L(1)`) black pen, anti-aliased.
Centre positions unchanged.

Dark and light both: the outline is black in both modes (Bernard asked for
black); confirm legibility in both on the Mac renders.

## Verification
Mac: `./build.sh`; render `--preview-float-rings` → rings-outline-01-light.png
and the same with the preview forced to dark appearance (if the preview path
has no dark switch, add `--preview-float-rings-dark` that sets
`NSApp.appearance = NSAppearance(named: .darkAqua)` before rendering) →
rings-outline-02-dark.png; `--preview-float-rings-session` → rings-outline-03-session.png;
`--preview-float-wide` byte-identical to the committed render (cmp);
`./build.sh --install`; `pgrep`. Iterate at least three times. "Right": a thin,
even black edge hugging each arc including the caps; numbers readable at a
glance on both appearances with no halo bleeding into the inner ring; nothing
else changed.
Windows: `~/.dotnet/dotnet build -c Release --no-incremental`, zero errors,
zero new warnings; grep for the outline pen width and the AddString/DrawPath
pair; compile-checked only, say so.
Checks JSON (`scratch/rings-outline-<agent>-checks.json`), all true:
Mac: build_clean, arcs_outlined_both_edges, numbers_outlined_legible_light,
numbers_outlined_legible_dark, wide_square_unchanged, install_ok_app_running.
Windows: build_clean_no_new_warnings, arcs_outlined, numbers_fill_and_stroke,
other_layouts_untouched.

## Report
Use the fixed report format. Nothing else.
