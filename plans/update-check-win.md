# Brief: update-check-win

## Decision
What: Windows parity for plans/update-check-mac.md: check GitHub Releases for a
newer version on startup, every 24 h (setting, default on) and on demand; one
balloon per new version; "Update…" opens the release page. Version 1.1.0.
Why: both machines run the meter.

## Files you own
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/Program.cs
- /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter/windows/ClaudeMeter.csproj
Everything else is read-only.

## Hard rules
- Touch only the files above. Never rename or move. Never commit, push or stash.
- Never invent content; mark gaps `TODO(orchestrator)`.
- Read the code first. Write your exact-edit plan to
  /Users/bernardmcclement/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/scratch/update-check-win-builder.md. Then build.
- Tree may be dirty: yes (Rings style uncommitted in Program.cs). Build on top.
- Never run any `claude` auth command.

## Intent
Existing: `UsageAPI.http` (HttpClient with UA), `S` settings, `App` (timers,
`RefreshNow`, `BuildMenu`, balloon via `tray.ShowBalloonTip`), `FlyoutForm`
footer painting, `--once` CLI block.

1. csproj `<Version>` → 1.1.0. `AppVersion.Current` = the assembly's
   informational/file version trimmed to "major.minor[.patch]".
2. `static class UpdateChecker`: `record Release(string Tag, string Version, string Url, string Notes)`;
   `Task<Release?> Latest()` GET
   `https://api.github.com/repos/bernmc/claude-meter/releases/latest`, headers
   Accept `application/vnd.github+json`, User-Agent `ClaudeMeter/<version>`,
   timeout 15 s; 200 → parse tag_name/html_url/body; 404 → null; else throw.
   `bool IsNewer(string a, string b)` numeric per component after stripping "v".
3. Settings: `S.AutoUpdateCheck` (key "autoUpdateCheck", default true),
   `S.LastNotifiedUpdate` (string, default "").
4. `App`: `Release? AvailableUpdate; string? UpdateStatus;`
   `async void CheckForUpdates(bool manual)` mirroring the Mac rules (balloon
   once per tag unless manual; manual shows "Up to date (v…)" or "Couldn't
   check: …" in `UpdateStatus` for 6 s then clears and repaints the flyout).
   Scheduling: one-shot 10 s after start, then a 24 h WinForms Timer, both
   gated by `S.AutoUpdateCheck`; toggling the setting starts/stops the timer
   in `OnSettingsChanged`.
5. Flyout: when `AvailableUpdate != null`, a row above the footer divider:
   "Update available: v<version>" (11 px semibold) and a clickable
   "Open release page…" rect (hand cursor, same hit-test pattern as the
   footer icons) that `Process.Start`s the URL with UseShellExecute. Include
   the row in `Relayout` height. When `UpdateStatus != null` draw it in place
   of the "updated HH:MM" footer text.
6. Menu: "Check for updates automatically" check item and "Check for
   updates…" just above "Launch at login"; "Update to v<version>…" (opens the
   URL) above "Quit" only when an update is available.
7. CLI `--check-update`: prints "current <v>, latest <tag or none>, newer: yes/no",
   exit 0 (2 on error), using the same AttachConsole pattern as `--once`.

## Verification
`~/.dotnet/dotnet build -c Release --no-incremental`: zero errors, zero new
warnings. Grep for the version, the API URL, the two menu items, the flyout
row in both Relayout and OnPaint. Runtime compile-checked only; say so.
Checks JSON (`scratch/update-check-win-checks.json`), all must be true:
```json
{
  "build_clean_no_new_warnings": false,
  "version_1_1_0": false,
  "checker_and_compare": false,
  "scheduling_gated_by_setting": false,
  "flyout_row_relayout_and_paint": false,
  "menu_items": false,
  "cli_check_update": false,
  "rings_work_untouched": false
}
```

## Report
Use the fixed report format. Nothing else.
