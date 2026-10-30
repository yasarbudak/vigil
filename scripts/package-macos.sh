#!/usr/bin/env bash
# Builds Native AOT releases and assembles them into two ad-hoc signed bundles under artifacts/<rid>/, and zips both:
#   Vigil.app        the daemon and the CLI (CFBundleExecutable vigil)
#   Vigil Tray.app   the menu bar icon and settings window (CFBundleExecutable vigil-tray, no Dock icon)
#   Vigil-<version>-<rid>.zip   both bundles at the top level
# Usage: scripts/package-macos.sh osx-x64|osx-arm64
#
# macOS ties the permission to post input events to the app bundle, so Vigil is run from inside Vigil.app. That bundle is
# exactly what it was before the tray existed (same executable name, identifier and contents), so the permission, the LaunchAgent
# and the `open -n -o ... Vigil.app --args diag` check keep working. The tray is a separate bundle on purpose: it needs no
# permission, and its Avalonia libraries (about 20 MB) and its own identity stay out of the bundle that holds the permission.
# The bundle identifier of Vigil.app must stay equal to the LaunchAgent label (LaunchAgentManager.Label).
set -euo pipefail

BUNDLE_ID="io.github.yasarbudak.vigil"
TRAY_BUNDLE_ID="io.github.yasarbudak.vigil.tray"
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
TRAY_PROJECT="$ROOT/src/Vigil.Tray/Vigil.Tray.csproj"
OUT="$ROOT/artifacts/$RID"
PUBLISH_DIR="$OUT/publish"
TRAY_PUBLISH_DIR="$OUT/publish-tray"
APP="$OUT/Vigil.app"
TRAY_APP="$OUT/Vigil Tray.app"

# The license of every third-party component in the binaries must be shipped with them (THIRD-PARTY-NOTICES.txt); no notices, no package.
for notice in LICENSE THIRD-PARTY-NOTICES.txt; do
  if [ ! -f "$ROOT/$notice" ]; then
    echo "$ROOT/$notice is missing (the licenses of the components Vigil ships require their notices in every distributed copy)" >&2
    exit 1
  fi
done

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo | tr -d '[:space:]')"
if ! [[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "unexpected project version '$VERSION'" >&2
  exit 1
fi

ZIP="$OUT/Vigil-$VERSION-$RID.zip"

echo "Publishing Vigil $VERSION for $RID"
rm -rf "$PUBLISH_DIR" "$TRAY_PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR" --nologo
dotnet publish "$TRAY_PROJECT" -c Release -r "$RID" -o "$TRAY_PUBLISH_DIR" --nologo

# Only remove what this script created, and only below artifacts/.
case "$APP" in
  "$ROOT"/artifacts/*/Vigil.app) rm -rf "$APP" ;;
  *) echo "refusing to remove $APP" >&2; exit 1 ;;
esac
case "$TRAY_APP" in
  "$ROOT"/artifacts/*/"Vigil Tray.app") rm -rf "$TRAY_APP" ;;
  *) echo "refusing to remove $TRAY_APP" >&2; exit 1 ;;
esac
case "$ZIP" in
  "$ROOT"/artifacts/*/Vigil-*-"$RID".zip) rm -f "$OUT"/Vigil-*-"$RID".zip ;;
  *) echo "refusing to remove $ZIP" >&2; exit 1 ;;
esac

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH_DIR/vigil" "$APP/Contents/MacOS/vigil"
chmod 755 "$APP/Contents/MacOS/vigil"

# The MIT license requires its notice in every distributed copy, and so do the licenses of the third-party components (the notices
# file). Both are copied before codesign below, so the signature seals them.
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE"
chmod 644 "$APP/Contents/Resources/LICENSE"
cp "$ROOT/THIRD-PARTY-NOTICES.txt" "$APP/Contents/Resources/THIRD-PARTY-NOTICES.txt"
chmod 644 "$APP/Contents/Resources/THIRD-PARTY-NOTICES.txt"

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

# The tray. Avalonia's native libraries (the publish output holds exactly three .dylib files) sit next to the executable, where
# the Native AOT program looks for them. Each is signed first, then the bundle seals them.
mkdir -p "$TRAY_APP/Contents/MacOS" "$TRAY_APP/Contents/Resources"
cp "$TRAY_PUBLISH_DIR/vigil-tray" "$TRAY_APP/Contents/MacOS/vigil-tray"
chmod 755 "$TRAY_APP/Contents/MacOS/vigil-tray"
for library in "$TRAY_PUBLISH_DIR"/*.dylib; do
  cp "$library" "$TRAY_APP/Contents/MacOS/"
  chmod 755 "$TRAY_APP/Contents/MacOS/$(basename "$library")"
done
if [ ! -f "$TRAY_APP/Contents/MacOS/libSkiaSharp.dylib" ] || [ ! -f "$TRAY_APP/Contents/MacOS/libAvaloniaNative.dylib" ]; then
  echo "the tray publish is missing an Avalonia native library" >&2
  exit 1
fi

cp "$ROOT/LICENSE" "$TRAY_APP/Contents/Resources/LICENSE"
chmod 644 "$TRAY_APP/Contents/Resources/LICENSE"
cp "$ROOT/THIRD-PARTY-NOTICES.txt" "$TRAY_APP/Contents/Resources/THIRD-PARTY-NOTICES.txt"
chmod 644 "$TRAY_APP/Contents/Resources/THIRD-PARTY-NOTICES.txt"

# LSUIElement: a menu bar program has no Dock icon and no menu bar of its own. NSHighResolutionCapable: Retina.
cat > "$TRAY_APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key>
	<string>$TRAY_BUNDLE_ID</string>
	<key>CFBundleName</key>
	<string>Vigil Tray</string>
	<key>CFBundleExecutable</key>
	<string>vigil-tray</string>
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
	<key>NSHighResolutionCapable</key>
	<true/>
</dict>
</plist>
PLIST
plutil -lint "$TRAY_APP/Contents/Info.plist" >/dev/null

for library in "$TRAY_APP"/Contents/MacOS/*.dylib; do
  codesign --force --sign - "$library"
done
codesign --force --sign - --identifier "$TRAY_BUNDLE_ID" "$TRAY_APP"
codesign --verify --strict --deep "$TRAY_APP"

echo "Built $TRAY_APP"

# One zip with both bundles at its top level. ditto keeps the bundle structure, permissions and the ad-hoc signatures intact.
# Unpacking the zip into a scratch folder and verifying both signatures proves that.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir "$STAGE/zip"
ditto "$APP" "$STAGE/zip/Vigil.app"
ditto "$TRAY_APP" "$STAGE/zip/Vigil Tray.app"
ditto -c -k "$STAGE/zip" "$ZIP"

mkdir "$STAGE/check"
ditto -x -k "$ZIP" "$STAGE/check"
for bundle in "Vigil.app" "Vigil Tray.app"; do
  for notice in LICENSE THIRD-PARTY-NOTICES.txt; do
    cmp "$ROOT/$notice" "$STAGE/check/$bundle/Contents/Resources/$notice"
  done
done
codesign --verify --strict "$STAGE/check/Vigil.app"
codesign --verify --strict --deep "$STAGE/check/Vigil Tray.app"

ls -l "$APP/Contents/MacOS/vigil" "$TRAY_APP/Contents/MacOS/vigil-tray" "$TRAY_APP"/Contents/MacOS/*.dylib "$ZIP"
echo "Built $ZIP"
