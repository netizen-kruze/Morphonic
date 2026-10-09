#!/usr/bin/env bash
# Build the Morphonic release on Linux (Fedora 44 with the .NET 9 SDK:
# `sudo dnf install dotnet-sdk-9.0`): one self-contained linux-x64 file —
# the .NET runtime, the interface and the CPU inference runtime are all
# inside it — written to releases/Morphonic-<version>-linux-x64. The binary
# installs itself into the app grid (--install), so nothing else ships
# beside it. GPU acceleration is downloaded in-app.
#
#   ./build.sh                       # Release
#   ./build.sh Debug
#   ./build.sh Release --offline DIR  # also releases/Morphonic-<ver>-linux-x64-offline with the
#                                    # three model files inside (DIR = a models folder the app
#                                    # filled with --fetch-models; the sample voice is in ../voices)
set -euo pipefail
cd "$(dirname "$0")"
CONFIG="${1:-Release}"
OFFLINE=""
if [ "${2:-}" = "--offline" ]; then OFFLINE="${3:?--offline needs a models folder}"; fi
PROJ=src/Morphonic/Morphonic.csproj
VER="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$PROJ" | head -n 1)"
[ -n "$VER" ] || { echo "No <Version> in the csproj."; exit 1; }

rm -rf publish-linux
echo "==> Publishing Morphonic $VER ($CONFIG, linux-x64, self-contained, single file)"
dotnet publish "$PROJ" -c "$CONFIG" -r linux-x64 --self-contained -o publish-linux \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded

mkdir -p releases
OUT="releases/Morphonic-$VER-linux-x64"
cp publish-linux/Morphonic "$OUT"
chmod 0755 "$OUT" publish-linux/Morphonic
echo "==> Done: $OUT ($(du -h "$OUT" | cut -f1))"
echo "    SHA-256: $(sha256sum "$OUT" | cut -d' ' -f1)"
if [ -n "$OFFLINE" ]; then
  OFF="releases/Morphonic-$VER-linux-x64-offline"
  "publish-linux/Morphonic" --pack-offline "$OFFLINE" "$OFF"
  chmod 0755 "$OFF"
  echo "==> Done: $OFF ($(du -h "$OFF" | cut -f1), models inside)"
  echo "    SHA-256: $(sha256sum "$OFF" | cut -d' ' -f1)"
fi
# releases/SHA256SUMS.txt: every release file of this version, for
# `sha256sum -c` next to a download.
(cd releases && sha256sum Morphonic-"$VER"-* > SHA256SUMS.txt)
echo "==> releases/SHA256SUMS.txt covers $(wc -l < releases/SHA256SUMS.txt) file(s)"
