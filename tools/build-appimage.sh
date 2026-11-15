#!/usr/bin/env bash
# Packs a Morphonic Linux binary (plain or offline) as an AppImage: the
# AppImage project's type-2 runtime (pinned by SHA-256) followed by a
# squashfs of the AppDir — what appimagetool does, without needing it.
#
#   tools/build-appimage.sh <Morphonic binary> <out .AppImage> <version>
#
# Needs: mksquashfs (squashfs-tools 4.4 or newer), curl, sha256sum; ImageMagick
# (magick or convert) for the PNG icon if present. Runs on any Linux,
# including WSL (build.ps1 calls it there). Work files go under
# $MORPHONIC_BUILD_CACHE (default ~/.cache/morphonic-build), never /tmp,
# which is in RAM on Fedora and the offline build is ~900 MB.
#
# Deterministic: file times and ownership in the image are fixed, so the
# same binary gives the same AppImage, byte for byte.
set -euo pipefail

BIN=${1:?binary}; OUT=${2:?output .AppImage}; VER=${3:?version}
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(dirname "$HERE")
CACHE=${MORPHONIC_BUILD_CACHE:-$HOME/.cache/morphonic-build}

# type2-runtime release 20251108 (github.com/AppImage/type2-runtime, MIT)
RUNTIME_URL=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
RUNTIME_SHA=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

command -v mksquashfs >/dev/null || { echo "mksquashfs not found (install squashfs-tools)" >&2; exit 1; }
mkdir -p "$CACHE"
RT="$CACHE/runtime-x86_64-20251108"
if ! echo "$RUNTIME_SHA  $RT" | sha256sum -c --status 2>/dev/null; then
  echo "fetching the AppImage runtime (20251108)"
  curl -fsSL -o "$RT.part" "$RUNTIME_URL"
  echo "$RUNTIME_SHA  $RT.part" | sha256sum -c --status || { echo "the AppImage runtime does not match its pinned SHA-256" >&2; rm -f "$RT.part"; exit 1; }
  mv "$RT.part" "$RT"
fi

WORK=$(mktemp -d "$CACHE/appdir.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/AppDir"
mkdir -p "$APPDIR/usr/bin"
cp "$BIN" "$APPDIR/usr/bin/Morphonic"
chmod 755 "$APPDIR/usr/bin/Morphonic"

# The runtime mounts the image and runs AppRun with APPIMAGE (this file) and
# APPDIR (the mount) set; exec keeps Morphonic the process it waits for.
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Morphonic" "$@"
EOF
chmod 755 "$APPDIR/AppRun"

cat > "$APPDIR/morphonic.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Morphonic
Comment=Real-time RVC voice changer, running on your own PC
Exec=Morphonic
Icon=morphonic
Terminal=false
StartupWMClass=Morphonic
Categories=AudioVideo;Audio;
Keywords=voice;changer;RVC;microphone;
X-AppImage-Version=$VER
EOF

cp "$ROOT/src/Morphonic/Install/morphonic.svg" "$APPDIR/morphonic.svg"
MAGICK=$(command -v magick || command -v convert || true)
if [ -n "$MAGICK" ] && "$MAGICK" -background none -density 384 "$APPDIR/morphonic.svg" -resize 256x256 -strip -define png:exclude-chunks=date,time "$APPDIR/morphonic.png" 2>/dev/null; then
  cp "$APPDIR/morphonic.png" "$APPDIR/.DirIcon"
else
  cp "$APPDIR/morphonic.svg" "$APPDIR/.DirIcon"
fi

# Fixed times and ownership: a reproducible image.
find "$APPDIR" -exec touch -h -d @0 {} +
mksquashfs "$APPDIR" "$WORK/image.squashfs" -root-owned -noappend -comp zstd -b 1M \
  -mkfs-time 0 -all-time 0 -no-progress -quiet

cat "$RT" "$WORK/image.squashfs" > "$OUT.part"
chmod 755 "$OUT.part"
mv "$OUT.part" "$OUT"
echo "AppImage: $OUT ($(stat -c %s "$OUT") bytes), SHA-256 $(sha256sum "$OUT" | cut -d' ' -f1)"
