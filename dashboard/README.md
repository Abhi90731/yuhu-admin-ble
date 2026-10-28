# YUHU Mesh Dashboard

A local, real-time dashboard + admin control plane for the YUHU BLE mesh
(`com.projectx.mesh`). It reads `adb logcat` from attached devices, parses the
privacy-safe mesh logs, and shows live mesh state in a browser. Runs on
**macOS and Windows**.

> **Scope (current):** Phase 1 + 2.
> - Discover devices (cabled via adb; mesh-only discovery needs the app-side
>   `adm` message type — see "Mesh-only discovery" below).
> - View per-device logs + **live** log streaming.
> - **Exceptions panel** — devices explicitly push crashes/exceptions (Part B).
> - **Admin mode** behind a flag (off = strictly read-only).
>
> **Deferred (Phase 3):** remote control / pushing updates to devices.

## Requirements

- **Node.js 18+** (tested on v24).
- **adb** (Android platform-tools) with at least one device connected and USB
  debugging enabled.

## Setup & run

```bash
cd YUHU-dashboard
npm install
npm start
```

Then open the printed URL (default <http://localhost:4317>).

### Admin mode

Admin mode is OFF by default (read-only dashboard). Enable it with an env flag:

```bash
# macOS / Linux
YUHU_ADMIN=1 npm start

# Windows (PowerShell)
$env:YUHU_ADMIN=1; npm start
```

When ON, the header shows an **ADMIN** badge and the "Discover (ping sweep)"
button unlocks.

## adb path (cross-platform)

The server auto-detects adb:

1. `YUHU_ADB` env var (explicit override), else
2. OS default SDK location:
   - macOS: `~/Library/Android/sdk/platform-tools/adb`
   - Windows: `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`
   - Linux: `~/Android/Sdk/platform-tools/adb`
3. bare `adb` on `PATH`.

Override example:

```bash
YUHU_ADB="/custom/path/to/adb" npm start
```

## What you see

- **Devices** — all adb-attached devices, online/offline, with a "view logs"
  button (one-shot buffer dump).
- **Connection Health** — per device: Bluetooth on/off, advertising, last BLE
  status code (e.g. 62 / 133 / 0).
- **Mesh Graph** — nodes = devices/mesh ids, edges = live links, colored by
  `On P2P` (blue) vs `On Mesh` (amber).
- **Exceptions** — crashes/exceptions the app pushes via the `MeshAdmin` log tag
  (Part B).
- **Message-Flow Timeline** — live SEND / RECV / RELAY / DELIVERED / DEDUP-DROP,
  metadata only (there is no message content in these logs).

## Mesh-only discovery (requires the app-side change)

adb only sees devices **cabled to this laptop**. To discover devices reachable
only **over the mesh**, a cabled "gateway" phone sends a mesh **PING** and
collects **PONG** replies. That requires the small app-side `adm` message type +
`AdminService`, being built in the `ProjectX-admin` git worktree
(`feature/admin-control-plane`). Until it lands, cabled devices are discovered
automatically and mesh-only ping-sweep returns a "pending" note.

## Part B — device-reported exceptions

The app logs exceptions to the **`MeshAdmin`** tag in this format:

```
MeshAdmin: EXCEPTION {"type":"...","message":"...","thread":"...","ts":...}
```

The dashboard parses these into the Exceptions panel in real time. (The app-side
hook that writes these lines lives in the `ProjectX-admin` worktree.)

## Privacy

Metadata only — ids, links, seq numbers, status codes, exception metadata.
Message **content** is never in these logs and is never shown.

## Read-only guarantee

This tool only runs `adb devices` and `adb logcat`. It never installs,
force-stops, or otherwise mutates phones, so it won't disrupt other test
sessions driving the same devices.

## Tests

```bash
npm test
```

## Windows notes

- Install Android platform-tools; ensure `adb` works in a terminal first
  (`adb devices`).
- Some Vivo/Funtouch devices suppress app logs; full-fidelity capture may need
  `--pid` filtering. The dashboard still shows whatever logcat emits.
