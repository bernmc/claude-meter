# Claude Meter

A tiny macOS menu bar app that shows your **Claude plan usage** at a glance —
the same numbers as the Claude app's *Settings → Usage* screen, without having
to go looking for them.

![Claude Meter](docs/screenshot.png)

## Features

- **Menu bar**: a traffic-light ring (green → amber → red) plus the percentage
  of whichever limit is closest to its ceiling (configurable).
- **Popover** (click the menu bar item): animated ring gauges for the
  5-hour session window and the weekly limit, per-model weekly bars, reset
  countdowns, your plan badge, and a 24-hour usage sparkline.
- **Floating desktop gauge**: a small always-on-top frosted panel with mini
  gauges and the reset countdown. Drag it anywhere; position is remembered.
  Two layouts: one-line or square.
- **Usage warnings**: a notification when any limit crosses a threshold
  (80/90/95%, or off). Warns once per approach, re-arms after the reset.
- **Configurable** from the gear menu: desktop gauge on/off and layout, which
  limit the menu bar tracks, percent text on/off, warning threshold, launch
  at login.

Works with any Claude subscription (Pro, Max, …) — it displays whatever
limits your plan reports. Dates and times follow your system locale.

## Requirements

- macOS 14 or later
- [Claude Code](https://claude.com/claude-code) installed and signed in at
  least once (that's where the credentials come from)
- Xcode Command Line Tools to build (`xcode-select --install`)

## Install

```sh
git clone https://github.com/bernmc/claude-meter.git
cd claude-meter
./build.sh --install
```

That compiles a universal binary, ad-hoc signs it, installs to
`~/Applications/Claude Meter.app`, and launches it. No Xcode project, no
dependencies — one Swift file.

To test the data path without the UI:

```sh
"$HOME/Applications/Claude Meter.app/Contents/MacOS/Claude Meter" --once
```

## How it works (and what it touches)

You should know exactly what an app near your credentials does:

- It reads Claude Code's OAuth credentials from your **login keychain**
  (service `Claude Code-credentials`) using `/usr/bin/security` — the same
  entry Claude Code itself maintains. Nothing is sent anywhere except to
  Anthropic's own endpoints.
- Every 60 s it calls `GET https://api.anthropic.com/api/oauth/usage` — the
  endpoint the Claude app's usage screen uses — with your token.
- When the access token expires it refreshes it via
  `POST https://platform.claude.com/v1/oauth/token` (Claude Code's public
  OAuth client id) and **writes the rotated tokens back to the keychain** so
  Claude Code stays signed in. This mirrors what Claude Code does itself.
- Usage history for the sparkline is stored locally in
  `~/Library/Application Support/Claude Meter/history.json` (7-day retention).

No analytics, no third-party services, no network calls other than the two
Anthropic endpoints above.

## Disclaimer

This is an **unofficial** tool, not affiliated with or endorsed by Anthropic.
It uses undocumented endpoints that Anthropic may change or remove at any
time, which would break the app without notice. Use at your own risk.

## Uninstall

Quit the app, then:

```sh
rm -rf ~/Applications/"Claude Meter.app" ~/Library/Application\ Support/"Claude Meter"
defaults delete au.bernard.claude-meter
```

Your Claude Code keychain entry is left untouched.

## License

[MIT](LICENSE)
