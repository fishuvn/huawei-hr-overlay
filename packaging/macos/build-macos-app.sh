#!/usr/bin/env bash
set -euo pipefail

# Usage: ./build-macos-app.sh <version> <arch:arm64|x64> <publish-dir> <output-dir>
RAW_VER="${1:?Version required (e.g. 2.0.0)}"
VER="${RAW_VER#v}"
ARCH="${2:?Arch required (arm64 or x64)}"
PUB_DIR="$(cd "${3:?Publish directory required}" && pwd)"
OUT_DIR="${4:?Output directory required}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

APP_NAME="Huawei HR Overlay.app"
APP_DIR="$STAGE_DIR/dmg-root/$APP_NAME"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

cp -R "$PUB_DIR"/* "$APP_DIR/Contents/MacOS/"
chmod +x "$APP_DIR/Contents/MacOS/HuaweiHROverlay"
sed "s/__VERSION__/${VER}/g" "$SCRIPT_DIR/Info.plist.template" > "$APP_DIR/Contents/Info.plist"

# Ad-hoc sign so Apple Silicon Gatekeeper/kernel accepts the Mach-O bundle cleanly
codesign --force --deep --sign - "$APP_DIR"

ln -s /Applications "$STAGE_DIR/dmg-root/Applications"

mkdir -p "$OUT_DIR"
OUT_DMG="$OUT_DIR/HuaweiHROverlay-v${VER}-macos-${ARCH}.dmg"
hdiutil create -volname "Huawei HR Overlay ${VER}" -srcfolder "$STAGE_DIR/dmg-root" -ov -format UDZO "$OUT_DMG"
echo "Built macOS DMG: $OUT_DMG"
