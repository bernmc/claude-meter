# Brief: status-export

## Decision
What: Claude Meter (macOS only) writes its current usage snapshot to a JSON status
file on every refresh attempt, atomically, so external processes can read live
usage numbers. Plus a `--status` CLI mode, a gear-menu toggle, and a README section.
Why: other local tooling wants the numbers without talking to Anthropic's endpoint
or the keychain itself. The app already holds the data every 60 s.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/macos/main.swift
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/README.md
Everything else is read-only. Do NOT touch windows/, HistoryStore, history.json,
or the UsageAPI layer.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to `scratch/status-export-builder.md`
  (scratch dir: /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/). Then build.
- Tree may be dirty: no.

## Intent
main.swift is a single-file AppKit+SwiftUI app. Relevant existing pieces you will
find in it: `UsageSnapshot` (fetchedAt, limits; computed session / weeklyAll /
scoped), `LimitEntry` (kind, label, percent, resetsAt), `UsageModel.refresh()`
(success path calls `history.record` and `Notifier.check`; failure path sets
`errorText`), a SwiftUI gear `Menu` in `PopoverView` holding Toggles/Pickers bound
to @AppStorage, and a `--once` CLI block at the entry point.

Add a new `enum StatusExporter` section:

1. Path resolution: UserDefaults key `statusExportPath` (String). Default
   `~/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/status/current.json`.
   Tilde-expand with `(path as NSString).expandingTildeInPath`.
2. Skip rule: let statusDir = file's parent (`.../status`), projectDir = statusDir's
   parent. If projectDir does not exist, return silently (machine without Synology
   Drive must behave exactly as today). Create statusDir if missing.
3. Enabled rule: UserDefaults `statusExportEnabled`, Bool, default true (absent key
   counts as true). When false, skip export (both success and failure paths).
4. `static func exportSuccess(_ snap: UsageSnapshot, plan: String?)` — builds the
   full document and writes it.
5. `static func exportFailure(_ message: String)` — reads the existing
   current.json if parseable; keeps every prior field; replaces `error` with the
   message and `checked_at` with now; writes. If no readable prior file: write the
   full schema with `fetched_at`, `plan`, `session`, `weekly_all` as NSNull,
   `models` as [], `error` set.
6. Document shape — build a `[String: Any]`, serialize with JSONSerialization
   `[.prettyPrinted, .sortedKeys]`, NSNull() for missing values:
   - `fetched_at`: snap.fetchedAt, ISO 8601 UTC with trailing Z, seconds precision
     (ISO8601DateFormatter, timeZone UTC). Same formatter for all timestamps.
   - `checked_at`: Date() at write time (success and failure).
   - `plan`: the plan string as-is (e.g. "max") or NSNull.
   - `session`: {"percent": Double, "resets_at": ISO string or NSNull} from
     snap.session; NSNull if snap.session is nil.
   - `weekly_all`: same from snap.weeklyAll.
   - `models`: array from snap.scoped, order preserved, each
     {"name": String, "percent": Double, "resets_at": ISO or NSNull}. `name` is the
     entry's label with the leading "Week — " prefix stripped (labels look like
     "Week — Fable"); if the prefix is absent use the label unchanged.
   - `error`: NSNull on success, String on failure.
7. Atomic write: serialize to Data; write to a temp file in the SAME directory
   (`current.json.tmp`); then `FileManager.default.replaceItemAt(dest, withItemAt: tmp)`
   (fall back to removeItem+moveItem only if replaceItemAt throws). Any thrown
   error anywhere in the exporter is swallowed — the exporter must never crash or
   surface errors to the UI.
8. Threading: call the exporter off the main actor is NOT required; call it inline
   from `refresh()` like `history.record` is (file is tiny). Success path: call
   `StatusExporter.exportSuccess(snap, plan: plan)` right after `history.record`.
   Failure path (the catch block): call `StatusExporter.exportFailure(error.localizedDescription)`
   after `errorText` is set.
9. Gear menu: add a Toggle "Status file" bound to
   `@AppStorage("statusExportEnabled") = true`, placed with the other config items
   directly above the Divider that precedes "Launch at login".
10. `--status` CLI mode at the entry point, structured exactly like `--once`:
    perform one fetch; on success call exportSuccess, on error call exportFailure
    (ignore the `statusExportEnabled` toggle in this mode — explicit invocation —
    but keep the missing-projectDir skip rule for the file write); then print the
    same JSON document to stdout (print the serialized string, success or failure
    shape accordingly) and exit(0) on success / exit(1) on fetch error. Reuse one
    document-building function so file and stdout can never diverge.
11. README.md: add a short "## Status file" section between "How it works" and
    "Disclaimer": default path, note it is macOS-only for now, the JSON schema
    (one fenced example), the two defaults keys (`statusExportPath`,
    `statusExportEnabled`), the gear toggle, and `--status`. Match the README's
    existing tone; keep it under ~25 lines.

## Verification
Run (from claude-meter/macos):
1. `./build.sh` — must compile clean.
2. `"./build/Claude Meter.app/Contents/MacOS/Claude Meter" --status` — prints JSON, exit 0.
3. `python3 -m json.tool "$HOME/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/status/current.json"` — validates.
4. `python3 -c` assertion: keys exactly {fetched_at, checked_at, plan, session, weekly_all, models, error}; error is null; session.percent is a number; every timestamp ends in Z; each models[] name does not start with "Week".
5. Failure path test: run with `HOME` unchanged but simulate by temporarily running
   `"...Claude Meter" --status` while network is fine is not enough — instead unit-check
   exportFailure by hand: move current.json aside, write a doctored copy with error null,
   then call the binary with env var `CLAUDE_METER_FORCE_FAIL=1`? NO — do not add test
   env vars to the app. Verify the failure path by code inspection in your report plus:
   temporarily rename `~/.claude` is forbidden. Accept: state in notes that the failure
   path was verified by inspection only.
6. `./build.sh --install` — replaces the running app; `pgrep -x "Claude Meter"` shows it running.
7. After ≥70 s, `python3` check: current.json checked_at is newer than at step 3
   (proves the running app exports on its timer).
Measure: exit codes, the JSON assertions, the checked_at delta.
Screenshots (named): none — non-visual change; the gear-menu toggle is verified by
grepping your own edit and by the app running.
Checks JSON (`scratch/status-export-checks.json`), all must be true:
```json
{
  "build_clean": false,
  "status_cli_exit0_prints_json": false,
  "file_validates_and_schema_exact": false,
  "timestamps_utc_z": false,
  "model_names_stripped": false,
  "running_app_updates_file_on_timer": false,
  "install_ok_app_running": false,
  "windows_and_history_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.
