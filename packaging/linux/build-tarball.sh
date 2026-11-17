#!/usr/bin/env bash
set -euo pipefail

# Usage: ./build-tarball.sh <version> <published-binary-path> <output-dir>
RAW_VER="${1:?Version required (e.g. 2.0.0)}"
VER="${RAW_VER#v}"
BIN_PATH="$(realpath "${2:?Published HuaweiHROverlay binary path required}")"
OUT_DIR="$(realpath -m "${3:?Output directory required}")"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

BUNDLE_DIR="$STAGE_DIR/huawei-hr-overlay-${VER}-linux-x64"
mkdir -p "$BUNDLE_DIR"

install -m 0755 "$BIN_PATH" "$BUNDLE_DIR/HuaweiHROverlay"
install -m 0644 "$SCRIPT_DIR/huawei-hr-overlay.desktop" "$BUNDLE_DIR/huawei-hr-overlay.desktop"
install -m 0644 "$SCRIPT_DIR/heart.svg" "$BUNDLE_DIR/huawei-hr-overlay.svg"

cat > "$BUNDLE_DIR/install.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${HOME}/.local"

mkdir -p "$PREFIX/lib/huawei-hr-overlay" "$PREFIX/bin" "$PREFIX/share/applications" "$PREFIX/share/icons/hicolor/scalable/apps"
install -m 0755 "$DIR/HuaweiHROverlay" "$PREFIX/lib/huawei-hr-overlay/HuaweiHROverlay"
ln -sf "../lib/huawei-hr-overlay/HuaweiHROverlay" "$PREFIX/bin/huawei-hr-overlay"
sed "s|/usr/bin/huawei-hr-overlay|$PREFIX/bin/huawei-hr-overlay|g" "$DIR/huawei-hr-overlay.desktop" > "$PREFIX/share/applications/huawei-hr-overlay.desktop"
install -m 0644 "$DIR/huawei-hr-overlay.svg" "$PREFIX/share/icons/hicolor/scalable/apps/huawei-hr-overlay.svg"

echo "Installed Huawei HR Overlay to $PREFIX/bin/huawei-hr-overlay"
EOF
chmod +x "$BUNDLE_DIR/install.sh"

mkdir -p "$OUT_DIR"
OUT_TAR="$OUT_DIR/HuaweiHROverlay-v${VER}-linux-x64.tar.gz"
tar -C "$STAGE_DIR" -czf "$OUT_TAR" "huawei-hr-overlay-${VER}-linux-x64"
echo "Built portable Linux tarball: $OUT_TAR"
