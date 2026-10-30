#!/usr/bin/env bash
# Builds Native AOT releases and packs them into a tar.gz under artifacts/<rid>/.
# Usage: scripts/package-linux.sh linux-x64|linux-arm64
#
# Output:
#   artifacts/<rid>/vigil
#   artifacts/<rid>/tray/vigil-tray and the two .so files Avalonia needs next to it (Skia, HarfBuzz)
#   artifacts/<rid>/Vigil-<version>-<rid>.tar.gz   (a folder named Vigil holding vigil, the tray folder, LICENSE and THIRD-PARTY-NOTICES.txt)
#
# Native AOT cannot compile across operating systems, so this runs on Linux, and it builds for the machine's own
# architecture: x86_64 for linux-x64, aarch64 for linux-arm64. Cross-architecture on Linux needs a cross linker, the
# target's C runtime and zlib objects, which the .NET documentation describes as steps that "may suffice ... although this
# is not documented or guaranteed" (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile), so
# CI builds linux-arm64 on a native Arm runner instead (ubuntu-24.04-arm, free for public repositories:
# https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
#
# The binaries link the C library dynamically, so they need a glibc at least as new as the one they were built against (the
# build prints the version it found, for vigil and for every file of the tray). The tray also needs libfontconfig1 and the X11
# libraries at run time (README, "Install on Linux"); the build does not check for them. Nothing is signed.
set -euo pipefail

usage() {
  echo "usage: $0 linux-x64|linux-arm64" >&2
  exit 2
}

[ "$#" -eq 1 ] || usage
RID="$1"
case "$RID" in
  linux-x64) EXPECTED_ARCH="x86_64" ;;
  linux-arm64) EXPECTED_ARCH="aarch64" ;;
  *) usage ;;
esac

if [ "$(uname -s)" != "Linux" ]; then
  echo "package-linux.sh must run on Linux (Native AOT cannot cross-compile between operating systems)." >&2
  exit 1
fi

if [ "$(uname -m)" != "$EXPECTED_ARCH" ]; then
  echo "package-linux.sh builds $RID on a $EXPECTED_ARCH machine; this one is $(uname -m). Use a runner of the target architecture." >&2
  exit 1
fi

if ! command -v clang >/dev/null 2>&1; then
  echo "clang is required by Native AOT (sudo apt-get install clang zlib1g-dev)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Vigil/Vigil.csproj"
TRAY_PROJECT="$ROOT/src/Vigil.Tray/Vigil.Tray.csproj"
OUT="$ROOT/artifacts/$RID"
PUBLISH_DIR="$OUT/publish"
TRAY_PUBLISH_DIR="$OUT/publish-tray"
TRAY_OUT="$OUT/tray"

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo | tr -d '[:space:]')"
if ! [[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "unexpected project version '$VERSION'" >&2
  exit 1
fi

for notice in LICENSE THIRD-PARTY-NOTICES.txt; do
  if [ ! -f "$ROOT/$notice" ]; then
    echo "$ROOT/$notice is missing (the licenses of Vigil and of the components it ships require their notices in every distributed copy)" >&2
    exit 1
  fi
done

echo "Publishing Vigil $VERSION for $RID"

# Only remove what this script created, and only below artifacts/.
case "$OUT" in
  "$ROOT"/artifacts/linux-*) rm -rf "$PUBLISH_DIR" "$TRAY_PUBLISH_DIR" "$TRAY_OUT" "$OUT/vigil" "$OUT"/Vigil-*.tar.gz ;;
  *) echo "refusing to clean $OUT" >&2; exit 1 ;;
esac

dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR" --nologo

if [ ! -x "$PUBLISH_DIR/vigil" ]; then
  echo "the publish did not produce $PUBLISH_DIR/vigil" >&2
  exit 1
fi

cp "$PUBLISH_DIR/vigil" "$OUT/vigil"
chmod 755 "$OUT/vigil"

# The tray: the program and the two native libraries the publish puts next to it (Avalonia loads them from its own folder).
# Debug symbols stay behind.
dotnet publish "$TRAY_PROJECT" -c Release -r "$RID" -o "$TRAY_PUBLISH_DIR" --nologo
for required in vigil-tray libSkiaSharp.so libHarfBuzzSharp.so; do
  if [ ! -f "$TRAY_PUBLISH_DIR/$required" ]; then
    echo "the tray publish did not produce $TRAY_PUBLISH_DIR/$required" >&2
    exit 1
  fi
done
mkdir "$TRAY_OUT"
cp "$TRAY_PUBLISH_DIR/vigil-tray" "$TRAY_PUBLISH_DIR/libSkiaSharp.so" "$TRAY_PUBLISH_DIR/libHarfBuzzSharp.so" "$TRAY_OUT/"
chmod 755 "$TRAY_OUT/vigil-tray"
chmod 644 "$TRAY_OUT/libSkiaSharp.so" "$TRAY_OUT/libHarfBuzzSharp.so"

# The newest glibc symbol version the binaries ask for: the oldest distribution they can run on.
if command -v objdump >/dev/null 2>&1; then
  GLIBC="$(objdump -T "$OUT/vigil" | grep -o 'GLIBC_[0-9][0-9.]*' | sort -uV | tail -n 1 || true)"
  echo "vigil requires ${GLIBC:-no versioned glibc symbols}"
  for file in "$TRAY_OUT"/*; do
    GLIBC="$(objdump -T "$file" | grep -o 'GLIBC_[0-9][0-9.]*' | sort -uV | tail -n 1 || true)"
    echo "tray/$(basename "$file") requires ${GLIBC:-no versioned glibc symbols}"
  done
  # What the tray's own libraries link to at run time. Only listed: the build machine need not have them.
  objdump -p "$TRAY_OUT/libSkiaSharp.so" | grep NEEDED || true
fi

# The MIT license requires its notice in every distributed copy: it goes next to vigil.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir "$STAGE/Vigil" "$STAGE/Vigil/tray"
cp "$OUT/vigil" "$STAGE/Vigil/vigil"
cp "$TRAY_OUT"/* "$STAGE/Vigil/tray/"
cp "$ROOT/LICENSE" "$STAGE/Vigil/LICENSE"
cp "$ROOT/THIRD-PARTY-NOTICES.txt" "$STAGE/Vigil/THIRD-PARTY-NOTICES.txt"
chmod 755 "$STAGE/Vigil/vigil" "$STAGE/Vigil/tray/vigil-tray"
chmod 644 "$STAGE/Vigil/LICENSE" "$STAGE/Vigil/THIRD-PARTY-NOTICES.txt"

# Owner 0 and sorted names, so the archive does not carry the build machine's user ids; gzip -n leaves out the file name and time.
ARCHIVE="$OUT/Vigil-$VERSION-$RID.tar.gz"
tar --sort=name --owner=0 --group=0 --numeric-owner -C "$STAGE" -cf - Vigil | gzip -n -9 > "$ARCHIVE"

tar -tzvf "$ARCHIVE"
ls -l "$OUT/vigil" "$TRAY_OUT"/* "$ARCHIVE"
echo "Built $ARCHIVE"
