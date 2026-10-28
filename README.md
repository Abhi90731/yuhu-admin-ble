# YUHU Admin BLE + Dashboard (unified)

This repo contains the **Windows BLE admin tooling** for the YUHU mesh, in two
parts that work together:

| Folder | What | Language | Runs |
|--------|------|----------|------|
| (root) | BLE admin **sidecar** — scans/connects to phones over custom BLE, authenticates as the super-user admin, pulls logs | C# / WinRT | Windows |
| `dashboard/` | Browser **dashboard UI** — adb logs + the BLE admin panel (buttons, no console typing) | Node + JS | Windows/macOS |
| `tools/` | Offline cert minting (root key + admin certs) | Node | secure/offline |

## The full picture

```
Browser Dashboard UI  (dashboard/, http://localhost:4317)
     ↕ WebSocket
Node dashboard server ──ws://127.0.0.1:4318──► C# BLE sidecar (root, --bridge)
     ↕ adb                                           ↕ custom BLE
  cabled phones                                  admin-enabled phones
```

The dashboard shows **two** log sources in one UI:
- **adb logs** from cabled / wifi-adb devices (always works)
- **BLE admin logs** pulled cable-free from admin-enabled phones (via the sidecar)

## Run it on Windows (two terminals)

**Terminal 1 — the BLE sidecar (headless):**
```powershell
cd yuhu-admin-ble
dotnet run -- --bridge
```
(First run without `--bridge` once, to generate the admin key + place the cert —
see "Admin setup" below.)

**Terminal 2 — the dashboard UI:**
```powershell
cd yuhu-admin-ble\dashboard
npm install
npm start
```
Open <http://localhost:4317>. The **BLE Admin** panel shows discovered phones;
click **connect**, then use **State / Detail / Exceptions / Live / System**
buttons — no typing.

## Admin setup (one-time)

1. `dotnet run` (no `--bridge`) → prints the admin public key, errors for a cert.
2. Mint a cert offline: `node tools/mint-admin-cert.js --rootPriv <root-private.key> --adminPub <printed key> --out admin-identity/admin-cert.json`
   (or mint on another machine and copy the JSON into `admin-identity/`).
3. Re-run — `[admin] identity loaded; cert present.`

## Standalone console mode

The sidecar also runs as an interactive console (without `--bridge`) for
debugging: `dotnet run` → connect by index → `state` / `detail [min]` / `exc` /
`live` / `sys`.

---

# A.3.0 — Windows central BLE spike (original notes)


A minimal **Windows** console app that proves the laptop can talk to a YUHU
phone over the **custom BLE** protocol as a **BLE central** (laptop scans and
connects outward). This is the de-risking spike for the Windows admin node.

**What it does:**
- Scans for the frozen YUHU Service UUID (`0da35ffe-f05d-4824-a6ad-0f6a1277c82b`)
- Lists discovered devices (name, logical id, address, RSSI)
- Connects to one, resolves the TX/RX/Identity/Capability characteristics
- Subscribes to TX notifications, reads the Identity characteristic
- Runs the keepalive PING loop (2s) and link-dead timeout (6s)

**What it deliberately does NOT do (yet):**
- No advertising (central-only; peripheral is a future extension behind the same
  `IBleTransport` interface)
- No admin auth / super-user challenge
- No Noise handshake
- No dashboard integration

Those come in the full A.3 once this spike confirms Windows BLE works.

## Requirements (Windows machine)

- **Windows 10 (2004 / build 19041) or Windows 11** with a BLE adapter.
- **.NET 8 SDK or newer** — <https://dotnet.microsoft.com/download>
- Bluetooth turned ON. First run may prompt for Bluetooth permission
  (Settings → Privacy → Bluetooth → allow desktop apps).
- A YUHU phone running with BLE transport active and advertising.

## Build & run (Windows)

```powershell
cd YUHU-admin-ble
dotnet run
```

