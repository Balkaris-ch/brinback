#!/bin/bash
# Builds "Brinback.app" from source with the Swift compiler from Apple's command line tools.
#
#   mac/build.sh              build into mac/dist
#   mac/build.sh --install    build, put it in ~/Applications and open it (no admin password)
#   mac/build.sh --selftest   build, then test the logic and the local server (touches no window)
#
# Built on your own Mac, the app carries no download quarantine, so Gatekeeper does not stop it.
# It is signed ad hoc for this Mac only. macOS asks once for Accessibility access - the one thing
# no app can grant itself - and, because each build is a new signature, again after a rebuild.
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v swiftc >/dev/null 2>&1; then
  echo "The Swift compiler is missing. Install Apple's command line tools first:"
  echo "    xcode-select --install"
  exit 1
fi

APP="dist/Brinback.app"
BIN="$APP/Contents/MacOS/Brinback"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "Compiling..."
swiftc -O -swift-version 5 -target "$(uname -m)-apple-macos13.0" \
  -framework AppKit -framework WebKit -framework ApplicationServices -framework ServiceManagement -framework Network \
  -o "$BIN" Brinback.swift

cp ../src/ui/index.html "$APP/Contents/Resources/index.html"

VERSION=$(sed -n 's/^let appVersion = "\(.*\)"/\1/p' Brinback.swift)
# The clone this app is built from: the app's Update menu item pulls and rebuilds there.
SOURCE=$(cd .. && pwd | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g')
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Brinback</string>
  <key>CFBundleDisplayName</key><string>Brinback</string>
  <key>CFBundleIdentifier</key><string>ch.balkaris.brinback</string>
  <key>CFBundleExecutable</key><string>Brinback</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT License - Balkaris</string>
  <key>BrinbackSource</key><string>${SOURCE}</string>
  <key>NSAppTransportSecurity</key>
  <dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || echo "(ad-hoc signing skipped)"
echo "Built mac/$APP"

case "${1:-}" in
  --selftest)
    "$BIN" --selftest
    ;;
  --install)
    DEST="$HOME/Applications/Brinback.app"
    mkdir -p "$HOME/Applications"
    pkill -f "Brinback.app/Contents/MacOS/Brinback" 2>/dev/null || true
    # Brinback was called Window Groups up to 1.5: the old app goes, its groups are carried over.
    pkill -f "Window Groups.app/Contents/MacOS/WindowGroups" 2>/dev/null || true
    sleep 0.5
    rm -rf "$HOME/Applications/Window Groups.app"
    rm -rf "$DEST"
    cp -R "$APP" "$DEST"
    echo "Installed $DEST"
    open "$DEST"
    echo
    echo "One step left, and only you can do it: when macOS asks, allow Brinback under"
    echo "System Settings > Privacy & Security > Accessibility. Without it no app can read or move"
    echo "another app's windows. After a rebuild, switch it off and on again there."
    ;;
esac
