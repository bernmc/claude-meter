# STATUS (Windows-side mirror)

The authority for this project is Bernard's AI_Context PARA set
(`01-Projects/Claude_Toolkit/Claude_Meter/TODO.md`, `DECISIONS.md`, `SESSION_LOG.md`),
which only the Mac can see. A machine without AI_Context (the Windows PC) uses this file
for its open/agreed/done; the Mac session folds it into TODO.md on every sync.
Briefs for both machines live in `plans/`.

## Open (for the Windows PC)
- v1.2.1-look (a04bbd0): first real update-available test — the PC on 1.2 should get a balloon
  and "Update to v1.2.1…"; then check Rings outlines (arc edge, outlined numbers, 1-px
  glyph drift), Gauges submenu lock, 1- and 2-gauge layouts in flyout and all float styles.
- rings-and-update-look (c65a658, d886588): run v1.2 on Windows and check: Rings style
  (disc edge may look jagged from the Region clip; drag still saves position; hover
  tooltip appears; right-click on the float does nothing now, tray menu still works),
  Rings centre option, "Check for updates…" and the auto-check toggle, the
  "Update available" flyout row (only visible when a newer release exists), balloon
  click routing, and `--check-update`. Fix in place; briefs in plans/.
- status-file-win: port the macOS status/current.json export to Windows. Path decision:
  `%APPDATA%\Claude Meter\status\current.json`, defaults key `statusExportPath`, same
  JSON shape as macOS (see plans/status-export.md). No brief yet.

## Agreed (reviewed, committed, not yet pushed)

## Done (pushed)
- rings-outline + rings-select a04bbd0 (v1.2.1 tagged)

- rings-style-win c65a658, update-check-win d886588 (v1.2 tagged)
- three-gauges-win: e80b0bf (runtime look still open above)
- reauth-button 4a06a3c, reauth-401 ac32500, menu-check-margin 2377a63, signin-unify 91a3420
- reauth-mac-verify: folded into the Mac TODO (live click still pending a real sign-out)