If your installed Windows SDK differs, edit `TargetFramework` in
`YuhuAdminBle.csproj` (min `net8.0-windows10.0.17763.0`).

### Using the spike

```
Scanning… commands: <number> connect | r rescan list | q quit

  [0] Alice id=46b3a5... addr=AA:BB:CC:DD:EE:FF rssi=-54
  [1] (no name) id=? addr=11:22:33:44:55:66 rssi=-78

0            <- type the index to connect to device [0]
r            <- reprint the discovered list
q            <- quit
```

On a successful connect you'll see `>>> CONNECTED`, the `[identity]` string,
the TX subscribe status, and keepalive activity.

## Success criteria (what "the spike worked" means)

1. Your YUHU phone appears in the scan list (by Service UUID), ideally with its
   name + logical id from the advert.
2. `dotnet run` → connect by index → `>>> CONNECTED`.
3. TX subscribe returns `Success`; the Identity read prints
   `<base64 pubkey>|<name>`.
4. The link stays up (keepalive) rather than immediately dropping.

If all four hold, Windows BLE central works against a real phone and we proceed
to full A.3 (admin challenge + direct-peer log streaming). If it fails, we learn
the exact Windows constraint cheaply.

## A.3 — Admin direct-peer logs (authenticated)

Once the spike connects, the admin workflow adds authenticated log access:

### One-time setup (per admin laptop)
1. Run the app once; it generates an admin keypair in `admin-identity/admin-key.dpapi`
   (DPAPI-encrypted, bound to your Windows user) and prints the admin PUBLIC key.
2. Offline, mint a root-signed cert for that public key:
   ```bash
   node tools/mint-admin-cert.js --rootPriv /offline/root-private.key \
     --adminPub <printed admin public key> --label admin-laptop-1 --days 365 \
     --out admin-identity/admin-cert.json
   ```
3. Re-run the app; it loads the cert.

### Using it
With a phone built with `admin.enabled=true` and `transport.impl=ble`:
```
<index>      connect to the phone
auth         run the admin handshake (HELLO -> CHALLENGE -> PROOF -> OK)
logs         pull a log tail
logs MeshFlow  pull a tail filtered to the MeshFlow tag
live         start live log streaming
stop         stop live streaming
```

A successful `auth` prints `>>> ADMIN AUTHENTICATED`. `logs` then returns the
phone's recent log lines over the direct BLE link — no cable. The phone only
honors this if its cert verifies AND `admin.enabled` is on; otherwise it drops
the admin frames.

## Tests (cross-platform, run anywhere)

The framing logic is pure .NET (no WinRT) and verifies the interop **golden
vector** against the Android `BleFraming.kt`:

```bash
cd tests
dotnet test
```

(Target `net10.0` locally, or `net8.0` on the Windows box — adjust
`tests/YuhuAdminBle.Tests.csproj` TargetFramework to a runtime you have.)

## Project layout

| File | Role |
|------|------|
| `Protocol.cs` | Frozen UUIDs + 9-byte frame header constants (platform-neutral) |
| `BleFraming.cs` | Split/reassemble chunks, golden-vector compatible (platform-neutral) |
| `IBleTransport.cs` | Role-agnostic transport interface (central now, peripheral/multi-laptop later) |
| `WinRtCentralTransport.cs` | WinRT central implementation (scan/connect/subscribe/keepalive) |
| `Program.cs` | Interactive spike driver |
| `tests/` | Cross-platform framing tests (golden vector) |

## Known Windows BLE caveats to watch for

- **Permission prompt:** if scanning returns nothing, check Bluetooth privacy
  settings for desktop apps.
- **Pairing/bonding:** some phones require the devices be bonded before GATT
  reads succeed; if Identity read fails with `AccessDenied`, pair the phone with
  the laptop once in Windows Bluetooth settings.
- **Active scan needed for name:** the advertised name/id live in the scan
  response, so the watcher uses Active scanning.
