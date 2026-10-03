# PLAN

Authority for this repo. Decisions are made by the orchestrator; agents build only what is here.

## Decisions

| Slug | Decision | Why | Files owned | Brief | Status |
|---|---|---|---|---|---|
| reauth-button | Windows meter gets a "Sign in to Claude Code…" action (tray menu + flyout error state) that launches the official `claude auth login`, watches `.credentials.json`, and refreshes; shows an install dialog if `claude` is missing. Auth-required is detected as missing creds / no refresh token / token endpoint 400–401. | A revoked refresh token (after a credential reset) left the meter stuck with no in-app recovery. The official signed CLI is the safe recovery path; the meter must not implement its own OAuth/PKCE. | windows/Program.cs, README.md (Windows install section) | plans/reauth-button.md | open |

## Global hard rules
- Touch only owned files. Never rename or move. Never commit, push or stash. Never invent content.

## Verification conventions
- Screenshots to `scratch/`, named `<slug>-NN-<what>.png`.
- Checks JSON to `scratch/<slug>-checks.json`.
