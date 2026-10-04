# Brief: update-remote-check

## Decision
What: The macOS Update action must only ever pull from this project's GitHub
repository. The generated update script verifies the checkout's `origin` URL
before `git pull`, aborts with a clear message otherwise, and the build runs
only after a successful fast-forward. README documents this, the two Windows
environment overrides, and the sync caveat for the status folder. Version 1.3.1.
Why: an external audit (ChatGPT, 04/10) correctly found that Update trusted
whatever remote the checkout had and then executed the pulled build script,
contradicting the README's "no other hosts" claim. The README promises audit
findings get fixed.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/Info.plist
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else read-only (windows/ClaudeMeter.csproj is already bumped to 1.3.1 by the orchestrator).

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/update-remote-check-builder.md. Then build.
- Tree may be dirty: yes (csproj). Build on top.
- Never run `claude auth`, never open the generated update or sign-in scripts,
  never run `git pull` in Bernard's checkout.
- README style: no em/en dashes, no emoji, no marketing adjectives.

## Intent
1. `UpdateLauncher` (macOS): the generated `update.command` becomes:
   ```
   #!/bin/zsh -l
   cd "<path>" || exit 1
   expected='https://github.com/bernmc/claude-meter'
   remote="$(git remote get-url origin 2>/dev/null)"
   case "$remote" in
     "$expected"|"$expected.git"|"git@github.com:bernmc/claude-meter"|"git@github.com:bernmc/claude-meter.git") ;;
     *) echo "Refusing to update: this checkout's origin is '$remote', not $expected."; echo "Open https://github.com/bernmc/claude-meter/releases instead."; exit 2 ;;
   esac
   echo "Updating Claude Meter from $expected…"
   git pull --ff-only origin main || { echo "git pull failed; nothing was built."; exit 3; }
   cd macos && ./build.sh --install
   echo
   echo "Done. You can close this window."
   ```
   (path quoted/escaped as today). The `set -e` style is not used; the
   explicit `||` guards are the control flow. Keep writing it with 0755.
2. Also check the remote inside the app before offering the script: extend
   `repoPath()`'s usability test to run `git -C <path> remote get-url origin`
   (via Process, 5 s timeout) and require one of the four accepted forms;
   otherwise treat the checkout as unusable (button says "Open release
   page…" and opens the URL). Cache the result per launch.
3. Info.plist versions → "1.3.1".
4. README, safety section:
   - In the `github.com` table row, replace the Update wording with: "on
     macOS with a checkout, Update runs `git pull` from that checkout, and
     refuses unless the checkout's origin is this repository".
   - In "What it never does", keep the runtime-code exception but add that
     the update script checks the remote before pulling.
   - New short sub-heading in the safety section, **Environment variables
     (Windows, for tests)**: `CLAUDE_METER_CREDS_PATH` overrides where the
     credential file is read and written; `CLAUDE_METER_CLAUDE_EXE` overrides
     which `claude` executable Sign in launches. Both exist for automated
     tests; anyone who can set your environment can already do worse.
   - In the status-file bullet (safety section) and the Status file section:
     one sentence: the file never contains tokens; if the folder it lives in
     is synced by another tool, the usage numbers travel with it.
   - Audit prompt: unchanged.
   - Add a line at the end of "Check it yourself": "First external audit:
     04/10/2026, which found the unverified update remote; fixed in 1.3.1."
5. CLI: `--selftest-update-script` prints the generated script text to
   stdout without opening it, and exits 0; `--selftest-remote-check <url>`
   prints accept/reject for a given origin URL using the same matcher as
   item 2, exit 0.

## Verification
From claude-meter/macos: `./build.sh`; `--selftest-update-script` output
contains the four accepted forms and `git pull --ff-only origin main`;
`--selftest-remote-check` accepts the four forms and rejects
`https://github.com/someone/claude-meter`, `https://example.com/x.git`;
`--check-update` prints "current 1.3.1"; README greps for dashes/emoji print
nothing; `./build.sh --install`; `pgrep`; `defaults read au.bernard.claude-meter updateRepoPath`
unchanged. Confirm no `update.command` was executed (`ls` shows it only if
previously written; its mtime must not be newer than your build).
Checks JSON (`scratch/update-remote-check-checks.json`), all true:
build_clean, script_verifies_remote_and_guards_pull, app_checks_remote_before_offering,
version_1_3_1, readme_updated_no_dashes, selftests_pass, no_scripts_executed,
install_ok_app_running.

## Report
Use the fixed report format. Nothing else.
