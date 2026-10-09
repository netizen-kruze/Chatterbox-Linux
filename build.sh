#!/usr/bin/env bash
# Build the Chatterbox release for Linux: one self-contained linux-x64
# file — the .NET runtime, the interface and the speech natives are all
# inside it — written to releases/Chatterbox-<version>-linux-x64. The
# binary installs itself into the app grid (--install), so nothing else
# ships beside it.
#
#   ./build.sh            # Release
#   ./build.sh Debug
set -euo pipefail
cd "$(dirname "$0")"
CONFIG="${1:-Release}"
PROJ=src/Chatterbox/Chatterbox.csproj
VER="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$PROJ" | head -n 1)"
[ -n "$VER" ] || { echo "No <Version> in the csproj."; exit 1; }

rm -rf publish
echo "==> Publishing Chatterbox $VER ($CONFIG, linux-x64, self-contained, single file)"
dotnet publish "$PROJ" -c "$CONFIG" -r linux-x64 --self-contained -o publish \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded

mkdir -p releases
OUT="releases/Chatterbox-$VER-linux-x64"
cp publish/Chatterbox "$OUT"
chmod 0755 "$OUT" publish/Chatterbox
echo "==> Done: $OUT ($(du -h "$OUT" | cut -f1))"
echo "    SHA-256: $(sha256sum "$OUT" | cut -d' ' -f1)"
