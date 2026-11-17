#!/usr/bin/env bash
set -euo pipefail

# Usage: ./build-deb.sh <version> <published-binary-path> <output-dir>
RAW_VER="${1:?Version required (e.g. 2.0.0)}"
VER="${RAW_VER#v}"
BIN_PATH="${2:?Published HuaweiHROverlay binary path required}"
OUT_DIR="${3:?Output directory required}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

PKG_ROOT="$STAGE_DIR/huawei-hr-overlay_${VER}_amd64"
mkdir -p \
  "$PKG_ROOT/DEBIAN" \
  "$PKG_ROOT/usr/lib/huawei-hr-overlay" \
  "$PKG_ROOT/usr/bin" \
  "$PKG_ROOT/usr/share/applications" \
  "$PKG_ROOT/usr/share/icons/hicolor/scalable/apps"

install -m 0755 "$BIN_PATH" "$PKG_ROOT/usr/lib/huawei-hr-overlay/HuaweiHROverlay"
ln -s ../lib/huawei-hr-overlay/HuaweiHROverlay "$PKG_ROOT/usr/bin/huawei-hr-overlay"
install -m 0644 "$SCRIPT_DIR/huawei-hr-overlay.desktop" "$PKG_ROOT/usr/share/applications/huawei-hr-overlay.desktop"
install -m 0644 "$SCRIPT_DIR/heart.svg" "$PKG_ROOT/usr/share/icons/hicolor/scalable/apps/huawei-hr-overlay.svg"

INSTALLED_KB=$(du -sk "$PKG_ROOT/usr" | awk '{print $1}')

cat > "$PKG_ROOT/DEBIAN/control" <<EOF
Package: huawei-hr-overlay
Version: ${VER}
Section: video
Priority: optional
Architecture: amd64
Installed-Size: ${INSTALLED_KB}
Depends: bluez, libfontconfig1, libx11-6
Recommends: obs-studio
Maintainer: fishuvn <hn42963@st.huce.edu.vn>
Homepage: https://github.com/fishuvn/huawei-hr-overlay
Description: Real-time Bluetooth LE Heart Rate Overlay for OBS Studio
 Streams live heart rate telemetry directly from Huawei Watches, Huawei Bands,
 and standard BLE Heart Rate Service (0x180D) monitors to OBS Studio via a
 local HTTP + WebSocket broadcast studio. Works on X11 and Wayland via BlueZ.
EOF

mkdir -p "$OUT_DIR"
OUT_DEB="$OUT_DIR/HuaweiHROverlay-v${VER}-linux-x64.deb"
dpkg-deb --root-owner-group --build "$PKG_ROOT" "$OUT_DEB"
echo "Built Debian package: $OUT_DEB"
