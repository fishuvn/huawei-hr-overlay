# Huawei HR Overlay for OBS

Real-time heart rate overlay for OBS Studio, reading data directly from your **Huawei Watch or Smartband** via Bluetooth LE — no Huawei SDK, no cloud, no phone required.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET%208-Runtime%20Required-purple)
![OBS](https://img.shields.io/badge/OBS-Browser%20Source-orange)

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

> [!IMPORTANT]
> **You must install the .NET 8 Desktop Runtime** before running `HuaweiHROverlay.exe`.
> The app does **not** bundle the runtime — this keeps the EXE small (~2 MB).

| Requirement | Download |
|-------------|---------|
| **.NET 8 Desktop Runtime** (x64) | **[Download from Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime)** → _Run desktop apps_ → _Windows x64_ |
| Windows 10 (build 19041+) or Windows 11 | — |
| OBS Studio | [obsproject.com](https://obsproject.com) |
| Huawei Watch/Band with HR Data Broadcast | Most GT2/GT3/GT4, Band 6+, Watch Fit series |

### Quick .NET 8 Runtime Install (PowerShell)
```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

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

## Step 4 — Connect to your watch

1. Click **▶ Scan** — your watch should appear within 5–10 seconds
2. Select it from the list
3. Click **⚡ Connect**
4. Live BPM will update in the control panel

---

## Step 5 — Add OBS Browser Source

1. In OBS, click **+** in the Sources panel → **Browser**
2. Set URL to: `http://localhost:8764/`
3. Width: **400**, Height: **120**
4. **"Shutdown source when not visible"** → OFF
5. Click **OK**

The animated heart rate widget appears on your stream!

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

- Huawei Watch GT2 / GT3 / GT3 Pro / GT4
- Huawei Watch Fit / Fit 2 / Fit 3
- Huawei Band 6 / 7 / 8 / 9
- Any standard BLE HR chest strap (Polar H10, Wahoo TICKR, etc.)

---

## Ports Used

| Service | Address |
|---------|---------|
| Overlay page (OBS Browser Source) | `http://localhost:8764/` |
| WebSocket BPM stream | `ws://localhost:8765/` |
| Status JSON | `http://localhost:8764/status` |

---

## Building from Source

Requires **.NET 8 SDK** ([download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)).

```powershell
# Debug build + run
cd HuaweiHROverlay
dotnet run

# Release single-file EXE (requires .NET 8 Runtime on target machine)
dotnet publish -c Release -o ./publish
# Output: ./publish/HuaweiHROverlay.exe
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
│   ├── BleHeartRateService.cs   # BLE advertisement scanner + GATT reader
│   ├── WebSocketServer.cs       # WS broadcast server (port 8765)
│   ├── HttpOverlayServer.cs     # HTTP server for overlay (port 8764)
│   └── HeartRateReading.cs      # Data model
├── Assets/
│   ├── overlay.html             # Animated OBS overlay (embedded in EXE)
│   └── heart.ico                # App icon
├── Resources/
│   └── Styles.xaml              # WPF dark theme
├── MainWindow.xaml              # Control panel UI
└── MainWindow.xaml.cs           # App logic
```
