# Brief: reauth-401

## Decision
What: In `windows/Program.cs`, treat an HTTP 401 from the usage endpoint (`https://api.anthropic.com/api/oauth/usage`) as a possible revocation: on 401, discard the cached/unexpired access token, force one token refresh via the existing refresh path, and retry the usage request once. If the forced refresh throws `AuthRequiredException` (token endpoint 400/401), surface it unchanged so the existing sign-in UI appears. If the retry also 401s, throw `AuthRequiredException` with message `Usage request rejected (HTTP 401) after token refresh.` Any other outcome keeps current behaviour.
Why: The reauth-button work (commit in STATUS.md) only detects a dead session when the *refresh* fails. If Anthropic revokes a session while the access token is still unexpired, the usage call 401s and the meter shows a bare "Usage request failed (HTTP 401)" with no sign-in button. Closing this makes the sign-in action appear for both ways a revocation shows up.

## Files you own
- windows/Program.cs
Everything else is read-only.

## Hard rules
- Touch only the file above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first (start at `UsageAPI.ValidToken` and the usage-fetch method that follows it, and `AuthRequiredException`). Write your exact-edit plan to `scratch/reauth-401-builder.md`. Then build.
- Tree may be dirty: no (previous work is committed).
- NEVER run `claude auth login` / `claude auth logout` or write the real `%USERPROFILE%\.claude\.credentials.json`. Record its LastWriteTimeUtc before and after.
- Finish with `.\build.ps1 -Install` (run via `powershell -ExecutionPolicy Bypass` as before) so the installed meter is the new build.

## Intent
- Add a `forceRefresh` parameter (default false) to `ValidToken`; when true, skip the "access token still valid" shortcut and go straight to the refresh POST. Keep the write-back behaviour.
- In the usage fetch: call as today; if status == 401 and this is the first attempt, call `ValidToken(forceRefresh: true)` and repeat the request once. Second 401 → `AuthRequiredException` as specified. Do not loop more than once.
- Do not change any UI, strings, or the `AuthRequired` model logic — the exception type already drives it.
- Keep the change minimal (expect ~15–25 lines).

## Verification
Run (from `windows/`):
1. `.\build.ps1` — 0 errors.
2. Unit-style check without touching real creds: with `CLAUDE_METER_CREDS_PATH` pointing at `scratch/fake-creds-401.json` containing an **unexpired** fake token (`expiresAt` = now + 1 h in Unix ms, `accessToken` = `"bogus"`, `refreshToken` = `"dead"`), run the built exe. The usage call will 401 on `bogus`, the forced refresh will 400 on `dead`, and the flyout must show `Claude Code sign-in has expired or been revoked.` with the Sign in button (previously it would have shown `Usage request failed (HTTP 401).`). Screenshot it.
3. `.\build.ps1 -Install`, confirm the running meter shows normal usage; screenshot.
Screenshots: reauth-401-01-unexpired-token-401-shows-signin.png, reauth-401-02-normal-after-install.png
Checks JSON (`scratch/reauth-401-checks.json`), all must be true:
```json
{
  "build_ok": false,
  "unexpired_bogus_token_shows_signin_button": false,
  "normal_state_after_install": false,
  "real_credentials_file_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.
