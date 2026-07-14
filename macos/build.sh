#!/bin/zsh
# Build Claude Meter.app from main.swift. Usage:
#   ./build.sh            build only (app/build/Claude Meter.app)
#   ./build.sh --install  build and copy to ~/Applications, then launch
set -e
cd "$(dirname "$0")"

APP="build/Claude Meter.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp Info.plist "$APP/Contents/Info.plist"

echo "Compiling…"
# Pin the deployment target: the beta toolchain otherwise targets a newer
# macOS than the installed one and Launch Services refuses to open the app.
# Universal binary so it runs on both Apple Silicon and Intel Macs.
swiftc -O -swift-version 5 -target arm64-apple-macos14.0 main.swift -o "$APP/Contents/MacOS/cm-arm64"
swiftc -O -swift-version 5 -target x86_64-apple-macos14.0 main.swift -o "$APP/Contents/MacOS/cm-x86_64"
lipo -create -output "$APP/Contents/MacOS/Claude Meter" "$APP/Contents/MacOS/cm-arm64" "$APP/Contents/MacOS/cm-x86_64"
rm "$APP/Contents/MacOS/cm-arm64" "$APP/Contents/MacOS/cm-x86_64"

codesign --force --sign - "$APP"
echo "Built $APP"

if [[ "$1" == "--install" ]]; then
  mkdir -p ~/Applications
  # Quit a running copy so the binary can be replaced cleanly.
  pkill -x "Claude Meter" 2>/dev/null || true
  sleep 1
  rm -rf ~/Applications/"Claude Meter.app"
  ditto "$APP" ~/Applications/"Claude Meter.app"
  echo "Installed to ~/Applications/Claude Meter.app — launching…"
  open ~/Applications/"Claude Meter.app"
fi
