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

- float-width-mac: check whether the macOS one-line float has the same trailing grey slack as Windows (see plans/v121-look-win.md Amendment 1); match the content-derived width rule (Mac session)

## Agreed (reviewed, committed, not yet pushed)

## Done (pushed)
- v121-look-win: af17548 (closes v1.2.1-look, rings-and-update-look). Fixed: settings.json never written on Windows (net8 serializer); DWM frame on Rings disc; float tooltip; float overhang; one-line width + stacked time. Open by design: Rings disc stair-step (Region clip); Rings centre numbers overlap inner ring (Bernard to decide); update-available path unexercised (no test override).
- rings-outline + rings-select a04bbd0 (v1.2.1 tagged)

- rings-style-win c65a658, update-check-win d886588 (v1.2 tagged)
- three-gauges-win: e80b0bf (runtime look still open above)
- reauth-button 4a06a3c, reauth-401 ac32500, menu-check-margin 2377a63, signin-unify 91a3420
- reauth-mac-verify: folded into the Mac TODO (live click still pending a real sign-out)
