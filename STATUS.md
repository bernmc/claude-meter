# STATUS (Windows-side mirror)

The authority for this project is Bernard's AI_Context PARA set
(`01-Projects/Claude_Toolkit/Claude_Meter/TODO.md`, `DECISIONS.md`, `SESSION_LOG.md`),
which only the Mac can see. A machine without AI_Context (the Windows PC) uses this file
for its open/agreed/done; the Mac session folds it into TODO.md on every sync.
Briefs for both machines live in `plans/`.

## Open (for the Windows PC)
- status-file-win: port the macOS status/current.json export to Windows. Path decision:
  `%APPDATA%\Claude Meter\status\current.json`, defaults key `statusExportPath`, same
  JSON shape as macOS (see plans/status-export.md). No brief yet.
- three-gauges-win-look: e80b0bf landed three rings in the flyout and float, compile-checked
  only. Run it: check the 25 px number fits the smaller L(72) ring, labels don't overflow at
  L(104) spacing, the flyout re-widens if open when a third ring appears, "Model week" in
  the tray menu. Fix in place if needed (brief plans/three-gauges-win.md).

## Agreed (reviewed, committed, not yet pushed)

## Done (pushed)
- three-gauges-win: e80b0bf (runtime look still open above)
- reauth-button 4a06a3c, reauth-401 ac32500, menu-check-margin 2377a63, signin-unify 91a3420
- reauth-mac-verify: folded into the Mac TODO (live click still pending a real sign-out)
