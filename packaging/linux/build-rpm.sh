#!/usr/bin/env bash
set -euo pipefail

# Usage: ./build-rpm.sh <version> <published-binary-path> <output-dir>
RAW_VER="${1:?Version required (e.g. 2.0.0)}"
VER="${RAW_VER#v}"
BIN_PATH="$(realpath "${2:?Published HuaweiHROverlay binary path required}")"
OUT_DIR="$(realpath -m "${3:?Output directory required}")"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RPM_TOP="$(mktemp -d)"
trap 'rm -rf "$RPM_TOP"' EXIT

mkdir -p "$RPM_TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}

cat > "$RPM_TOP/SPECS/huawei-hr-overlay.spec" <<EOF
# CRITICAL: Never strip .NET PublishSingleFile binaries (stripping truncates appended bundle)
%global debug_package %{nil}
%global __os_install_post %{nil}
%global __strip /bin/true

Name:           huawei-hr-overlay
Version:        ${VER}
Release:        1
Summary:        Real-time Bluetooth LE Heart Rate Overlay for OBS Studio
License:        MIT
URL:            https://github.com/fishuvn/huawei-hr-overlay
ExclusiveArch:  x86_64
Requires:       bluez, fontconfig, libX11
AutoReqProv:    no

%description
Streams live heart rate telemetry directly from Huawei Watches, Huawei Bands,
and standard BLE Heart Rate Service (0x180D) monitors into OBS Studio via a
local HTTP and WebSocket broadcast studio.

%install
mkdir -p %{buildroot}/usr/lib/huawei-hr-overlay
mkdir -p %{buildroot}/usr/bin
mkdir -p %{buildroot}/usr/share/applications
mkdir -p %{buildroot}/usr/share/icons/hicolor/scalable/apps

install -m 0755 "${BIN_PATH}" %{buildroot}/usr/lib/huawei-hr-overlay/HuaweiHROverlay
ln -s ../lib/huawei-hr-overlay/HuaweiHROverlay %{buildroot}/usr/bin/huawei-hr-overlay
install -m 0644 "${SCRIPT_DIR}/huawei-hr-overlay.desktop" %{buildroot}/usr/share/applications/huawei-hr-overlay.desktop
install -m 0644 "${SCRIPT_DIR}/heart.svg" %{buildroot}/usr/share/icons/hicolor/scalable/apps/huawei-hr-overlay.svg

%files
/usr/lib/huawei-hr-overlay/HuaweiHROverlay
/usr/bin/huawei-hr-overlay
/usr/share/applications/huawei-hr-overlay.desktop
/usr/share/icons/hicolor/scalable/apps/huawei-hr-overlay.svg
EOF

rpmbuild --define "_topdir $RPM_TOP" --target x86_64 -bb "$RPM_TOP/SPECS/huawei-hr-overlay.spec"

mkdir -p "$OUT_DIR"
BUILT_RPM=$(find "$RPM_TOP/RPMS" -name "*.rpm" | head -n 1)
OUT_RPM="$OUT_DIR/HuaweiHROverlay-v${VER}-linux-x64.rpm"
cp "$BUILT_RPM" "$OUT_RPM"
echo "Built Fedora/RPM package: $OUT_RPM"
