# Huawei HR Overlay for OBS

Real-time heart rate overlay for OBS Studio, reading data directly from your **Huawei Watch or Smartband** via Bluetooth LE — no Huawei SDK, no cloud, no phone required.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET%208-Runtime%20Included-purple)
![OBS](https://img.shields.io/badge/OBS-Browser%20Source-orange)

---

## Downloads & In-App Updates

Grab the latest build from [**Releases**](https://github.com/fishuvn/huawei-hr-overlay/releases):

| File | Size | For |
|------|------|-----|
| `HuaweiHROverlay-x.y.z-win-x64.exe` | ~180 MB | **Most users** — .NET 8 Desktop Runtime included, just download and run |
| `HuaweiHROverlay-x.y.z-win-x64-lite.exe` | ~25 MB | Machines that already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime) |

> **Built-in GitHub Updater:** Starting in `v1.2.0`, the app automatically checks GitHub Releases on launch (or via **↻ Check Updates**), selects the matching **Full** or **Lite** build for your installation, verifies the **SHA-256** digest, and updates + restarts in one click.

---

## How It Works

```
Huawei Watch/Band
  [HR Data Broadcast ON]
        │ Bluetooth LE (GATT 0x180D)
        ▼
  HuaweiHROverlay.exe (this app)
  ├── BLE Scanner + GATT reader + Auto-reconnect
  ├── GitHub Releases Updater (Full/Lite aware)
  ├── WebSocket server  ws://localhost:8765
  └── HTTP server       http://localhost:8764
        │
        ▼
  OBS Browser Source → customizable animated overlay + live ECG wave
```

Your Huawei watch's **HR Data Broadcast** mode makes it act as a standard BLE Heart Rate Monitor.
No Huawei SDK or cloud account is needed.

---

## Requirements

| Requirement | Download |
|-------------|---------|
| Windows 10 (build 19041+) or Windows 11 | — |
| **Bluetooth 5.0 or newer adapter** | Most modern PCs have it built in; older PCs need a BT 5.0+ USB dongle |
| OBS Studio | [obsproject.com](https://obsproject.com) |
| Huawei Watch/Band with HR Data Broadcast | Most GT2/GT3/GT4/GT5/GT6, Band 6+, Watch Fit series |

> [!NOTE]
> Bluetooth 4.x adapters may discover devices but often fail to hold a reliable BLE connection to
> modern watches. If scans come up empty or connections drop, a BT 5.0+ USB dongle is the usual fix.

---

## Quick Setup

### Step 1 — Enable HR Data Broadcast on your Huawei watch
1. On your **Huawei watch/band**, press the side button to open the app list
2. Go to **Settings → HR Data Broadcasts** and enable it
3. Keep the watch screen awake while connecting

### Step 2 — Pair with Windows (optional, improves reconnect reliability)
1. Open **Windows Settings → Bluetooth & devices → Add device → Bluetooth**
2. Select your Huawei watch to pair

### Step 3 — Connect in Huawei HR Overlay
1. Launch **`HuaweiHROverlay.exe`**
2. Click **▶ Scan** (toggle **HR Only** to filter out non-wearable Bluetooth devices)
3. Double-click your watch — or select it and click **⚡ Connect Watch**
4. Live BPM, HR Zone, **Min / Avg / Peak**, and a real-time trend sparkline appear in the Hero card
5. Connect additional watches the same way — click **Copy URL** on any connected row for a per-watch OBS source

### Step 4 — Customize & Add OBS Browser Source
1. In the **OBS Browser Source Studio** card, choose a **Theme Preset** (`Glass Card`, `Minimal Clean`, `Neon Cyber`, `Compact Pill`) and toggle **Overlay Elements** (`ECG Waveform`, `HR Zone`, `Min / Max`, `Watch Name`, `Auto-hide Offline`)
2. Click **Copy URL**
3. In OBS: **Sources → + → Browser**
   - **URL:** paste the copied URL (default `http://localhost:8764/`)
   - **Width:** `400` · **Height:** `120`
   - **Shutdown source when not visible:** **OFF**

---

## OBS Overlay URL Parameters

All parameters are optional and generated automatically by the in-app **OBS Browser Source Studio**:

| Parameter | Values | Default | Description |
|-----------|--------|---------|-------------|
| `/overlay/<ID>` or `device=<ID>` | 12-hex BLE ID or `SIM` | *(any active)* | Lock overlay to a specific watch |
| `theme` | `glass`, `minimal`, `neon`, `pill` | `glass` | Visual style preset |
| `ecg` | `1`, `0` | `1` | Real-time P-QRS-T cardiac waveform trace |
| `zone` | `1`, `0` | `1` | Adaptive HR zone status pill (`Rest`, `Fat Burn`, `Cardio`, `Peak`) |
| `stats` | `1`, `0` | `0` | Show session `MIN` / `MAX` alongside BPM |
| `name` | `1`, `0` | `0` | Display watch name above BPM |
| `label` | Custom text | — | Custom player label (e.g. `?label=Player+1`) |
| `align` | `left`, `center`, `right` | `center` | Horizontal alignment inside OBS Browser Source |
| `autohide` | `1`, `0` | `0` | Smoothly hide overlay when watch is offline/disconnected |

---

## Supported Devices

Any device implementing the standard **BLE GATT Heart Rate Service (0x180D)** works:

- Huawei Watch GT2 / GT2e / GT2 Pro / GT3 / GT3 Pro / GT3 SE / GT4 / GT5 / GT5 Pro / GT6 / GT6 Pro
- Huawei Watch Ultimate / Ultimate Design / Ultimate 2
- Huawei Watch Fit / Fit Elegant / Fit 2 / Fit 3 / Fit 4 / Fit 4 Pro / Fit 5
- Huawei Band 6 / 7 / 7 Pro / 8 / 9 / 10 / 10 Pro / 11 / 11 Pro
- Standard BLE HR chest straps & armbands (Polar H10, Wahoo TICKR, Garmin HRM, Coospo, etc.)

---

## Ports & Endpoints

| Service | Address |
|---------|---------|
| Overlay page, any active device | `http://localhost:8764/` |
| Overlay page, specific device | `http://localhost:8764/overlay/<DEVICE-ID>` |
| WebSocket BPM stream | `ws://localhost:8765/` or `ws://localhost:8765/?device=<DEVICE-ID>` |
| Telemetry status JSON | `http://localhost:8764/status` or `http://localhost:8764/status?device=<DEVICE-ID>` |

---

## Changelog

**v1.2.0**
- **GitHub In-App Updater** — checks `fishuvn/huawei-hr-overlay` releases automatically on startup and on demand, detects Full vs Lite installation, verifies SHA-256 checksums, downloads with live progress, and restarts cleanly
- **Telemetry Studio UI/UX Upgrade** — resizable modern dark interface, live 45-sample BPM sparkline, session Min/Avg/Peak counters, custom pill switches, HR-only scanner filter, 4-bar signal meter, empty/loading states, and 1-click per-device URL copy
- **Upgraded OBS Overlay + Studio Builder** — crisp vector heart, real-time P-QRS-T ECG canvas waveform, 4 theme presets (`glass`, `minimal`, `neon`, `pill`), optional Min/Max & player/watch labels, auto-hide mode, and instant `/status` hydration
- **BLE Auto-Reconnect** — automatically reconnects if your watch briefly drops signal mid-stream
- Fixed: OBS overlay now resets cleanly to waiting state when a watch disconnects or Simulator stops, and `/overlay/SIM` works for Simulator targeting

**v1.1.0**
- Multi-device support & per-device overlay URLs
- Self-contained and Lite single-file release builds

**v1.0.0**
- Initial release: single-watch BLE heart rate overlay for OBS

---

## Building from Source

Requires **.NET 8 SDK** ([download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)).

```powershell
# Debug build + run
cd HuaweiHROverlay
dotnet run

# Release self-contained single-file EXE (bundles .NET 8 Desktop Runtime)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:BuildVariant=full -o ./publish

# Release runtime-dependent single-file EXE (Lite)
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:BuildVariant=lite -o ./publish-lite
```

---

## Architecture

```
HuaweiHROverlay/
├── Core/
│   ├── BleHeartRateService.cs   # BLE scanner, multi-device sessions & auto-reconnect
│   ├── GitHubUpdateService.cs   # GitHub Releases version check, SHA-256 verified download & self-updater
│   ├── WebSocketServer.cs       # WS broadcast server, per-device subscriptions (port 8765)
│   ├── HttpOverlayServer.cs     # HTTP server for overlay + telemetry JSON (port 8764)
│   └── HeartRateReading.cs      # Heart rate & connection event payload
├── Assets/
│   ├── overlay.html             # Customizable OBS overlay + real-time ECG renderer (embedded)
│   └── heart.ico                # App icon
├── Resources/
│   └── Styles.xaml              # Telemetry Studio Dark WPF design system
├── MainWindow.xaml              # Studio control panel & OBS URL builder UI
└── MainWindow.xaml.cs           # Telemetry state, sparkline renderer & update workflow
```
