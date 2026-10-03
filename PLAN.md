# PLAN

Authority for this repo. Decisions are made by the orchestrator; agents build only what is here.

## Decisions

| Slug | Decision | Why | Files owned | Brief | Status |
|---|---|---|---|---|---|
| reauth-button | Windows meter gets a "Sign in to Claude Code…" action (tray menu + flyout error state) that launches the official `claude auth login`, watches `.credentials.json`, and refreshes; shows an install dialog if `claude` is missing. Auth-required is detected as missing creds / no refresh token / token endpoint 400–401. | A revoked refresh token (after a credential reset) left the meter stuck with no in-app recovery. The official signed CLI is the safe recovery path; the meter must not implement its own OAuth/PKCE. | windows/Program.cs, README.md (Windows install section) | plans/reauth-button.md | agreed 4a06a3c |
| reauth-401 | On usage 401, force one token refresh and retry; second 401 or refresh 400/401 → AuthRequiredException so the sign-in UI appears. | Revocation while the access token is unexpired showed a bare HTTP 401 with no recovery. | windows/Program.cs | plans/reauth-401.md | agreed ac32500 |
| menu-check-margin | Tray/gear context menus get a dedicated check column (ShowCheckMargin on, image margin off; renderer adjusted if needed) so check marks never overlap item text. | Check glyph painted over the first letter of checked items at >100% DPI. | windows/Program.cs | plans/menu-check-margin.md | agreed 2377a63 |

## Global hard rules
- Touch only owned files. Never rename or move. Never commit, push or stash. Never invent content.

## Verification conventions
- Screenshots to `scratch/`, named `<slug>-NN-<what>.png`.
- Checks JSON to `scratch/<slug>-checks.json`.
