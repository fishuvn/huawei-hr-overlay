# Huawei HR Overlay for OBS

Real-time heart rate overlay for OBS Studio, reading data directly from your **Huawei Watch or Smartband** via Bluetooth LE — no Huawei SDK, no cloud, no phone required.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET%208-Runtime%20Included-purple)
![OBS](https://img.shields.io/badge/OBS-Browser%20Source-orange)

---

## Downloads

Grab the latest build from [**Releases**](https://github.com/fishuvn/huawei-hr-overlay/releases):

| File | Size | For |
|------|------|-----|
| `HuaweiHROverlay-x.y.z-win-x64.exe` | ~180 MB | **Most users** — .NET 8 Desktop Runtime included, just download and run |
| `HuaweiHROverlay-x.y.z-win-x64-lite.exe` | ~25 MB | Machines that already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime) |

---

## How It Works

```
Huawei Watch/Band
  [HR Data Broadcast ON]
        │ Bluetooth LE (GATT 0x180D)
        ▼
  HuaweiHROverlay.exe (this app)
  ├── BLE Scanner + GATT reader
  ├── WebSocket server  ws://localhost:8765
  └── HTTP server       http://localhost:8764
        │
        ▼
  OBS Browser Source → animated overlay on stream
```

Your Huawei watch's **HR Data Broadcast** mode makes it act as a standard BLE Heart Rate Monitor.
No Huawei SDK or cloud account is needed.

---

## Requirements

> **No runtime install needed** — the main release EXE bundles the .NET 8 Desktop Runtime (~180 MB single file).
> A smaller **lite** build (~25 MB) is available for machines that already have the runtime.

| Requirement | Download |
|-------------|---------|
| Windows 10 (build 19041+) or Windows 11 | — |
| **Bluetooth 5.0 or newer adapter** | Most modern PCs have it built in; older PCs need a BT 5.0+ USB dongle |
| OBS Studio | [obsproject.com](https://obsproject.com) |
| Huawei Watch/Band with HR Data Broadcast | Most GT2/GT3/GT4, Band 6+, Watch Fit series |

> [!NOTE]
> Bluetooth 4.x adapters may discover devices but often fail to hold a reliable BLE connection to
> modern watches. If scans come up empty or connections drop, a BT 5.0+ USB dongle is the usual fix.

---

## Step 1 — Enable HR Data Broadcast on your Huawei watch

> ⚠️ **This step is mandatory.** Without it, the watch won't be detected.

1. On your **Huawei watch/band**, press the side button to open the app list
2. Go to **Settings**
3. Scroll to **HR Data Broadcasts** and enable it
4. The watch will start broadcasting HR data over Bluetooth

> **Note:** While HR Data Broadcast is active, the watch may disconnect from the Huawei Health app on your phone. This is normal.

---

## Step 2 — Pair with Windows (optional but recommended)

1. Open **Windows Settings → Bluetooth & devices → Add device**
2. Select **Bluetooth** and wait for your Huawei watch to appear
3. Click it to pair — no PIN needed for most models

> The app can also discover and connect to **unpaired** devices. Pairing first makes reconnection more reliable.

---

## Step 3 — Run the app

Double-click **`HuaweiHROverlay.exe`** — no installation needed.

> If Windows shows a SmartScreen warning, click **More info → Run anyway**.
> The app is not code-signed (open source, build from source to verify).

---

## Step 4 — Connect to your watch(es)

1. Click **▶ Scan** — your watch should appear within 5–10 seconds
2. Select it from the list
3. Click **⚡ Connect**
4. Live BPM shows in the **Connected — Live** list
5. Keep scanning and connect **more watches/bands** the same way — each gets its own live row and overlay URL

---

## Step 5 — Add OBS Browser Source

1. In OBS, click **+** in the Sources panel → **Browser**
2. Set URL to: `http://localhost:8764/`
3. Width: **400**, Height: **120**
4. **"Shutdown source when not visible"** → OFF
5. Click **OK**

The animated heart rate widget appears on your stream!

### Multiple devices in OBS

Add one Browser Source per device, each with its own URL:

```
http://localhost:8764/overlay/<DEVICE-ID>
```

`<DEVICE-ID>` is the 12-character hex Bluetooth address shown under the device name in the app's scan list (e.g. `http://localhost:8764/overlay/A4C138F12345`). A source without an ID shows the most recent BPM from any connected device.

---

## Overlay Features

| Feature | Details |
|---------|---------|
| Animated pulsing heart | Speed dynamically matches BPM |
| Gradient BPM number | Large, readable, with red glow |
| Heart rate zones | 🔵 Rest (<100) → 🟢 Fat Burn (<130) → 🟡 Cardio (<160) → 🔴 Peak (160+) |
| Connection indicator | Green dot = connected, grey = disconnected |
| Auto-reconnect | Overlay reconnects automatically if the app restarts |
| Transparent background | Works natively with OBS browser source transparency |

---

## Simulator Mode

Check **Simulate** in the app to generate realistic fake BPM values — great for testing the overlay layout in OBS without your watch nearby.

---

## Supported Devices

Any device implementing the standard **BLE GATT Heart Rate Service (0x180D)** will work:

- Huawei Watch GT2 / GT2e / GT2 Pro / GT3 / GT3 Pro / GT3 SE / GT4 / GT5 / GT5 Pro / GT6 / GT6 Pro
- Huawei Watch Ultimate / Ultimate Design / Ultimate 2
- Huawei Watch Fit / Fit Elegant / Fit 2 / Fit 3 / Fit 4 / Fit 4 Pro / Fit 5
- Huawei Band 6 / 7 / 7 Pro / 8 / 9 / 10 / 10 Pro / 11 / 11 Pro
- Any standard BLE HR chest strap (Polar H10, Wahoo TICKR, etc.)

---

## Ports Used

| Service | Address |
|---------|---------|
| Overlay page, any device (OBS Browser Source) | `http://localhost:8764/` |
| Overlay page, one device | `http://localhost:8764/overlay/<DEVICE-ID>` |
| WebSocket BPM stream, one device | `ws://localhost:8765/?device=<DEVICE-ID>` |
| Status JSON | `http://localhost:8764/status` |
| Status JSON, one device | `http://localhost:8764/status?device=<DEVICE-ID>` |

---

## Changelog

**v1.1.0**
- **Multi-device support** — connect multiple watches/bands at the same time, each with its own live row and overlay
- **Per-device overlay URLs** — `http://localhost:8764/overlay/<DEVICE-ID>` for one browser source per watch
- **Self-contained release build** — main EXE now bundles the .NET 8 Desktop Runtime (no install needed)
- **Lite build** — small EXE for machines that already have the runtime
- Fixed: app shutdown could hang when OBS sources were still connected
- Fixed: malformed BLE heart rate packets could crash the app
- Fixed: Scan button stuck disabled after unexpected Bluetooth stop
- Fixed: port conflict on startup no longer kills the app

**v1.0.0**
- Initial release: single-watch BLE heart rate overlay for OBS

---

## Building from Source

Requires **.NET 8 SDK** ([download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)).

```powershell
# Debug build + run
cd HuaweiHROverlay
dotnet run

# Release single-file EXE with bundled .NET runtime (no install needed on target)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
# Output: ./publish/HuaweiHROverlay.exe (~150 MB)

# Small EXE (~2 MB) for machines that already have the .NET 8 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish
```

---

## Troubleshooting

**Watch not appearing in scan:**
- Ensure **HR Data Broadcast** is enabled on the watch (it can time out — re-enable if needed)
- Click **Stop** then **Scan** again
- Try toggling Bluetooth off/on on the PC

**"Heart Rate Service not found":**
- HR Data Broadcast may have timed out — go back to watch Settings and re-enable it
- The watch may not support HR Data Broadcast (check your model)

**OBS overlay shows "--":**
- Ensure the app is running and BPM is showing in the control panel
- Check the URL is exactly `http://localhost:8764/` (no HTTPS)
- Click **Refresh** on the OBS Browser Source

**Windows Firewall prompt:**
- Allow the app on Private networks — it only uses `localhost`, never the internet

---

## Architecture

```
HuaweiHROverlay/
├── Core/
│   ├── BleHeartRateService.cs   # BLE scanner + multi-device session manager (one session per watch)
│   ├── WebSocketServer.cs       # WS broadcast server, per-device subscriptions (port 8765)
│   ├── HttpOverlayServer.cs     # HTTP server for overlay + status JSON (port 8764)
│   └── HeartRateReading.cs      # Data model
├── Assets/
│   ├── overlay.html             # Animated OBS overlay (embedded in EXE)
│   └── heart.ico                # App icon
├── Resources/
│   └── Styles.xaml              # WPF dark theme
├── MainWindow.xaml              # Control panel UI
└── MainWindow.xaml.cs           # App logic
```
