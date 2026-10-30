#!/usr/bin/env bash
# Builds a Native AOT release and packs it into a tar.gz under artifacts/<rid>/.
# Usage: scripts/package-linux.sh linux-x64|linux-arm64
#
# Output:
#   artifacts/<rid>/vigil
#   artifacts/<rid>/Vigil-<version>-<rid>.tar.gz   (a folder named Vigil holding vigil and LICENSE)
#
# Native AOT cannot compile across operating systems, so this runs on Linux, and it builds for the machine's own
# architecture: x86_64 for linux-x64, aarch64 for linux-arm64. Cross-architecture on Linux needs a cross linker, the
# target's C runtime and zlib objects, which the .NET documentation describes as steps that "may suffice ... although this
# is not documented or guaranteed" (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile), so
# CI builds linux-arm64 on a native Arm runner instead (ubuntu-24.04-arm, free for public repositories:
# https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
#
# The binary links the C library dynamically, so it needs a glibc at least as new as the one it was built against (the
# build prints the version it found). Nothing is signed.
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
OUT="$ROOT/artifacts/$RID"
PUBLISH_DIR="$OUT/publish"

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo | tr -d '[:space:]')"
if ! [[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "unexpected project version '$VERSION'" >&2
  exit 1
fi

if [ ! -f "$ROOT/LICENSE" ]; then
  echo "the license file $ROOT/LICENSE is missing (the MIT license requires its notice in every distributed copy)" >&2
  exit 1
fi

echo "Publishing Vigil $VERSION for $RID"

# Only remove what this script created, and only below artifacts/.
case "$OUT" in
  "$ROOT"/artifacts/linux-*) rm -rf "$PUBLISH_DIR" "$OUT/vigil" "$OUT"/Vigil-*.tar.gz ;;
  *) echo "refusing to clean $OUT" >&2; exit 1 ;;
esac

dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR" --nologo

if [ ! -x "$PUBLISH_DIR/vigil" ]; then
  echo "the publish did not produce $PUBLISH_DIR/vigil" >&2
  exit 1
fi

cp "$PUBLISH_DIR/vigil" "$OUT/vigil"
chmod 755 "$OUT/vigil"

# The newest glibc symbol version the binary asks for: the oldest distribution it can run on.
if command -v objdump >/dev/null 2>&1; then
  GLIBC="$(objdump -T "$OUT/vigil" | grep -o 'GLIBC_[0-9][0-9.]*' | sort -uV | tail -n 1 || true)"
  echo "Requires ${GLIBC:-no versioned glibc symbols}"
fi

# The MIT license requires its notice in every distributed copy: it goes next to vigil.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir "$STAGE/Vigil"
cp "$OUT/vigil" "$STAGE/Vigil/vigil"
cp "$ROOT/LICENSE" "$STAGE/Vigil/LICENSE"
chmod 755 "$STAGE/Vigil/vigil"
chmod 644 "$STAGE/Vigil/LICENSE"

# Owner 0 and sorted names, so the archive does not carry the build machine's user ids; gzip -n leaves out the file name and time.
ARCHIVE="$OUT/Vigil-$VERSION-$RID.tar.gz"
tar --sort=name --owner=0 --group=0 --numeric-owner -C "$STAGE" -cf - Vigil | gzip -n -9 > "$ARCHIVE"

tar -tzvf "$ARCHIVE"
ls -l "$OUT/vigil" "$ARCHIVE"
echo "Built $ARCHIVE"
