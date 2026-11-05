#!/usr/bin/env bash
# Builds MeetingScribe.app into ./build and (with --install) copies it to /Applications.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
APP="build/MeetingScribe.app"

echo "==> Compiling (release)"
swift build -c release --product MeetingScribe
swift build -c release --product msctl
BIN="$(swift build -c release --show-bin-path)"

echo "==> Compiling MLX Metal shaders"
KERNELS=".build/checkouts/mlx-swift/Source/Cmlx/mlx/mlx/backend/metal/kernels"
METALLIB="$BIN/mlx.metallib"
if [[ ! -f "$METALLIB" || "$KERNELS" -nt "$METALLIB" ]]; then
  if ! xcrun -sdk macosx metal --version >/dev/null 2>&1; then
    echo "Metal toolchain missing, installing (one time)…"; xcodebuild -downloadComponent MetalToolchain
  fi
  TMP="$(mktemp -d)"; trap 'command rm -rf "$TMP"' EXIT
  AIRS=()
  while IFS= read -r src; do
    out="$TMP/$(echo "$src" | shasum | cut -c1-16).air"
    xcrun -sdk macosx metal -x metal -std=metal3.2 -mmacosx-version-min=15.0 -g0 -fno-fast-math \
      -Wno-c++17-extensions -Wno-c++20-extensions -c "$src" -I"$KERNELS" \
      -I".build/checkouts/mlx-swift/Source/Cmlx/mlx" -o "$out"
    AIRS+=("$out")
  done < <(find "$KERNELS" -name '*.metal' ! -name '*_nax.metal' | LC_ALL=C sort)
  xcrun -sdk macosx metallib "${AIRS[@]}" -o "$METALLIB"
fi
cp "$METALLIB" "$BIN/../release/mlx.metallib" 2>/dev/null || true   # lets msctl find it too

echo "==> Assembling $APP"
command rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/MeetingScribe" "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
for b in "$BIN"/*.bundle; do cp -R "$b" "$APP/Contents/Resources/"; done
CMLX="$APP/Contents/Resources/mlx-swift_Cmlx.bundle/Contents/Resources"
mkdir -p "$CMLX" && cp "$METALLIB" "$CMLX/default.metallib"

if [[ ! -f build/AppIcon.icns ]]; then
  ICONSET="build/AppIcon.iconset"; mkdir -p "$ICONSET"
  swift scripts/make_icon.swift build/icon1024.png
  for s in 16 32 128 256 512; do
    sips -z $s $s build/icon1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) build/icon1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o build/AppIcon.icns
fi
cp build/AppIcon.icns "$APP/Contents/Resources/"

echo "==> Signing"
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -p codesigning -v 2>/dev/null | awk -F'"' '/Apple Development|Developer ID/ {print $2; exit}')}"
IDENTITY="${IDENTITY:--}"
codesign --force --deep --options runtime --entitlements Resources/MeetingScribe.entitlements -s "$IDENTITY" "$APP"
codesign --verify --deep --strict "$APP" && echo "signed with: $IDENTITY"

if [[ "${1:-}" == "--install" ]]; then
  echo "==> Installing to /Applications"
  command rm -rf /Applications/MeetingScribe.app
  cp -R "$APP" /Applications/
  echo "Installed /Applications/MeetingScribe.app"
fi
echo "Done: $ROOT/$APP"
