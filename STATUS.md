# STATUS (Windows-side mirror)

The authority for this project is Bernard's AI_Context PARA set
(`01-Projects/Claude_Toolkit/Claude_Meter/TODO.md`, `DECISIONS.md`, `SESSION_LOG.md`),
which only the Mac can see. A machine without AI_Context (the Windows PC) uses this file
for its open/agreed/done; the Mac session folds it into TODO.md on every sync.
Briefs for both machines live in `plans/`.

## Open (for the Windows PC)
- v1.3-look-win (60df0d1): opacity slider (Gauge opacity submenu → TrackBar) drives all three
  float styles live; each tick writes settings.json (watch for lag); update balloon from
  1.2.1 → 1.3 should appear. Parity requests rings-polish-mac and float-width-mac are DONE
  on the Mac in 60df0d1 (labels use the text colour, not black, because the Mac disc is glass).

## Agreed (reviewed, committed, not yet pushed)

## Done (pushed)
- v1.3.1 272988e: Windows code unchanged except version 1.3.1; expect the 1.3 → 1.3.1 update balloon
- opacity-win 60df0d1 (v1.3 tagged); rings-polish-mac + float-width-mac done on Mac 60df0d1
- status-file-win: f5920f1 — current-<hostname>.json in the synced status folder (project dir required, status/ created), %APPDATA% fallback, settings keys, Status file toggle, --status. JSON matches the Mac in keys/order/types/timestamps; only whitespace style differs.
- rings-tweaks-win: cee9e18 — #3A3A3A number outline; float right-click opens the tray menu (all styles).
- rings-polish-win: 3005549 — centre numbers inside hole, curved labels (Segoe UI Regular, not Light: Light was faint on the arcs), layered Rings window + DwmFrame toggle, centred one-line text, sign-in item only when needed. Untested: dark theme (black labels on dark track). Pre-existing: no-snapshot error text overflows the Rings disc.
- v121-look-win: af17548 (closes v1.2.1-look, rings-and-update-look). Fixed: settings.json never written on Windows (net8 serializer); DWM frame on Rings disc; float tooltip; float overhang; one-line width + stacked time. Open by design: Rings disc stair-step (Region clip); Rings centre numbers overlap inner ring (Bernard to decide); update-available path unexercised (no test override).
- rings-outline + rings-select a04bbd0 (v1.2.1 tagged)

- rings-style-win c65a658, update-check-win d886588 (v1.2 tagged)
- three-gauges-win: e80b0bf (runtime look still open above)
- reauth-button 4a06a3c, reauth-401 ac32500, menu-check-margin 2377a63, signin-unify 91a3420
- reauth-mac-verify: folded into the Mac TODO (live click still pending a real sign-out)
