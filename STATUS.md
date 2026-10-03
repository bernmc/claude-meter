# STATUS (Windows-side mirror)

The authority for this project is Bernard's AI_Context PARA set
(`01-Projects/Claude_Toolkit/Claude_Meter/TODO.md`, `DECISIONS.md`, `SESSION_LOG.md`),
which only the Mac can see. A machine without AI_Context (the Windows PC) uses this file
for its open/agreed/done; the Mac session folds it into TODO.md on every sync.
Briefs for both machines live in `plans/`.

## Open (for the Windows PC)
- rings-polish-mac: parity for A/B/D of plans/rings-polish-win.md, plus plans/rings-tweaks-win.md (dark-grey number outline; right-click on the float opens the menu again — Bernard reversed the earlier no-op) — centre numbers inside the inner hole, curved band labels (total / model / session from 12 o'clock clockwise, thin black), one-line float text centre-justified (Mac session)
- status-file-win: port the macOS status/current.json export to Windows. Path decision:
  `%APPDATA%\Claude Meter\status\current.json`, defaults key `statusExportPath`, same
  JSON shape as macOS (see plans/status-export.md). No brief yet.

- float-width-mac: check whether the macOS one-line float has the same trailing grey slack as Windows (see plans/v121-look-win.md Amendment 1); match the content-derived width rule (Mac session)

## Agreed (reviewed, committed, not yet pushed)

## Done (pushed)
- rings-tweaks-win: cee9e18 — #3A3A3A number outline; float right-click opens the tray menu (all styles).
- rings-polish-win: 3005549 — centre numbers inside hole, curved labels (Segoe UI Regular, not Light: Light was faint on the arcs), layered Rings window + DwmFrame toggle, centred one-line text, sign-in item only when needed. Untested: dark theme (black labels on dark track). Pre-existing: no-snapshot error text overflows the Rings disc.
- v121-look-win: af17548 (closes v1.2.1-look, rings-and-update-look). Fixed: settings.json never written on Windows (net8 serializer); DWM frame on Rings disc; float tooltip; float overhang; one-line width + stacked time. Open by design: Rings disc stair-step (Region clip); Rings centre numbers overlap inner ring (Bernard to decide); update-available path unexercised (no test override).
- rings-outline + rings-select a04bbd0 (v1.2.1 tagged)

- rings-style-win c65a658, update-check-win d886588 (v1.2 tagged)
- three-gauges-win: e80b0bf (runtime look still open above)
- reauth-button 4a06a3c, reauth-401 ac32500, menu-check-margin 2377a63, signin-unify 91a3420
- reauth-mac-verify: folded into the Mac TODO (live click still pending a real sign-out)
