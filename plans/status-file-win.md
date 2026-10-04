# Brief: status-file-win

## Decision
What: Port the macOS status-file export to Windows in `windows/Program.cs`. On every refresh attempt the meter atomically writes the current snapshot as JSON — the same shape as macOS (`plans/status-export.md`; a live example synced from the Mac is at `%USERPROFILE%\SynologyDrive\AI_Context\01-Projects\Claude_Toolkit\Claude_Meter\status\current.json`). Default path on Windows: that same synced folder, file name `current-<hostname>.json` with the hostname lower-cased (this PC → `current-win-cnc.json`), so each machine (Windows PC, Mac, Mac VM) writes its own file and Synology sync never sees two writers on one file. If the Synology folder does not exist on a machine, fall back to `%APPDATA%\Claude Meter\status\current-<hostname>.json`. Settings keys `statusExportPath` (string override, full path) and `statusExportEnabled` (bool, default true); a "Status file" check item in the tray/float menu that toggles `statusExportEnabled`; `--status` command-line flag does a one-shot fetch, writes the file, prints the same JSON to stdout, exits 0 (non-zero on failure), without starting the UI or taking the single-instance mutex.
Why: Bernard chose the synced folder (Option B) so every machine's numbers land in one place; per-machine names avoid sync conflicts. Parity with macOS (3ca0bb0).

## Files you own
- windows/Program.cs
- README.md — only the `## Status file` section: change "**macOS only, for now.**" to describe both platforms and the Windows naming/fallback; nothing else in the file.
Everything else is read-only.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read `plans/status-export.md`, the synced `current.json` example, `macos/main.swift`'s export code (read-only, for shape and atomic-write behaviour), and the Windows `App.RefreshNow`/settings code first. Write your exact-edit plan to `scratch/status-file-win-builder.md`. Then build.
- Tree may be dirty: no.
- Never run `claude auth login`/`logout` or write the real `%USERPROFILE%\.claude\.credentials.json`.
- Never write to or modify the Mac's `current.json` in the synced folder. Your file is `current-win-cnc.json` only. Do not delete anything in that folder.
- Back up `%APPDATA%\Claude Meter\settings.json` to `scratch/settings.backup.json` and restore it at the end.
- build.ps1 via `powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Install]`; finish with `-Install`.

## Intent
- JSON shape: byte-for-byte the same keys, nesting, value types and ISO-8601 UTC timestamp format as macOS (`fetched_at`, `checked_at`, `plan`, `error`, `session{percent,resets_at}`, `weekly_all{...}`, `models[{name,percent,resets_at}]`). On an error, `error` holds the message and the last good numbers stay (match the Mac: read its code to confirm whether it nulls them).
- Atomic write: write to `<path>.tmp` in the same directory, then `File.Move(tmp, path, overwrite: true)`.
- Write after every refresh attempt (success or failure), and from `--status`.
- Path resolution each write: `statusExportPath` if set and non-empty → else synced default if its parent `status` directory exists → else `%APPDATA%` fallback (create the `status` directory). Hostname from `Environment.MachineName`, lower-cased, non-alphanumerics replaced with `-`.
- Menu item "Status file" (checked = enabled) placed next to "Check for updates automatically"; toggling writes settings and, when turning on, writes the file immediately.
- `--status`: like `--check-update`/`--find-claude` in style; prints the JSON and nothing else to stdout.

## Verification
Run (from `windows/`):
1. build.ps1 — 0 errors, 0 warnings.
2. `& ".\build\Claude Meter.exe" --status` → prints JSON; compare key set and timestamp format with the Mac's `current.json` (`scratch/status-file-win-diff.txt` with both side by side).
3. Confirm `...\Claude_Meter\status\current-win-cnc.json` exists, is valid JSON, and the Mac's `current.json` mtime is unchanged.
4. Set `statusExportPath` to `scratch\status-test.json` in settings, run `--status`, confirm it writes there; remove the key.
5. Fallback: run with `statusExportPath` unset and env `USERPROFILE` pointed at an empty temp dir (`$env:USERPROFILE='C:\Users\Bernard\AppData\Local\Temp\...'`) so the synced folder isn't found → file lands under that temp profile's `AppData\Roaming\Claude Meter\status\`. (If `%APPDATA%` is resolved independently of USERPROFILE, set `APPDATA` too.)
6. build.ps1 -Install; open the menu, screenshot the "Status file" item checked; toggle it off, confirm the file stops updating (mtime) after a Refresh now; toggle back on.
Screenshots: status-file-win-01-menu-item.png, status-file-win-02-folder-listing.png (Explorer or `Get-ChildItem` output of the synced status folder showing both files).
Checks JSON (`scratch/status-file-win-checks.json`), all true:
```json
{
  "build_ok": false,
  "json_shape_matches_mac": false,
  "writes_current_win_cnc_in_synced_folder": false,
  "mac_current_json_untouched": false,
  "override_path_honoured": false,
  "fallback_path_when_no_synology": false,
  "menu_toggle_persists_and_stops_writes": false,
  "status_flag_prints_json_exit_0": false,
  "settings_restored": false,
  "real_credentials_file_untouched": false,
  "readme_status_section_covers_windows": false
}
```

## Report
Use the fixed report format. Nothing else.
