# Huawei HR Overlay for OBS

Real-time Bluetooth LE heart rate overlay for **OBS Studio** across **Windows 10/11**, **Linux (Debian/Ubuntu, Arch Linux, Fedora)**, and **macOS (Apple Silicon & Intel)** — reading data directly from your **Huawei Watch, Huawei Band, or standard BLE HR monitor** with no Huawei SDK, no cloud, and no phone required.

![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)
![.NET](https://img.shields.io/badge/.NET%208-Avalonia%20UI-purple)
![OBS](https://img.shields.io/badge/OBS-Browser%20Source-orange)

---

## Downloads & Installation

Grab the latest build for your OS from [**Releases**](https://github.com/fishuvn/huawei-hr-overlay/releases):

### Windows 10 / 11
| File | For |
|------|-----|
| `HuaweiHROverlay-vX.Y.Z-win-x64.exe` | **Most users** — self-contained single file, just download and run |
| `HuaweiHROverlay-vX.Y.Z-win-x64-lite.exe` | Machines with [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) preinstalled |

### Linux (Debian / Ubuntu / Arch / Fedora)
Ensure BlueZ is active (`sudo systemctl enable --now bluetooth`):

- **Debian 12/13 (Bookworm/Trixie), Ubuntu, Linux Mint, Pop!_OS:**
  ```bash
  sudo apt install ./HuaweiHROverlay-vX.Y.Z-linux-x64.deb
  huawei-hr-overlay
  ```
- **Fedora, RHEL, Nobara, Bazzite, openSUSE:**
  ```bash
  sudo dnf install ./HuaweiHROverlay-vX.Y.Z-linux-x64.rpm
  huawei-hr-overlay
  ```
- **Arch Linux, EndeavourOS, CachyOS, Manjaro:**
  Download `PKGBUILD` from the latest release (or `packaging/arch/PKGBUILD` in this repo) and run:
  ```bash
  makepkg -si
  huawei-hr-overlay
  ```
  *Or use the portable archive on any distro:*
  ```bash
  tar -xzf HuaweiHROverlay-vX.Y.Z-linux-x64.tar.gz
  cd huawei-hr-overlay-*-linux-x64 && ./install.sh
  ```

### macOS (Apple Silicon & Intel)
1. Download `HuaweiHROverlay-vX.Y.Z-macos-arm64.dmg` (M1/M2/M3/M4) or `HuaweiHROverlay-vX.Y.Z-macos-x64.dmg` (Intel).
2. Open the DMG and drag **Huawei HR Overlay.app** to **Applications**.
3. If macOS Gatekeeper warns about an unsigned open-source app on first launch:
   ```bash
   xattr -cr "/Applications/Huawei HR Overlay.app"
   ```
4. Grant **Bluetooth** access when macOS prompts on first scan.

> **Built-in GitHub Updater:** The app checks `fishuvn/huawei-hr-overlay` releases automatically on launch (or via **Check Updates**), selects the matching package/binary for your OS & architecture, and verifies SHA-256 checksums.

---

## Quick Start

1. **Enable HR Data Broadcast on your watch:**
   On your Huawei Watch/Band open **Settings → HR Data Broadcasts → ON** and keep the screen awake while connecting.
2. **Scan & Connect:**
   Launch **Huawei HR Overlay**, click **Scan** (toggle **HR Only** to filter out non-wearable BLE beacons), and double-click your watch (or click **Connect Watch**). Repeat to connect multiple watches simultaneously.
3. **Add to OBS Studio:**
   Customize your theme in **OBS Browser Source Studio**, click **Copy URL**, then in OBS add **Sources → + → Browser**:
   - **URL:** `http://localhost:8764/` (or per-device `/overlay/<ID>`)
   - **Width:** `400` · **Height:** `120`
   - **Shutdown source when not visible:** **OFF**

### Headless CLI Mode (Optional)
For Linux tiling window managers, scripts, or headless setups:
```bash
huawei-hr-overlay --headless --scan
huawei-hr-overlay --headless --device A4:C1:38:F1:23:45
huawei-hr-overlay --headless --simulate
```

---

## OBS Overlay URL Parameters

| Parameter | Values | Default | Description |
|-----------|--------|---------|-------------|
| `/overlay/<ID>` or `device=<ID>` | BLE ID / UUID or `SIM` | *(any active)* | Lock overlay to one specific watch |
| `theme` | `glass`, `minimal`, `neon`, `pill` | `glass` | Visual style preset |
| `ecg` | `1`, `0` | `1` | Real-time P-QRS-T cardiac waveform trace |
| `zone` | `1`, `0` | `1` | Adaptive HR zone status (`Rest`, `Fat Burn`, `Cardio`, `Peak`) |
| `stats` | `1`, `0` | `0` | Show session `MIN` / `MAX` alongside BPM |
| `name` | `1`, `0` | `0` | Display watch name above BPM |
| `label` | Custom text | — | Custom player label (e.g. `?label=Player+1`) |
| `align` | `left`, `center`, `right` | `center` | Horizontal alignment inside OBS Browser Source |
| `autohide` | `1`, `0` | `0` | Smoothly hide overlay when watch is offline |

---

## Supported Devices

Any wearable or chest strap implementing standard **BLE GATT Heart Rate Service (`0x180D` / `0x2A37`)**:
- Huawei Watch GT2 / GT3 / GT4 / GT5 / GT6 series & Watch Ultimate series
- Huawei Watch Fit / Fit 2 / Fit 3 / Fit 4 / Fit 5 series
- Huawei Band 6 / 7 / 8 / 9 / 10 / 11 series
- Polar H10, Wahoo TICKR, Garmin HRM, Coospo, Magene, and all standard BLE HR straps

---

## Architecture

```text
HuaweiHROverlay/
├── Core/
│   ├── Platforms/
│   │   ├── WindowsBleBackend.cs         # Windows 10/11 WinRT BLE GATT
│   │   ├── LinuxBlueZBackend.cs         # Linux BlueZ over System D-Bus (Debian/Arch/Fedora)
│   │   └── MacCoreBluetoothBackend.cs   # macOS CoreBluetooth in-process Objective-C bridge
│   ├── BleHeartRateService.cs           # Multi-device coordinator + auto-reconnect
│   ├── GitHubUpdateService.cs           # Multi-OS GitHub Releases SHA-256 updater
│   ├── HttpOverlayServer.cs             # Embedded overlay + status JSON (port 8764)
│   ├── WebSocketServer.cs               # Real-time BPM WebSocket server (port 8765)
│   └── HeartRateReading.cs              # BT SIG 0x2A37 packet parser & JSON payload
├── Assets/overlay.html                  # Broadcast OBS overlay + 60fps ECG canvas
├── MainWindow.axaml(.cs)                # Cross-platform Avalonia 11 Studio UI
└── HeadlessRunner.cs                    # Optional `--headless` terminal mode
packaging/
├── linux/ (build-deb.sh, build-rpm.sh, build-tarball.sh, .desktop, icon)
├── arch/PKGBUILD
└── macos/ (build-macos-app.sh, Info.plist with NSBluetoothAlwaysUsageDescription)
```

---

## Changelog

**v2.0.0**
- **Cross-Platform Desktop & Native BLE** — full support for **Linux** (`.deb` for Debian/Ubuntu, `.rpm` for Fedora, `PKGBUILD` & `.tar.gz` for Arch via BlueZ D-Bus), **macOS** (`arm64` & `x64` `.dmg` via CoreBluetooth), and **Windows 10/11** (`x64` Full & Lite EXEs via WinRT), powered by Avalonia UI 11
- **Headless CLI Mode** — added `--headless` (`--scan`, `--device <ID>`, `--simulate`)
- **Automated Multi-OS CI & Auto-Release** — pushing to `main` automatically builds Windows, Linux, and macOS packages and publishes versioned GitHub Releases with `SHA256SUMS.txt`
- **Multi-OS GitHub Updater & Impeccable Broadcast Polish** — OS-aware release asset matching, WCAG AA contrast, crisp tabular telemetry numerals, and zero anti-patterns
