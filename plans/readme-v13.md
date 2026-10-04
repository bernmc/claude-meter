# Brief: readme-v13

## Decision
What: Rewrite README.md for v1.3: add the four new screenshots as a gallery of
floating-gauge styles, bring Features up to date, add a section "Is it safe to
run?" containing the exact copy and the audit prompt given below, and strip the
whole file of em-dashes, emoji and promotional adjectives.
Why: the app now has several visual styles the README never shows, and people
are rightly wary of a third-party app that uses their Claude sign-in. The
reassurance must be factual and checkable, not soothing.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else is read-only. Read macos/main.swift and windows/Program.cs
only to verify the facts you state; do not edit them.

## Hard rules
- Touch only README.md. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`. Every factual claim
  about behaviour must be checked against the source; if the source disagrees
  with this brief, follow the source and say so in the report.
- Read the current README and the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/readme-v13-builder.md. Then build.
- Tree may be dirty: no.
- Style: no em-dashes (U+2014) or en-dashes used as dashes anywhere in the
  file; use commas, colons or full stops. No emoji. No words like seamless,
  elegant, beautiful, powerful, effortless, delight, supercharge, elevate.
  Short sentences. Tables and lists where they help. Keep the existing tone
  (plain, second person).

## Intent
Section order and content:

1. `# Claude Meter`, the current intro paragraph (keep), then the two existing
   screenshots (macOS, Windows) as now.
2. `## Floating gauge styles`: a 2×2 gallery using an HTML table so the images
   sit side by side (GitHub renders it): `docs/single.png` captioned
   "One line", `docs/square.png` "Square", `docs/ring.png` "Rings (outer total,
   then the model, then the session)", `docs/right-click.png` "Right-click
   menu with the opacity slider". Set each image width to 300 via the img tag.
   One sentence above the table: all styles are Liquid Glass on macOS 26 and
   later, frosted on older macOS, and their opacity is adjustable.
3. `## Features`: update to the current app. Must mention: three ring gauges
   in the popover (session, weekly all models, the per-model weekly limit,
   any further models as bars); choose which limits are shown as gauges;
   three floating styles; Rings centre option; opacity slider; usage warnings
   with threshold; one-click sign-in when Claude Code is signed out; update
   check against GitHub Releases; status file for other programs; launch at
   login. Keep it a bulleted list, one line each where possible.
4. `## Requirements`, `## Install` (macOS, Windows, the `--once` test, the
   "Sign-in expired?" sub-section): keep, correcting any stale wording
   (sign-in instruction is `claude auth login`; plain `claude` no longer
   prompts).
5. `## Is it safe to run?` Insert this text exactly, then verify every claim
   in it against the source and adjust only if the source contradicts it
   (report any adjustment):

   ---
   This app handles your Claude Code sign-in, so you should be able to check
   what it does rather than take it on trust. The whole app is two source
   files, `macos/main.swift` and `windows/Program.cs`, plus a build script
   per platform. A person can read them in an afternoon, and an AI assistant
   can read them in a minute.

   **Every network destination in the code**

   | Host | When | What is sent |
   |---|---|---|
   | `api.anthropic.com` (usage endpoint) | every 60 s | your Claude Code access token, as a bearer header |
   | `platform.claude.com` (token endpoint) | only when the access token has expired | your refresh token, to get a new pair |
   | `api.github.com` (releases/latest) | 10 s after launch, then daily, or on demand; can be turned off | nothing but the app's version in the user-agent |
   | `github.com` | only when you click Update or Open release page | opens the page in your browser; no data sent by the app |

   There are no other hosts, no analytics, no crash reporting and no
   telemetry. Your tokens are sent to Anthropic's endpoints only.

   **Everything it writes**

   - Your Claude Code credential store, only to save a refreshed token pair
     in the same format Claude Code uses (the macOS keychain item
     `Claude Code-credentials`; on Windows the file
     `%USERPROFILE%\.claude\.credentials.json`).
   - Its own settings and usage history in `~/Library/Application Support/Claude Meter/`
     (macOS) or `%APPDATA%\Claude Meter\` (Windows).
   - The optional status file described below.
   - On macOS, two small shell scripts it generates and opens in Terminal
     when you click Sign in or Update: one runs `claude auth login`, the
     other runs `git pull` and the build script in your checkout. You see
     them run.

   **What it never does**

   It never reads any other keychain item or file, never installs anything
   persistent beyond the optional launch-at-login entry, never downloads or
   runs code at runtime, and never contacts a host that is not in the table.

   **Check it yourself**

   Paste this into Claude (or another assistant that can read a public
   repository) and read the verdict before you build the app:

   ```
   I am considering running Claude Meter, an open-source menu bar app that reads my Claude Code sign-in from my keychain (macOS) or from ~/.claude/.credentials.json (Windows) to show my plan usage. Audit it for anything malicious or unsafe before I run it.

   Repository: https://github.com/bernmc/claude-meter
   The whole app is two source files, macos/main.swift and windows/Program.cs, plus macos/build.sh, windows/build.ps1, windows/ClaudeMeter.csproj and .github/workflows/release.yml. Read all of them in full.

   Report, with file and line references:
   1. Every network destination the code can contact and what is sent to each. Flag anything other than api.anthropic.com, platform.claude.com, api.github.com, or opening a github.com page in the browser.
   2. Everything it does with my credentials: where it reads them, what it stores, where it writes, and whether a token could leave the machine anywhere except Anthropic's own endpoints.
   3. Every file it writes or executes, including any scripts it generates and runs.
   4. Anything obfuscated, encoded, fetched at runtime, or that changes behaviour based on time, locale or environment.
   5. Whether the build scripts and the GitHub Actions workflow do anything beyond compiling and publishing the executables.
   Finish with a plain verdict: safe to run as published, or not, and why.
   ```

   If the verdict mentions anything not covered above, open an issue; that
   is either a bug in this README or in the app, and both get fixed.
   ---

6. `## How it works`: keep the current facts (both platforms, the 60 s poll,
   the refresh and write-back, the note about not copying credential files
   between machines), trimmed so it does not repeat the safety section.
7. `## Updates`, `## Status file`, `## Disclaimer`, `## Uninstall`,
   `## License`: keep, correct stale details only. Uninstall must now also
   remove the two generated scripts (they live in the Application Support
   folder, so the existing `rm -rf` already covers them; say so).

## Verification
Run from the repo root:
- `grep -nP '\x{2014}|\x{2013}' README.md` must print nothing.
- `grep -nP '[\x{1F300}-\x{1FAFF}\x{2600}-\x{27BF}]' README.md` must print nothing.
- Every `docs/*.png` referenced exists (`ls` each path).
- `grep -ohE 'https://[a-z0-9./-]+' macos/main.swift windows/Program.cs | sort -u`
  lists exactly the hosts named in the safety table plus the docs link
  `code.claude.com` (that one is a link shown to the user, not a request;
  mention it in the table only if the code fetches it; report either way).
- The audit prompt block in README.md is byte-identical to the one in this
  brief (diff it).
- Render check: `python3 -c "import markdown"` is not required; instead view
  the HTML table block by eye and confirm it is valid HTML.
Checks JSON (`scratch/readme-v13-checks.json`), all true: no_dashes_no_emoji,
gallery_four_images_exist, features_current, safety_section_verbatim_and_verified,
hosts_table_matches_source, audit_prompt_identical, stale_wording_fixed.

## Report
Use the fixed report format. Nothing else.
