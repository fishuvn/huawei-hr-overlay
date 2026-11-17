# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Live streamers and content creators on Windows 10/11, Linux (Debian/Ubuntu, Arch Linux, Fedora), and macOS who wear a Huawei Watch, Huawei Band, or standard Bluetooth LE heart rate monitor while broadcasting in OBS Studio.

## Product Purpose

Streams real-time heart rate telemetry directly from Bluetooth LE wearables (GATT Heart Rate Service `0x180D` / Characteristic `0x2A37`) into OBS Studio via a local HTTP + WebSocket server, without requiring the Huawei SDK, cloud accounts, or a paired smartphone bridge.

## Positioning

Zero-account, local-only cross-platform desktop + OBS broadcast bridge that turns Huawei's built-in "HR Data Broadcast" mode into a customizable, multi-watch stream overlay with automatic reconnection and one-click GitHub updates.

## Operating Context

- Desktop companion app runs alongside OBS Studio and full-screen games or creative software on Windows, Linux (BlueZ over D-Bus), and macOS (CoreBluetooth).
- OBS Studio loads `http://localhost:8764/` (or per-device `/overlay/<ID>`) in a 400×120 transparent Browser Source layered directly over live video.
- Co-op and multi-player streams connect multiple watches simultaneously, routing each device to its own Browser Source URL.

## Capabilities and Constraints

- Reads standard BLE GATT `0x180D` / `0x2A37` notifications (8-bit and 16-bit BPM formats) across Windows WinRT, Linux BlueZ, and macOS CoreBluetooth.
- Serves embedded OBS overlay HTML on `http://localhost:8764/` and streams real-time JSON frames over `ws://localhost:8765/`.
- Includes Simulator mode (`SIM`) for testing OBS scene layouts without wearing a watch.
- Checks `fishuvn/huawei-hr-overlay` GitHub Releases for platform-matched updates verified with SHA-256 checksums.
- Overlay must remain legible at compact broadcast sizes (downscaled 720p/1080p video) with tabular numerals, zero layout shift, and transparent compositing.

## Evidence on Hand

- Repository source under `HuaweiHROverlay/`, embedded `HuaweiHROverlay/Assets/overlay.html`, and cross-platform packaging scripts under `packaging/`.
- Confirmed community demand for Linux (Debian 13 Trixie, Arch Linux, Fedora) and macOS alongside Windows 10/11 (GitHub Issue #1).

## Product Principles

1. **Instant broadcast legibility:** Heart rate numerals, zone status, and cardiac trace must read clearly at a glance over bright or dark gameplay without jitter.
2. **Zero-cloud local reliability:** Everything runs on `localhost` with automatic BLE signal recovery when a wearer briefly leaves Bluetooth range.
3. **One-binary simplicity per OS:** Native packages and portable builds on Windows, Debian/Arch/Fedora, and macOS work without cloud sign-ins or manual runtime hunting.
4. **Restrained telemetry craft:** Instrument-grade hierarchy, crisp vector iconography, directional elevation, and genuine tabular data typography over decorative clutter.

## Accessibility & Inclusion

- WCAG AA contrast (`>= 4.5:1` for labels and `>= 3:1` for display numerals) on overlay card surfaces.
- Functional UI labels at or above `11px` for high-DPI and downscaled stream legibility, plus `prefers-reduced-motion` support.
