#!/usr/bin/env bash
# Builds a Native AOT release and assembles it into an ad-hoc signed Vigil.app under artifacts/<rid>/.
# Usage: scripts/package-macos.sh osx-x64|osx-arm64
#
# macOS ties the permission to post input events to the app bundle, so Vigil is run from inside Vigil.app.
# The bundle identifier below must stay equal to the LaunchAgent label (LaunchAgentManager.Label).
set -euo pipefail

BUNDLE_ID="io.github.yasarbudak.vigil"
MIN_MACOS="12.0"

usage() {
  echo "usage: $0 osx-x64|osx-arm64" >&2
  exit 2
}

[ "$#" -eq 1 ] || usage
RID="$1"
case "$RID" in
  osx-x64 | osx-arm64) ;;
  *) usage ;;
esac

if [ "$(uname -s)" != "Darwin" ]; then
  echo "package-macos.sh must run on macOS (codesign is required)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Vigil/Vigil.csproj"
OUT="$ROOT/artifacts/$RID"
PUBLISH_DIR="$OUT/publish"
APP="$OUT/Vigil.app"

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo | tr -d '[:space:]')"
if ! [[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "unexpected project version '$VERSION'" >&2
  exit 1
fi

echo "Publishing Vigil $VERSION for $RID"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR" --nologo

# Only remove what this script created, and only below artifacts/.
case "$APP" in
  "$ROOT"/artifacts/*/Vigil.app) rm -rf "$APP" ;;
  *) echo "refusing to remove $APP" >&2; exit 1 ;;
esac

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH_DIR/vigil" "$APP/Contents/MacOS/vigil"
chmod 755 "$APP/Contents/MacOS/vigil"

# The MIT license requires its notice in every distributed copy. Copied before codesign below, so the signature seals it.
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE"
chmod 644 "$APP/Contents/Resources/LICENSE"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key>
	<string>$BUNDLE_ID</string>
	<key>CFBundleName</key>
	<string>Vigil</string>
	<key>CFBundleExecutable</key>
	<string>vigil</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleVersion</key>
	<string>$VERSION</string>
	<key>CFBundleShortVersionString</key>
	<string>$VERSION</string>
	<key>LSMinimumSystemVersion</key>
	<string>$MIN_MACOS</string>
	<key>LSUIElement</key>
	<true/>
</dict>
</plist>
PLIST
plutil -lint "$APP/Contents/Info.plist" >/dev/null

# Ad-hoc signature (no developer identity). Every rebuild changes it, and macOS then treats the app as new:
# the Accessibility permission has to be granted again.
codesign --force --sign - --identifier "$BUNDLE_ID" "$APP"
codesign --verify --strict "$APP"

echo "Built $APP"
